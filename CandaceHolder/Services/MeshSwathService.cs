using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CandaceHolder.Services
{
    /// <summary>
    /// Phase 2 hail visualization: true radar-derived MRMS MESH (Maximum Estimated
    /// Size of Hail) swaths, as size-banded polygons — the same shape contract as
    /// <see cref="RealDataService.GetMrmsHailSwathGeoJsonAsync"/> (the LSR-based
    /// Phase 1 swath) so the frontend can style either source with the same
    /// <c>swathColor()</c>/legend in storm-explorer.js.
    ///
    /// Pipeline: locate the daily-max MESH_Max_1440min GRIB2 for the requested
    /// UTC date on NOAA AWS OpenData (Nov 2020 → present) or the IEM MTArchive
    /// (Oct 2014 → present), download + decompress it, clip to the requested
    /// bbox with <c>gdal_translate</c>, then run <c>gdal_contour -p</c> to
    /// produce size-banded polygons. Both the raw grib (per date) and the
    /// final GeoJSON (per date+bbox) are cached under the Fly.io data volume
    /// so repeat map/report views don't reprocess.
    ///
    /// ── NOT VERIFIED AGAINST LIVE DATA ──
    /// This was written in a sandbox with no GDAL, no wgrib2, and no path to
    /// actually hit NOAA AWS / IEM and confirm a real download+contour run.
    /// Before flipping FeatureFlags:MeshSwaths on, run /RoofHealth/MeshDebug
    /// against a known hail date/location on a machine with GDAL installed and
    /// read the step-by-step output — see docs/mesh-phase2-handoff.md for the
    /// verification checklist. Also note: IEM's own MRMS curation effort hit
    /// real gdal_contour performance problems on this exact product
    /// (github.com/akrherz/iem issue #253, "Curate MRMS MESH Hail Contours") —
    /// budget time to tune timeouts/levels, don't assume first-run correctness.
    /// </summary>
    public class MeshSwathService
    {
        private readonly IHttpClientFactory        _httpFactory;
        private readonly ILogger<MeshSwathService> _logger;
        private readonly string                    _cacheDir;
        private readonly string                    _awsBaseUrl;
        private readonly string                    _iemBaseUrl;
        private readonly string                    _gdalTranslatePath;
        private readonly string                    _gdalContourPath;
        private readonly TimeSpan                  _processTimeout;

        private const string OutputSchemaVersion     = "v2"; // bump when ReshapeToAppSchema's output shape changes

        // Storm Explorer now defaults to *every* date in the selected period
        // being "on", which can fire dozens of simultaneous MeshSwath requests
        // from one page load. Each cache-miss request downloads a grib and
        // runs two GDAL subprocesses — cheap one at a time, but the Fly.io box
        // (1 shared CPU / 1GB) OOMs and 502s if too many run concurrently.
        // This caps how many grib-download+GDAL pipelines run at once
        // app-wide; extra requests queue here instead of piling onto the box.
        // Cache hits (the common case after the first view of a date) skip
        // this gate entirely — see GetMeshSwathGeoJsonAsync.
        private static readonly SemaphoreSlim PipelineGate = new(2, 2);

        // MESH switches from IEM MTArchive to NOAA AWS OpenData at this date —
        // NOAA did not backfill Sep 2019–Oct 2020 to AWS (pre a major MRMS
        // upgrade), so that window only exists via IEM. IEM's MTArchive covers
        // back to Oct 2014 (NCEP implementation).
        // Source: mesonet.agron.iastate.edu/archive/mrms.php
        private static readonly DateTime AwsArchiveStart = new(2020, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime IemArchiveStart  = new(2014, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        // Same size bands as the Phase 1 LSR swath (RealDataService), in inches.
        private static readonly double[] SizeBandsInches = { 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 };

        public MeshSwathService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<MeshSwathService> logger, IWebHostEnvironment env)
        {
            _httpFactory = httpFactory;
            _logger      = logger;

            // App_Data (project root, survives bin/obj cleans), not BaseDirectory
            // (bin/Debug/net8.0) — see Program.cs's App_Data comment. This cache
            // is regenerable either way, but keeping it alongside the other
            // App_Data folders avoids yet another data/ vs Data/ collision.
            var defaultCacheDir = Path.Combine(env.ContentRootPath, "App_Data", "mesh-cache");
            _cacheDir = string.IsNullOrWhiteSpace(config["Mesh:CacheDir"]) ? defaultCacheDir : config["Mesh:CacheDir"]!;
            Directory.CreateDirectory(_cacheDir);

            _awsBaseUrl        = config["Mesh:AwsBaseUrl"]        ?? "https://noaa-mrms-pds.s3.amazonaws.com";
            _iemBaseUrl        = config["Mesh:IemArchiveBaseUrl"] ?? "https://mtarchive.geol.iastate.edu";
            _gdalTranslatePath = config["Mesh:GdalTranslatePath"] ?? "gdal_translate";
            _gdalContourPath   = config["Mesh:GdalContourPath"]   ?? "gdal_contour";
            _processTimeout    = TimeSpan.FromSeconds(config.GetValue<int?>("Mesh:ProcessTimeoutSeconds") ?? 90);
        }

        /// <summary>
        /// Returns size-banded MESH swath polygons as a GeoJSON FeatureCollection
        /// string for the given bbox + UTC date. Returns an empty FeatureCollection
        /// (never throws) on any failure — caller/frontend treats that identically
        /// to "no swath for this view."
        /// </summary>
        public async Task<string> GetMeshSwathGeoJsonAsync(
            double minLat, double maxLat, double minLng, double maxLng,
            DateTime dateUtc, MeshDebugSink? debug = null)
        {
            var result = await GetMeshSwathCoreAsync(minLat, maxLat, minLng, maxLng, dateUtc, debug);
            return result.GeoJson;
        }

        /// <summary>
        /// Core pipeline, split out from <see cref="GetMeshSwathGeoJsonAsync"/> so callers that
        /// need to tell "confirmed no hail here" apart from "pipeline failed, unknown" can do so
        /// — see <see cref="GetContainmentAsync"/>, added for per-property report accuracy
        /// (docs/pdf-report-accuracy-punchlist.md item 3). The public GeoJSON method above still
        /// collapses both into an empty FeatureCollection, which is correct for its existing
        /// map-rendering callers (MeshSwath/MeshDebug) — their behavior is unchanged by this.
        /// </summary>
        private async Task<MeshSwathResult> GetMeshSwathCoreAsync(
            double minLat, double maxLat, double minLng, double maxLng,
            DateTime dateUtc, MeshDebugSink? debug)
        {
            dateUtc = dateUtc.Date;
            if (dateUtc < IemArchiveStart || dateUtc > DateTime.UtcNow.Date)
            {
                debug?.Note("date out of supported range (2014-10-01 .. today)");
                return new MeshSwathResult { Success = false, Reason = "date out of supported range (2014-10-01 .. today)" };
            }

            var bboxKey  = $"{minLat:F2}_{maxLat:F2}_{minLng:F2}_{maxLng:F2}";
            var cacheKey = $"{dateUtc:yyyyMMdd}_{bboxKey}";
            // OutputSchemaVersion bumps whenever ReshapeToAppSchema/ContourAsync's
            // *output* format changes (e.g. the below-minimum-band filter added
            // 2026-07-13) — old cached files under the previous name simply won't
            // match and get regenerated, instead of silently serving stale output
            // from before the fix on an already-deployed data volume.
            var geoJsonCachePath = Path.Combine(_cacheDir, $"mesh_{OutputSchemaVersion}_{cacheKey}.geojson");

            if (!(debug?.ForceRefresh ?? false) && File.Exists(geoJsonCachePath))
            {
                debug?.Note($"geojson cache hit: {geoJsonCachePath}");
                return new MeshSwathResult { Success = true, GeoJson = await File.ReadAllTextAsync(geoJsonCachePath) };
            }

            await PipelineGate.WaitAsync();
            debug?.Note($"pipeline gate acquired (slots={PipelineGate.CurrentCount} free after acquire)");
            try
            {
                // Re-check the cache now that we hold a slot — another request
                // for the same date+bbox may have finished processing while we
                // were queued, in which case we can skip straight to its output.
                if (!(debug?.ForceRefresh ?? false) && File.Exists(geoJsonCachePath))
                {
                    debug?.Note($"geojson cache hit after gate wait: {geoJsonCachePath}");
                    return new MeshSwathResult { Success = true, GeoJson = await File.ReadAllTextAsync(geoJsonCachePath) };
                }

                string? gribPath;
                try
                {
                    gribPath = await EnsureGribDownloadedAsync(dateUtc, debug);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MESH grib download failed for {Date}", dateUtc);
                    debug?.Note("download failed: " + ex.Message);
                    return new MeshSwathResult { Success = false, Reason = "download failed: " + ex.Message };
                }

                if (gribPath == null)
                {
                    debug?.Note("no grib resolved for this date — treating as no-data");
                    return new MeshSwathResult { Success = false, Reason = "no grib resolved for this date" };
                }

                MeshSwathResult contourResult;
                try
                {
                    contourResult = await ContourAsync(gribPath, minLat, maxLat, minLng, maxLng, dateUtc, debug);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MESH gdal_contour pipeline failed for {Date}", dateUtc);
                    debug?.Note("contour pipeline failed: " + ex.Message);
                    return new MeshSwathResult { Success = false, Reason = "contour pipeline failed: " + ex.Message };
                }

                if (contourResult.Success)
                {
                    try { await File.WriteAllTextAsync(geoJsonCachePath, contourResult.GeoJson); }
                    catch (Exception ex) { debug?.Note("cache write failed (non-fatal): " + ex.Message); }
                }

                return contourResult;
            }
            finally
            {
                PipelineGate.Release();
            }
        }

        /// <summary>
        /// Answers "does the MESH swath for this date actually cover this exact point" —
        /// the building block for location-specific report accuracy
        /// (docs/pdf-report-accuracy-punchlist.md item 3), as opposed to the existing
        /// 10-mile-radius point-source attribution in LeadsController.Report().
        ///
        /// Three distinct outcomes, because they mean different things to a caller deciding
        /// what to show a customer:
        ///   - Contained      — the point falls inside a real size-banded polygon for that date.
        ///   - ConfirmedClear — the pipeline ran successfully and found no band covering this
        ///                      point. A real negative, not a failure.
        ///   - Unknown        — the pipeline itself failed, or the date is unsupported. Callers
        ///                      must NOT treat this the same as ConfirmedClear — see the
        ///                      punchlist's fail-soft risk note.
        /// </summary>
        public async Task<MeshContainmentResult> GetContainmentAsync(
            double lat, double lng, DateTime dateUtc, MeshDebugSink? debug = null)
        {
            // Small bbox around the single point — enough margin for a contour ring
            // crossing this exact location to be captured. Not a viewport request.
            const double margin = 0.05; // ~3-4 miles
            var core = await GetMeshSwathCoreAsync(lat - margin, lat + margin, lng - margin, lng + margin, dateUtc, debug);

            if (!core.Success)
                return new MeshContainmentResult { Status = MeshContainmentStatus.Unknown, Reason = core.Reason };

            double? bestBandInches = null;
            try
            {
                using var doc = JsonDocument.Parse(core.GeoJson);
                if (doc.RootElement.TryGetProperty("features", out var features))
                {
                    foreach (var f in features.EnumerateArray())
                    {
                        if (!f.TryGetProperty("geometry", out var geom)) continue;
                        if (!PointInGeometry(geom, lng, lat)) continue;

                        double band = 0;
                        if (f.TryGetProperty("properties", out var props) &&
                            props.TryGetProperty("sizeBand", out var sb) &&
                            sb.ValueKind == JsonValueKind.Number)
                        {
                            band = sb.GetDouble();
                        }

                        if (bestBandInches == null || band > bestBandInches) bestBandInches = band;
                    }
                }
            }
            catch (Exception ex)
            {
                return new MeshContainmentResult { Status = MeshContainmentStatus.Unknown, Reason = "geojson parse failed: " + ex.Message };
            }

            return bestBandInches.HasValue
                ? new MeshContainmentResult { Status = MeshContainmentStatus.Contained, SizeBandInches = bestBandInches }
                : new MeshContainmentResult { Status = MeshContainmentStatus.ConfirmedClear };
        }

        // ── Point-in-polygon (ray casting) against gdal_contour's GeoJSON output ──────────

        private static bool PointInGeometry(JsonElement geometry, double lng, double lat)
        {
            if (!geometry.TryGetProperty("type", out var typeEl) ||
                !geometry.TryGetProperty("coordinates", out var coords))
                return false;

            return typeEl.GetString() switch
            {
                "Polygon"      => PointInPolygonRings(coords, lng, lat),
                "MultiPolygon" => coords.EnumerateArray().Any(poly => PointInPolygonRings(poly, lng, lat)),
                _              => false
            };
        }

        // rings[0] = outer boundary, rings[1..] = holes to subtract — standard GeoJSON polygon shape.
        private static bool PointInPolygonRings(JsonElement rings, double lng, double lat)
        {
            bool inside  = false;
            bool isOuter = true;
            foreach (var ring in rings.EnumerateArray())
            {
                bool ringHit = PointInRing(ring, lng, lat);
                if (isOuter) { inside = ringHit; isOuter = false; }
                else if (ringHit) { inside = false; } // point falls inside a hole
            }
            return inside;
        }

        // Standard even-odd ray-casting point-in-polygon test.
        private static bool PointInRing(JsonElement ring, double lng, double lat)
        {
            // Each coordinate is itself a JSON array [lng, lat] — JsonElement has no []
            // indexer, so materialize it to a real array first before pulling [0]/[1].
            var pts = ring.EnumerateArray()
                .Select(coord =>
                {
                    var xy = coord.EnumerateArray().ToArray();
                    return (x: xy[0].GetDouble(), y: xy[1].GetDouble());
                })
                .ToArray();

            bool inside = false;
            for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
            {
                var (xi, yi) = pts[i];
                var (xj, yj) = pts[j];
                bool crosses = ((yi > lat) != (yj > lat)) &&
                    (lng < (xj - xi) * (lat - yi) / (yj - yi) + xi);
                if (crosses) inside = !inside;
            }
            return inside;
        }

        // ── Locate + download the daily-max MESH grib for a UTC date ──────────

        private async Task<string?> EnsureGribDownloadedAsync(DateTime dateUtc, MeshDebugSink? debug)
        {
            var rawCachePath = Path.Combine(_cacheDir, $"raw_{dateUtc:yyyyMMdd}.grib2");
            if (!(debug?.ForceRefresh ?? false) && File.Exists(rawCachePath))
            {
                debug?.Note($"grib cache hit: {rawCachePath}");
                return rawCachePath;
            }

            var client = _httpFactory.CreateClient("mesh");

            string? gzUrl = dateUtc >= AwsArchiveStart
                ? await ResolveAwsKeyAsync(client, dateUtc, debug)
                : ResolveIemUrl(dateUtc, debug);

            if (gzUrl == null) return null;

            debug?.Note($"downloading {gzUrl}");
            var gzBytes = await client.GetByteArrayAsync(gzUrl);
            debug?.Note($"downloaded {gzBytes.Length} bytes");

            var gzPath = rawCachePath + ".gz";
            await File.WriteAllBytesAsync(gzPath, gzBytes);

            using (var inStream  = File.OpenRead(gzPath))
            using (var gzip      = new System.IO.Compression.GZipStream(inStream, System.IO.Compression.CompressionMode.Decompress))
            using (var outStream = File.Create(rawCachePath))
            {
                await gzip.CopyToAsync(outStream);
            }
            try { File.Delete(gzPath); } catch { /* best effort */ }

            return rawCachePath;
        }

        /// <summary>
        /// NOAA AWS OpenData: MESH object filenames carry an ad-hoc timestamp
        /// (not a fixed HHMMSS), so this lists the date's S3 "folder" via the
        /// public ListObjectsV2 REST API and picks the file, rather than
        /// guessing the exact key. This exact timestamping problem is documented
        /// by IEM's own MRMS curation work: github.com/akrherz/iem issue #253.
        /// </summary>
        private async Task<string?> ResolveAwsKeyAsync(HttpClient client, DateTime dateUtc, MeshDebugSink? debug)
        {
            var prefix  = $"CONUS/MESH_Max_1440min_00.50/{dateUtc:yyyyMMdd}/";
            var listUrl = $"{_awsBaseUrl}/?list-type=2&prefix={Uri.EscapeDataString(prefix)}";

            string xml;
            try
            {
                xml = await client.GetStringAsync(listUrl);
            }
            catch (Exception ex)
            {
                debug?.Note("AWS ListObjectsV2 failed: " + ex.Message);
                return null;
            }

            var keys = System.Text.RegularExpressions.Regex.Matches(xml, "<Key>([^<]+)</Key>")
                .Select(m => m.Groups[1].Value)
                .Where(k => k.EndsWith(".grib2.gz", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.Ordinal) // filenames are UTC-timestamped -> lexical order == chronological
                .ToList();

            debug?.Note($"AWS prefix '{prefix}' -> {keys.Count} candidate object(s)");
            if (keys.Count == 0) return null;

            // Daily-max product is produced ~once/day; the latest-timestamped
            // object for the date is the authoritative one.
            return $"{_awsBaseUrl}/{keys[^1]}";
        }

        /// <summary>
        /// IEM MTArchive fallback for dates before the AWS archive (back to
        /// Oct 2014). UNVERIFIED — MTArchive is an HTML directory index, not an
        /// S3 API, so unlike ResolveAwsKeyAsync this can't list-and-pick; it
        /// guesses a filename pattern that needs confirming against a real
        /// directory listing at
        /// https://mtarchive.geol.iastate.edu/{yyyy}/{MM}/{dd}/mrms/ncep/MESH_Max_1440min/
        /// before this path is trusted. Flag pre-Nov-2020 report/map requests
        /// for manual QA until that's done.
        /// </summary>
        private string ResolveIemUrl(DateTime dateUtc, MeshDebugSink? debug)
        {
            var url = $"{_iemBaseUrl}/{dateUtc:yyyy}/{dateUtc:MM}/{dateUtc:dd}/mrms/ncep/MESH_Max_1440min/" +
                      $"MRMS_MESH_Max_1440min_00.50_{dateUtc:yyyyMMdd}-120000.grib2.gz";
            debug?.Note("IEM path is a guessed pattern, not list-verified: " + url);
            return url;
        }

        // ── Clip to bbox + contour into size-banded polygons ───────────────────

        private async Task<MeshSwathResult> ContourAsync(
            string gribPath, double minLat, double maxLat, double minLng, double maxLng,
            DateTime dateUtc, MeshDebugSink? debug)
        {
            var tmpTif      = Path.Combine(Path.GetTempPath(), $"mesh_{Guid.NewGuid():N}.tif");
            var tmpGeoJson  = Path.Combine(Path.GetTempPath(), $"mesh_{Guid.NewGuid():N}.geojson");
            var ci          = CultureInfo.InvariantCulture;

            try
            {
                // 1. Clip to bbox with a small margin so contour rings aren't
                //    truncated right at the viewport edge.
                const double margin = 0.1;
                var translateArgs =
                    $"-projwin {(minLng - margin).ToString(ci)} {(maxLat + margin).ToString(ci)} " +
                    $"{(maxLng + margin).ToString(ci)} {(minLat - margin).ToString(ci)} " +
                    $"-of GTiff \"{gribPath}\" \"{tmpTif}\"";

                var (transOk, transOut) = await RunProcessAsync(_gdalTranslatePath, translateArgs);
                debug?.Note($"gdal_translate exit_ok={transOk}: {Truncate(transOut, 500)}");
                if (!transOk || !File.Exists(tmpTif))
                    return new MeshSwathResult { Success = false, Reason = "gdal_translate failed: " + Truncate(transOut, 300) };

                // 2. Contour in polygon mode (-p) at each size band. MESH grib
                //    units are millimeters, so convert the inch bands -> mm for
                //    the -fl level list; -amin names the output field carrying
                //    each band's lower bound.
                var levelsMm = string.Join(" ", SizeBandsInches.Select(b => (b * 25.4).ToString("F1", ci)));
                var contourArgs = $"-p -amin sizeBandMm -f GeoJSON -fl {levelsMm} \"{tmpTif}\" \"{tmpGeoJson}\"";

                var (contOk, contOut) = await RunProcessAsync(_gdalContourPath, contourArgs);
                debug?.Note($"gdal_contour exit_ok={contOk}: {Truncate(contOut, 500)}");
                if (!contOk || !File.Exists(tmpGeoJson))
                    return new MeshSwathResult { Success = false, Reason = "gdal_contour failed: " + Truncate(contOut, 300) };

                var raw = await File.ReadAllTextAsync(tmpGeoJson);
                return new MeshSwathResult { Success = true, GeoJson = ReshapeToAppSchema(raw, dateUtc) };
            }
            finally
            {
                try { if (File.Exists(tmpTif))     File.Delete(tmpTif); }     catch { /* best effort */ }
                try { if (File.Exists(tmpGeoJson)) File.Delete(tmpGeoJson); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Maps gdal_contour's <c>sizeBandMm</c> field back to the
        /// { sizeBand (inches), date, source } property shape used by the
        /// Phase 1 LSR swath (RealDataService.GetMrmsHailSwathGeoJsonAsync),
        /// so storm-explorer.js can style both sources with the same
        /// swathColor()/legend.
        /// </summary>
        private static string ReshapeToAppSchema(string gdalGeoJson, DateTime dateUtc)
        {
            using var doc    = JsonDocument.Parse(gdalGeoJson);
            using var ms     = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);

            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WriteStartArray("features");

            // gdal_contour -p emits one extra ring below the lowest requested
            // -fl level (everything from the raster's actual minimum up to
            // that level) — not a real hail-size band. At MESH=0 (no hail)
            // that ring typically covers most of the requested bbox, so left
            // unfiltered it painted a large flat wash over the whole map and
            // buried the real bands. Anything below our smallest configured
            // band (0.75") gets dropped here.
            double minBandMm = SizeBandsInches[0] * 25.4;

            if (doc.RootElement.TryGetProperty("features", out var features))
            {
                foreach (var f in features.EnumerateArray())
                {
                    double sizeBandMm = 0;
                    if (f.TryGetProperty("properties", out var props) &&
                        props.TryGetProperty("sizeBandMm", out var mm) &&
                        mm.ValueKind == JsonValueKind.Number)
                    {
                        sizeBandMm = mm.GetDouble();
                    }

                    if (sizeBandMm < minBandMm - 0.01) // small epsilon for float rounding
                        continue;

                    writer.WriteStartObject();
                    writer.WriteString("type", "Feature");

                    if (f.TryGetProperty("geometry", out var geom))
                    {
                        writer.WritePropertyName("geometry");
                        geom.WriteTo(writer);
                    }

                    writer.WriteStartObject("properties");
                    writer.WriteNumber("sizeBand", Math.Round(sizeBandMm / 25.4, 2));
                    writer.WriteString("date", dateUtc.ToString("yyyy-MM-dd"));
                    writer.WriteString("source", "mesh");
                    writer.WriteEndObject(); // properties

                    writer.WriteEndObject(); // feature
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private async Task<(bool ok, string output)> RunProcessAsync(string exe, string args)
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = exe,
                    Arguments              = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                }
            };

            var sb = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(_processTimeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* best effort */ }
                return (false, sb.ToString() + $"\n[TIMEOUT after {_processTimeout.TotalSeconds}s]");
            }

            return (proc.ExitCode == 0, sb.ToString());
        }

        private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "…";
    }

    /// <summary>
    /// Optional diagnostic sink threaded through the MESH pipeline so
    /// /RoofHealth/MeshDebug can surface each step (resolved URL, download
    /// size, gdal exit codes/output) instead of just the final GeoJSON.
    /// Mirrors the existing HailDebug/LsrDebug/RegridDebug dev-diagnostic
    /// pattern already used elsewhere in RoofHealthController.
    /// </summary>
    public class MeshDebugSink
    {
        public bool         ForceRefresh { get; set; }
        public List<string> Notes { get; } = new();
        public void Note(string s) => Notes.Add(s);
    }

    /// <summary>
    /// Internal result of a single MESH pipeline run. Distinguishes "ran successfully" (which
    /// may still carry zero features — a real negative) from "pipeline failed" (download/GDAL
    /// error, unsupported date). The public GetMeshSwathGeoJsonAsync collapses both into a bare
    /// empty FeatureCollection for its existing map-rendering callers; GetContainmentAsync needs
    /// the distinction so a pipeline failure is never mistaken for confirmed-clear.
    /// </summary>
    public class MeshSwathResult
    {
        public bool    Success { get; init; }
        public string  GeoJson { get; init; } = "{\"type\":\"FeatureCollection\",\"features\":[]}";
        public string? Reason  { get; init; }
    }

    public enum MeshContainmentStatus { Contained, ConfirmedClear, Unknown }

    /// <summary>
    /// Result of asking "does this date's MESH swath actually cover this one point" —
    /// see MeshSwathService.GetContainmentAsync.
    /// </summary>
    public class MeshContainmentResult
    {
        public MeshContainmentStatus Status         { get; init; }
        public double?                SizeBandInches { get; init; }
        public string?                Reason         { get; init; }
    }
}
