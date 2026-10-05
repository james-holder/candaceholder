using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace CandaceHolder.Services
{
    /// <summary>
    /// Wraps the three free external data sources:
    ///   1. OpenStreetMap Overpass API  — real nearby addresses   (no key needed)
    ///   2. NOAA SWDI API               — real hail event history  (no key needed)
    ///   3. Regrid Parcel API           — property owner names     (free 25/day token)
    ///
    /// Sign-ups:
    ///   Regrid  → https://app.regrid.com  (free Starter account, 25 lookups/day)
    ///   Then add your token to appsettings.json: "Regrid": { "Token": "YOUR_TOKEN" }
    /// </summary>
    public class RealDataService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration     _config;
        private readonly ILogger<RealDataService> _logger;
        private readonly IMemoryCache       _cache;

        public RealDataService(IHttpClientFactory factory, IConfiguration config, ILogger<RealDataService> logger, IMemoryCache cache)
        {
            _httpFactory = factory;
            _config      = config;
            _logger      = logger;
            _cache       = cache;
        }

        // ─────────────────────────────────────────────────────────────────
        // 1. OpenStreetMap Overpass  –  real residential addresses
        //    No key needed. Rate limit: 1 req/sec recommended.
        //    Docs: https://wiki.openstreetmap.org/wiki/Overpass_API
        // ─────────────────────────────────────────────────────────────────
        public async Task<List<OsmAddress>> GetNearbyAddressesAsync(
            double lat, double lng, double radiusMiles)
        {
            double radiusMeters = radiusMiles * 1609.34;

            // Query for any node/way with a house number — street tag is optional
            // (many US addresses in OSM omit addr:street on the building itself)
            var query = $@"[out:json][timeout:40];
(
  node[""addr:housenumber""](around:{radiusMeters:F0},{lat},{lng});
  way[""addr:housenumber""](around:{radiusMeters:F0},{lat},{lng});
  relation[""addr:housenumber""](around:{radiusMeters:F0},{lat},{lng});
);
out center;";

            try
            {
                using var client  = _httpFactory.CreateClient("overpass");
                var       content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("data", query)
                });

                var resp = await client.PostAsync(
                    "https://overpass-api.de/api/interpreter", content);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Overpass API returned {Status}", resp.StatusCode);
                    return new List<OsmAddress>();
                }

                var json = await resp.Content.ReadAsStringAsync();
                return ParseOverpassAddresses(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Overpass API call failed");
                return new List<OsmAddress>();
            }
        }

        private static List<OsmAddress> ParseOverpassAddresses(string json)
        {
            using var doc  = JsonDocument.Parse(json);
            var       list = new List<OsmAddress>();

            if (!doc.RootElement.TryGetProperty("elements", out var elements))
                return list;

            foreach (var el in elements.EnumerateArray())
            {
                if (!el.TryGetProperty("tags", out var tags))             continue;
                if (!tags.TryGetProperty("addr:housenumber", out var hn)) continue;

                double elLat, elLng;
                var type = el.GetProperty("type").GetString();
                if (type == "node")
                {
                    elLat = el.GetProperty("lat").GetDouble();
                    elLng = el.GetProperty("lon").GetDouble();
                }
                else if (el.TryGetProperty("center", out var center))
                {
                    elLat = center.GetProperty("lat").GetDouble();
                    elLng = center.GetProperty("lon").GetDouble();
                }
                else continue;

                var street = tags.TryGetProperty("addr:street",   out var st) ? st.GetString() ?? "" : "";
                var city   = tags.TryGetProperty("addr:city",     out var c)  ? c.GetString()  ?? "" : "";
                var state  = tags.TryGetProperty("addr:state",    out var s)  ? s.GetString()  ?? "" : "";
                var zip    = tags.TryGetProperty("addr:postcode", out var z)  ? z.GetString()  ?? "" : "";

                // Skip if we can't form a meaningful address
                if (string.IsNullOrEmpty(street) && string.IsNullOrEmpty(city)) continue;

                var addr = string.IsNullOrEmpty(street)
                    ? hn.GetString()!
                    : $"{hn.GetString()} {street}";
                if (!string.IsNullOrEmpty(city))  addr += $", {city}";
                if (!string.IsNullOrEmpty(state)) addr += $", {state}";
                if (!string.IsNullOrEmpty(zip))   addr += $" {zip}";

                list.Add(new OsmAddress
                {
                    FullAddress = addr,
                    HouseNumber = hn.GetString() ?? "",
                    Street      = street,
                    City        = city,
                    State       = state,
                    Lat         = elLat,
                    Lng         = elLng
                });
            }

            return list;
        }

        // ─────────────────────────────────────────────────────────────────
        // 2. NOAA Severe Weather Data Inventory (SWDI)  –  real hail events
        //    No API key needed. Data typically available 120+ days back.
        //    Dataset: nx3hail (NEXRAD Level-3 Hail Signatures)
        //    Docs: https://www.ncei.noaa.gov/products/severe-weather-data-inventory
        //
        //    NOTE: The SWDI API has a 1-year limit per query. We make two calls
        //    to cover the last 5 years of storm history.
        // ─────────────────────────────────────────────────────────────────
        public async Task<List<HailEvent>> GetSwdiHailEventsAsync(
            double lat, double lng, double radiusMiles)
        {
            // Hail events are scored against properties within 2 miles, so the bbox
            // must extend at least (radiusMiles + 2) from center. Use a 5-mile minimum.
            double hailMiles = Math.Max(radiusMiles + 2.0, 5.0);
            double span  = hailMiles / 69.0;
            double west  = Math.Round(lng - span, 4);
            double east  = Math.Round(lng + span, 4);
            double south = Math.Round(lat - span, 4);
            double north = Math.Round(lat + span, 4);

            // SWDI max range = 744 hours (~31 days). Split the 2-year window into
            // 30-day chunks and fetch all in parallel.
            // NOTE: Mesonet LSR covers the 5-year window for Texas — SWDI stays at 2yr
            // to avoid NOAA rate-limiting (60 chunks vs 24 causes timeouts).
            var end   = DateTime.UtcNow.AddDays(-121);
            var start = end.AddYears(-2);

            var windows = new List<(DateTime from, DateTime to)>();
            var cursor = start;
            while (cursor < end)
            {
                var next = cursor.AddDays(90);   // was 30 — 3× fewer NOAA calls (24→8)
                if (next > end) next = end;
                windows.Add((cursor, next));
                cursor = next;
            }

            // Throttle to 4 concurrent requests — NOAA rejects large parallel bursts
            var allEvents = new List<HailEvent>();
            try
            {
                using var semaphore = new System.Threading.SemaphoreSlim(4);
                var tasks = windows.Select(async w =>
                {
                    await semaphore.WaitAsync();
                    try   { return await FetchSwdiBatch(west, south, east, north, w.from, w.to); }
                    finally { semaphore.Release(); }
                });
                var batches = await Task.WhenAll(tasks);
                allEvents.AddRange(batches.SelectMany(b => b));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SWDI parallel fetch failed");
            }

            _logger.LogInformation("SWDI returned {Count} hail events near {Lat},{Lng} ({Windows} windows)",
                allEvents.Count, lat, lng, windows.Count);

            return allEvents;
        }

        private async Task<List<HailEvent>> FetchSwdiBatch(
            double west, double south, double east, double north,
            DateTime from, DateTime to)
        {
            var results = new List<HailEvent>();
            // URL format: /swdiws/json/{dataset}/{startYYYYMMDD}:{endYYYYMMDD}?bbox=W,S,E,N
            var url = $"https://www.ncei.noaa.gov/swdiws/json/nx3hail" +
                      $"/{from:yyyyMMdd}:{to:yyyyMMdd}" +
                      $"?bbox={west},{south},{east},{north}";
            try
            {
                using var client = _httpFactory.CreateClient("noaa");
                var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("SWDI returned {Status} for {Url}", resp.StatusCode, url);
                    return results;
                }

                var json = await resp.Content.ReadAsStringAsync();
                ParseSwdiJson(json, results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SWDI batch fetch failed for {From:yyyyMMdd}:{To:yyyyMMdd}", from, to);
            }
            return results;
        }

        private static void ParseSwdiJson(string json, List<HailEvent> events)
        {
            using var doc = JsonDocument.Parse(json);

            // SWDI response format: { "swdiJsonResponse": {}, "result": [...], "summary": {...} }
            // Also handle legacy: { "data": [...] } or bare [...]
            JsonElement arr;
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("result", out arr))
            { /* current format */ }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                     doc.RootElement.TryGetProperty("data", out arr))
            { /* legacy format */ }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            { arr = doc.RootElement; }
            else return;

            foreach (var item in arr.EnumerateArray())
            {
                // Coordinates come from SHAPE: "POINT (lon lat)"
                double? hLat = null, hLng = null;
                if (item.TryGetProperty("SHAPE", out var shape))
                {
                    var s = shape.GetString() ?? "";
                    // Format: "POINT (lon lat)"
                    var inner = s.Replace("POINT", "").Trim().Trim('(', ')').Trim();
                    var parts = inner.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 &&
                        double.TryParse(parts[0], System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var pLng) &&
                        double.TryParse(parts[1], System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var pLat))
                    {
                        hLng = pLng;
                        hLat = pLat;
                    }
                }

                if (hLat is null || hLng is null) continue;

                // Hail size: MAXSIZE field (inches)
                double? size = GetDoubleField(item, "MAXSIZE");

                // Timestamp: ZTIME is ISO 8601 e.g. "2025-04-05T08:29:42Z"
                DateTime date = DateTime.UtcNow.AddYears(-1);
                if (item.TryGetProperty("ZTIME", out var zt))
                    DateTime.TryParse(zt.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out date);

                events.Add(new HailEvent
                {
                    Lat        = hLat.Value,
                    Lng        = hLng.Value,
                    SizeInches = size ?? 0.75,
                    Date       = date
                });
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // 1b. Google Maps Reverse-Geocode Grid  –  fallback when OSM returns 0
        //     Generates a grid of points within the radius, reverse-geocodes each
        //     via Google Maps, deduplicates, and returns residential addresses.
        //     Cost: ~$5 / 1000 calls (~$0.25 for a 50-point grid search).
        // ─────────────────────────────────────────────────────────────────
        public async Task<List<OsmAddress>> GetAddressesViaGoogleGridAsync(
            double centerLat, double centerLng, double radiusMiles, string googleApiKey)
        {
            var addresses = new List<OsmAddress>();
            var seen      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Adaptive grid spacing — target ~50 points regardless of radius
            double radiusMeters  = radiusMiles * 1609.34;
            double spacingMeters = Math.Max(radiusMeters / 5.0, 150.0);
            double latStep = spacingMeters / 111111.0;
            double lngStep = spacingMeters / (111111.0 * Math.Cos(centerLat * Math.PI / 180.0));

            var points = new List<(double lat, double lng)>();
            for (double dLat = -radiusMeters / 111111.0; dLat <= radiusMeters / 111111.0; dLat += latStep)
            for (double dLng = -lngStep * 6;              dLng <= lngStep * 6;              dLng += lngStep)
            {
                var pLat = centerLat + dLat;
                var pLng = centerLng + dLng;
                if (HaversineDistanceMiles(centerLat, centerLng, pLat, pLng) <= radiusMiles)
                    points.Add((pLat, pLng));
            }

            // Throttle to 5 concurrent reverse-geocode calls
            using var sem = new System.Threading.SemaphoreSlim(5);
            var tasks = points.Take(50).Select(async pt =>
            {
                await sem.WaitAsync();
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var url  = $"https://maps.googleapis.com/maps/api/geocode/json" +
                               $"?latlng={pt.lat},{pt.lng}&result_type=street_address&key={googleApiKey}";
                    var json = await client.GetStringAsync(url);
                    return ParseGoogleReverseGeocode(json, pt.lat, pt.lng);
                }
                catch { return null; }
                finally { sem.Release(); }
            });

            var results = await Task.WhenAll(tasks);
            foreach (var addr in results)
            {
                if (addr == null) continue;
                if (!seen.Add(addr.FullAddress)) continue;
                addresses.Add(addr);
            }

            _logger.LogInformation(
                "Google grid fallback: {Count} unique addresses from {Points} points",
                addresses.Count, points.Count);

            return addresses;
        }

        private static OsmAddress? ParseGoogleReverseGeocode(string json, double fallbackLat, double fallbackLng)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("status").GetString() != "OK") return null;

            var results = root.GetProperty("results");
            if (results.GetArrayLength() == 0) return null;

            var first     = results[0];
            var formatted = first.GetProperty("formatted_address").GetString() ?? "";

            // Only residential — must start with a house number
            if (!System.Text.RegularExpressions.Regex.IsMatch(formatted, @"^\d+")) return null;

            var loc    = first.GetProperty("geometry").GetProperty("location");
            var addrLat = loc.GetProperty("lat").GetDouble();
            var addrLng = loc.GetProperty("lng").GetDouble();

            string num = "", street = "", city = "", state = "", zip = "";
            foreach (var comp in first.GetProperty("address_components").EnumerateArray())
            {
                var types = comp.GetProperty("types").EnumerateArray()
                                .Select(t => t.GetString()).ToHashSet();
                var longName  = comp.GetProperty("long_name").GetString()  ?? "";
                var shortName = comp.GetProperty("short_name").GetString() ?? "";

                if (types.Contains("street_number"))              num    = longName;
                else if (types.Contains("route"))                 street = longName;
                else if (types.Contains("locality"))              city   = longName;
                else if (types.Contains("administrative_area_level_1")) state = shortName;
                else if (types.Contains("postal_code"))           zip    = longName;
            }

            return new OsmAddress
            {
                FullAddress = formatted,
                HouseNumber = num,
                Street      = street,
                City        = city,
                State       = state,
                Lat         = addrLat,
                Lng         = addrLng
            };
        }

        // ─────────────────────────────────────────────────────────────────
        // 2b. NOAA Storm Events Database  –  fallback hail data
        //     Ground-truth spotter/insurance reports. No API key needed.
        //     Covers areas where NEXRAD radar has gaps or sparse coverage.
        //     Queries by state, then filters by proximity to search coordinates.
        //     Docs: https://www.ncdc.noaa.gov/stormeventsapi/
        // ─────────────────────────────────────────────────────────────────
        public async Task<List<HailEvent>> GetStormEventsHailAsync(
            double lat, double lng, double radiusMiles, string stateAbbr)
        {
            if (string.IsNullOrEmpty(stateAbbr)) return new List<HailEvent>();

            // Go back 5 years — Storm Events has no radar lag, data is ~60 days current
            var end   = DateTime.UtcNow;
            var start = end.AddYears(-5);

            var url = "https://www.ncdc.noaa.gov/stormeventsapi/rest/data" +
                      $"?format=json&eventType=Hail" +
                      $"&beginDate_mm={start:MM}&beginDate_dd={start:dd}&beginDate_yyyy={start:yyyy}" +
                      $"&endDate_mm={end:MM}&endDate_dd={end:dd}&endDate_yyyy={end:yyyy}" +
                      $"&state={Uri.EscapeDataString(stateAbbr)}" +
                      "&hailSize=0.50&range=50&rangetype=county&limit=500";

            try
            {
                using var client = _httpFactory.CreateClient("noaa");
                var resp = await client.GetAsync(url);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Storm Events API returned {Status} for state {State}",
                        resp.StatusCode, stateAbbr);
                    return new List<HailEvent>();
                }

                var json = await resp.Content.ReadAsStringAsync();
                _logger.LogInformation("Storm Events fallback for {State} — {Len} bytes",
                    stateAbbr, json.Length);

                return ParseStormEventsJson(json, lat, lng, radiusMiles);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Storm Events API call failed for state {State}", stateAbbr);
                return new List<HailEvent>();
            }
        }

        private List<HailEvent> ParseStormEventsJson(
            string json, double centerLat, double centerLng, double radiusMiles)
        {
            var results = new List<HailEvent>();
            // Filter radius: use 3x the search radius to ensure we catch nearby events
            double filterMiles = Math.Max(radiusMiles * 3, 10);

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Response can be { "data": [...] } or just [...]
                JsonElement arr;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("data", out arr)) { }
                else if (root.ValueKind == JsonValueKind.Array)
                    arr = root;
                else return results;

                foreach (var item in arr.EnumerateArray())
                {
                    // Latitude — try multiple field names
                    double? evLat = GetDoubleField(item, "BEGIN_LAT")
                                 ?? GetDoubleField(item, "begin_lat")
                                 ?? GetDoubleField(item, "LAT");
                    double? evLng = GetDoubleField(item, "BEGIN_LON")
                                 ?? GetDoubleField(item, "begin_lon")
                                 ?? GetDoubleField(item, "LON");

                    if (evLat is null || evLng is null) continue;

                    // Distance filter
                    if (HaversineDistanceMiles(centerLat, centerLng,
                            evLat.Value, evLng.Value) > filterMiles) continue;

                    // Hail size — MAGNITUDE field, inches
                    double? size = GetDoubleField(item, "MAGNITUDE")
                                ?? GetDoubleField(item, "magnitude")
                                ?? GetDoubleField(item, "HAIL_SIZE");

                    // Date — BEGIN_DATE_TIME or BEGIN_YEARMONTH
                    DateTime date = DateTime.UtcNow.AddYears(-1);
                    foreach (var dateField in new[] { "BEGIN_DATE_TIME", "begin_date_time", "BEGIN_DATE" })
                    {
                        if (!item.TryGetProperty(dateField, out var dtEl)) continue;
                        var s = dtEl.GetString() ?? "";
                        if (s.Length >= 8 &&
                            DateTime.TryParse(s, out var parsed))
                        { date = parsed; break; }
                    }

                    results.Add(new HailEvent
                    {
                        Lat        = evLat.Value,
                        Lng        = evLng.Value,
                        SizeInches = size ?? 0.75,
                        Date       = date
                    });
                }

                _logger.LogInformation(
                    "Storm Events parsed {Count} hail events within {Miles} miles",
                    results.Count, filterMiles);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Storm Events parse failed");
            }

            return results;
        }

        // ─────────────────────────────────────────────────────────────────
        // 2c. Iowa State Mesonet LSR  –  ground-truth hail spotter reports
        //     No API key needed. Data typically 0–60 min lag (real-time capable).
        //     LSR = Local Storm Reports filed by trained NWS storm spotters.
        //     Covers the last 5 years. Complements SWDI radar data.
        //     Docs: https://mesonet.agron.iastate.edu/lsr/
        //     API:  https://mesonet.agron.iastate.edu/geojson/lsr.php
        // ─────────────────────────────────────────────────────────────────
        public async Task<List<HailEvent>> GetMesonetLsrHailAsync(
            double lat, double lng, double radiusMiles, string stateAbbr = "")
        {
            // The Mesonet LSR API filters by state — no lat/lon proximity param supported.
            // We filter by proximity ourselves in the parser (same pattern as Storm Events).
            // Skip entirely if no state — would pull the entire US.
            if (string.IsNullOrWhiteSpace(stateAbbr)) return new List<HailEvent>();

            // Filter radius — use 3× the search radius to catch nearby events
            double filterMiles = Math.Max(radiusMiles * 3, 10.0);

            var endTime   = DateTime.UtcNow;
            var startTime = endTime.AddYears(-5);

            // Build year-sized windows to avoid timeouts
            var windows = new List<(DateTime from, DateTime to)>();
            var cursor  = startTime;
            while (cursor < endTime)
            {
                var next = cursor.AddYears(1);
                if (next > endTime) next = endTime;
                windows.Add((cursor, next));
                cursor = next;
            }

            var allEvents = new List<HailEvent>();

            // Throttle to 3 concurrent
            using var semaphore = new System.Threading.SemaphoreSlim(3);
            var tasks = windows.Select(async w =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // YYYYMMDDHHII format (UTC) — filter by state + hail type H
                    var sts = w.from.ToString("yyyyMMddHHmm");
                    var ets = w.to.ToString("yyyyMMddHHmm");
                    var url = $"https://mesonet.agron.iastate.edu/geojson/lsr.php" +
                              $"?sts={sts}&ets={ets}" +
                              $"&states={Uri.EscapeDataString(stateAbbr.ToUpper())}" +
                              $"&type=H&fmt=geojson";

                    using var client = _httpFactory.CreateClient("mesonet");
                    var resp = await client.GetAsync(url);
                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Mesonet LSR returned {Status}", resp.StatusCode);
                        return new List<HailEvent>();
                    }
                    var json = await resp.Content.ReadAsStringAsync();
                    return ParseMesonetLsrGeoJson(json, lat, lng, filterMiles);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Mesonet LSR fetch failed");
                    return new List<HailEvent>();
                }
                finally { semaphore.Release(); }
            });

            var batches = await Task.WhenAll(tasks);
            allEvents.AddRange(batches.SelectMany(b => b));

            _logger.LogInformation("Mesonet LSR returned {Count} hail events near {Lat},{Lng} in {State}",
                allEvents.Count, lat, lng, stateAbbr);

            return allEvents;
        }

        private static List<HailEvent> ParseMesonetLsrGeoJson(
            string json, double centerLat, double centerLng, double filterMiles)
        {
            var results = new List<HailEvent>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("features", out var features)) return results;

                foreach (var feature in features.EnumerateArray())
                {
                    if (!feature.TryGetProperty("properties", out var props)) continue;

                    // Filter to hail only — double-check typetext even with type=H filter
                    if (props.TryGetProperty("typetext", out var tt))
                    {
                        var typeText = tt.GetString() ?? "";
                        if (!typeText.Contains("HAIL", StringComparison.OrdinalIgnoreCase)) continue;
                    }

                    // Coordinates from geometry (GeoJSON: [lon, lat])
                    if (!feature.TryGetProperty("geometry", out var geom)) continue;
                    if (!geom.TryGetProperty("coordinates", out var coords)) continue;
                    var coordArr = coords.EnumerateArray().ToArray();
                    if (coordArr.Length < 2) continue;
                    double hLng = coordArr[0].GetDouble();
                    double hLat = coordArr[1].GetDouble();

                    // Proximity filter — only keep events near our search center
                    if (HaversineDistanceMiles(centerLat, centerLng, hLat, hLng) > filterMiles) continue;

                    // Hail size in inches
                    double? size = GetDoubleField(props, "magnitude")
                                ?? GetDoubleField(props, "magf")
                                ?? GetDoubleField(props, "size");

                    // Date from "valid" field (ISO 8601)
                    DateTime date = DateTime.UtcNow.AddYears(-1);
                    if (props.TryGetProperty("valid", out var vt))
                        DateTime.TryParse(vt.GetString(), null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out date);

                    results.Add(new HailEvent
                    {
                        Lat        = hLat,
                        Lng        = hLng,
                        SizeInches = size ?? 0.75,
                        Date       = date,
                        Source     = "lsr"
                    });
                }
            }
            catch (Exception)
            {
                // Parse failure — return empty
            }
            return results;
        }

        // ─────────────────────────────────────────────────────────────────
        // 2d. Iowa State Mesonet LSR  –  wind gust events (type=G)
        //     Mirrors the hail LSR but returns non-thunderstorm wind reports.
        // ─────────────────────────────────────────────────────────────────
        public record WindEvent(double Lat, double Lng, double SpeedMph, DateTime Date, string Source);

        public async Task<List<WindEvent>> GetMesonetLsrWindAsync(
            double lat, double lng, double radiusMiles, string stateAbbr, int lookbackDays = 90)
        {
            if (string.IsNullOrWhiteSpace(stateAbbr)) return new List<WindEvent>();

            double filterMiles = Math.Max(radiusMiles * 2, 10.0);
            var endTime   = DateTime.UtcNow;
            var startTime = endTime.AddDays(-lookbackDays);

            var sts = startTime.ToString("yyyyMMddHHmm");
            var ets = endTime.ToString("yyyyMMddHHmm");
            var url = $"https://mesonet.agron.iastate.edu/geojson/lsr.php" +
                      $"?sts={sts}&ets={ets}" +
                      $"&states={Uri.EscapeDataString(stateAbbr.ToUpper())}" +
                      $"&type=G&fmt=geojson";
            try
            {
                using var client = _httpFactory.CreateClient("mesonet");
                var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return new List<WindEvent>();
                var json = await resp.Content.ReadAsStringAsync();
                return ParseMesonetLsrWindGeoJson(json, lat, lng, filterMiles);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mesonet LSR wind fetch failed");
                return new List<WindEvent>();
            }
        }

        private static List<WindEvent> ParseMesonetLsrWindGeoJson(
            string json, double centerLat, double centerLng, double filterMiles)
        {
            var results = new List<WindEvent>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("features", out var features)) return results;

                foreach (var feature in features.EnumerateArray())
                {
                    if (!feature.TryGetProperty("properties", out var props)) continue;
                    if (!feature.TryGetProperty("geometry",   out var geom))  continue;
                    if (!geom.TryGetProperty("coordinates",   out var coords)) continue;
                    var coordArr = coords.EnumerateArray().ToArray();
                    if (coordArr.Length < 2) continue;

                    double wLng = coordArr[0].GetDouble();
                    double wLat = coordArr[1].GetDouble();
                    if (HaversineDistanceMiles(centerLat, centerLng, wLat, wLng) > filterMiles) continue;

                    // LSR wind gust magnitude is in knots — convert to mph
                    double? speedKnots = GetDoubleField(props, "magnitude") ?? GetDoubleField(props, "magf");
                    double  speedMph   = (speedKnots ?? 0) * 1.15078;

                    // Skip junk entries (< 25 mph) — real damaging gusts start around 25-30 mph
                    if (speedMph < 25) continue;

                    DateTime date = DateTime.UtcNow.AddDays(-30);
                    if (props.TryGetProperty("valid", out var vt))
                        DateTime.TryParse(vt.GetString(), null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out date);

                    results.Add(new WindEvent(wLat, wLng, speedMph, date, "lsr-wind"));
                }
            }
            catch { }
            return results;
        }

        // ─────────────────────────────────────────────────────────────────
        // Nominatim reverse geocode — detect state for Storm Explorer
        //   Uses the same "overpass" HttpClient (no key, browser-style UA required)
        // ─────────────────────────────────────────────────────────────────
        public async Task<string> GetStateFromLatLngAsync(double lat, double lng)
        {
            try
            {
                using var client = _httpFactory.CreateClient("overpass");
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent", "StormLeadPro/1.0");
                var url  = $"https://nominatim.openstreetmap.org/reverse?format=json&lat={lat}&lon={lng}";
                var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return "";
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("address", out var addr))
                {
                    // ISO3166-2-lvl4 → "US-TX" → we keep only "TX"
                    if (addr.TryGetProperty("ISO3166-2-lvl4", out var lvl4))
                    {
                        var code = lvl4.GetString() ?? "";
                        return code.Length > 3 ? code[3..] : code;
                    }
                    if (addr.TryGetProperty("state_code", out var sc))
                        return sc.GetString() ?? "";
                }
                return "";
            }
            catch { return ""; }
        }

        // ─────────────────────────────────────────────────────────────────
        // 2e. NOAA SPC Local Storm Reports  –  hail (last 3 days only)
        //     Near-real-time CSV feed, typically available within minutes.
        //     URL: https://www.spc.noaa.gov/climo/reports/YYMMDD_rpts_hail.csv
        //     Columns: Time,Size,Location,County,State,Lat,Lon,Comments
        //     Used to fill the gap before Iowa State Mesonet indexes recent LSRs.
        // ─────────────────────────────────────────────────────────────────
        public async Task<List<HailEvent>> GetSpcRecentHailAsync(
            double lat, double lng, double radiusMiles, int lookbackDays = 3)
        {
            var results  = new List<HailEvent>();
            double filterMiles = Math.Max(radiusMiles * 2, 50.0);
            int daysToFetch = Math.Min(lookbackDays, 3); // SPC CSV only used for recent gap

            using var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");

            for (int d = 0; d < daysToFetch; d++)
            {
                var date   = DateTime.UtcNow.Date.AddDays(-d);
                var yymmd  = date.ToString("yyMMdd");
                var url    = $"https://www.spc.noaa.gov/climo/reports/{yymmd}_rpts_hail.csv";
                try
                {
                    var resp = await client.GetAsync(url);
                    if (!resp.IsSuccessStatusCode) continue;
                    var csv = await resp.Content.ReadAsStringAsync();
                    results.AddRange(ParseSpcHailCsv(csv, date, lat, lng, filterMiles));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SPC hail CSV fetch failed for {Date}", yymmd);
                }
            }
            return results;
        }

        private static List<HailEvent> ParseSpcHailCsv(
            string csv, DateTime date, double centerLat, double centerLng, double filterMiles)
        {
            var results = new List<HailEvent>();
            if (string.IsNullOrWhiteSpace(csv)) return results;

            foreach (var rawLine in csv.Split('\n'))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // Skip header row
                if (line.StartsWith("Time", StringComparison.OrdinalIgnoreCase)) continue;

                var cols = line.Split(',');
                if (cols.Length < 7) continue;

                try
                {
                    // Col 0: HHMM UTC,  Col 1: size in hundredths of inches (e.g. 100=1.00", 250=2.50"),  Col 5: lat,  Col 6: lon
                    var timeStr  = cols[0].Trim().PadLeft(4, '0');
                    var sizeStr  = cols[1].Trim();
                    var latStr   = cols[5].Trim();
                    var lonStr   = cols[6].Trim();

                    if (!double.TryParse(sizeStr, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double sizeRaw))
                        continue;
                    // SPC encodes size as hundredths of an inch (integer); convert to inches
                    double sizeIn = sizeRaw / 100.0;
                    if (!double.TryParse(latStr,  System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double eLat))
                        continue;
                    if (!double.TryParse(lonStr,  System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double eLng))
                        continue;

                    // Parse HHMM into a full UTC datetime
                    if (timeStr.Length >= 4
                        && int.TryParse(timeStr[..2], out int hh)
                        && int.TryParse(timeStr[2..4], out int mm))
                    {
                        var eventTime = date.AddHours(hh).AddMinutes(mm);
                        double dist   = HaversineDistanceMiles(centerLat, centerLng, eLat, eLng);
                        if (dist <= filterMiles && sizeIn > 0)
                        {
                            results.Add(new HailEvent
                            {
                                Lat        = eLat,
                                Lng        = eLng,
                                SizeInches = sizeIn,
                                Date       = eventTime,
                                Source     = "SPC/NOAA"
                            });
                        }
                    }
                }
                catch { /* skip malformed rows */ }
            }
            return results;
        }

        // ─────────────────────────────────────────────────────────────────
        // Storm clustering — group LSR point reports into storm events
        //   Same-day + within 15 mi → one cluster. Wind events within 20 mi
        //   on the same day are associated and contribute to the score.
        //
        //   Relevancy score (0-100):
        //     • hail   up to 60 pts  (capped at 2.4")
        //     • recency up to 30 pts  (linear decay over lookback window)
        //     • wind    up to 10 pts  (capped at 50 mph)
        // ─────────────────────────────────────────────────────────────────
        public class StormCluster
        {
            public string   Id             { get; set; } = "";
            public DateTime Date           { get; set; }
            public double   Lat            { get; set; }
            public double   Lng            { get; set; }
            public double   MaxHailInches  { get; set; }
            public double   MaxWindMph     { get; set; }
            public int      HailReports    { get; set; }
            public int      WindReports    { get; set; }
            public double   RelevancyScore { get; set; }
        }

        public async Task<List<StormCluster>> GetStormClustersAsync(
            double lat, double lng, double radiusMiles,
            string stateAbbr, double minHailInches, bool includeWind, int lookbackDays)
        {
            var endTime   = DateTime.UtcNow;
            var startTime = endTime.AddDays(-lookbackDays);

            // Fetch LSR, SPC recent, and wind in parallel
            var lsrTask  = GetMesonetLsrHailInWindowAsync(lat, lng, radiusMiles, stateAbbr, startTime, endTime);
            var spcTask  = GetSpcRecentHailAsync(lat, lng, radiusMiles, Math.Min(lookbackDays, 3));
            var windTask = includeWind
                ? GetMesonetLsrWindAsync(lat, lng, radiusMiles, stateAbbr, lookbackDays)
                : Task.FromResult(new List<WindEvent>());

            await Task.WhenAll(lsrTask, spcTask, windTask);

            // Merge LSR + SPC, deduplicate by proximity and date
            var lsrEvents = await lsrTask;
            var spcEvents = await spcTask;
            var combined  = new List<HailEvent>(lsrEvents);
            foreach (var spc in spcEvents)
            {
                // Only add SPC report if no LSR report exists within 5 mi on the same day
                bool duplicate = combined.Any(e =>
                    e.Date.Date == spc.Date.Date &&
                    HaversineDistanceMiles(e.Lat, e.Lng, spc.Lat, spc.Lng) < 5.0);
                if (!duplicate) combined.Add(spc);
            }

            var hailEvents = combined
                .Where(e => e.SizeInches >= minHailInches && e.Date >= startTime)
                .ToList();
            var windEvents = await windTask;

            _logger.LogInformation(
                "StormClusters: {Lsr} LSR + {Spc} SPC = {Total} hail reports, {Wind} wind reports → clustering",
                lsrEvents.Count, spcEvents.Count, hailEvents.Count, windEvents.Count);

            return ClusterIntoStorms(hailEvents, windEvents, lookbackDays);
        }

        /// <summary>Single-window hail LSR fetch for a specific date range.</summary>
        private async Task<List<HailEvent>> GetMesonetLsrHailInWindowAsync(
            double lat, double lng, double radiusMiles, string stateAbbr,
            DateTime startTime, DateTime endTime)
        {
            if (string.IsNullOrWhiteSpace(stateAbbr)) return new List<HailEvent>();

            double filterMiles = Math.Max(radiusMiles * 2, 10.0);
            var sts = startTime.ToString("yyyyMMddHHmm");
            var ets = endTime.ToString("yyyyMMddHHmm");
            var url = $"https://mesonet.agron.iastate.edu/geojson/lsr.php" +
                      $"?sts={sts}&ets={ets}" +
                      $"&states={Uri.EscapeDataString(stateAbbr.ToUpper())}" +
                      $"&type=H&fmt=geojson";
            try
            {
                using var client = _httpFactory.CreateClient("mesonet");
                var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return new List<HailEvent>();
                var json = await resp.Content.ReadAsStringAsync();
                return ParseMesonetLsrGeoJson(json, lat, lng, filterMiles);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mesonet LSR hail window fetch failed");
                return new List<HailEvent>();
            }
        }

        private static List<StormCluster> ClusterIntoStorms(
            List<HailEvent> hailEvents, List<WindEvent> windEvents, int lookbackDays)
        {
            var clusters = new List<StormCluster>();
            var used     = new HashSet<int>();

            for (int i = 0; i < hailEvents.Count; i++)
            {
                if (used.Contains(i)) continue;

                var seed    = hailEvents[i];
                var members = new List<HailEvent> { seed };
                used.Add(i);

                // Sweep remaining reports: same calendar day + within 15 miles → same storm
                for (int j = i + 1; j < hailEvents.Count; j++)
                {
                    if (used.Contains(j)) continue;
                    var other    = hailEvents[j];
                    bool sameDay = seed.Date.Date == other.Date.Date;
                    bool near    = HaversineDistanceMiles(
                        seed.Lat, seed.Lng, other.Lat, other.Lng) <= 15;
                    if (sameDay && near) { members.Add(other); used.Add(j); }
                }

                double cLat    = members.Average(m => m.Lat);
                double cLng    = members.Average(m => m.Lng);
                double maxHail = members.Max(m => m.SizeInches);

                // Associate wind events from the same day within 20 miles
                var assocWind = windEvents
                    .Where(w => w.Date.Date == seed.Date.Date &&
                                HaversineDistanceMiles(cLat, cLng, w.Lat, w.Lng) <= 20)
                    .ToList();

                double maxWind = assocWind.Count > 0 ? assocWind.Max(w => w.SpeedMph) : 0;

                int    daysAgo  = (int)(DateTime.UtcNow - seed.Date.Date).TotalDays;
                double hailPts  = Math.Min(maxHail * 25, 60);
                double recency  = 30.0 * Math.Max(0, 1.0 - (double)daysAgo / Math.Max(lookbackDays, 1));
                double windPts  = Math.Min(maxWind / 5.0, 10);
                double score    = Math.Round(hailPts + recency + windPts, 1);

                clusters.Add(new StormCluster
                {
                    Id             = $"{seed.Date:yyyy-MM-dd}-{cLat:F3}-{cLng:F3}",
                    Date           = seed.Date.Date,
                    Lat            = Math.Round(cLat, 5),
                    Lng            = Math.Round(cLng, 5),
                    MaxHailInches  = Math.Round(maxHail, 2),
                    MaxWindMph     = Math.Round(maxWind, 1),
                    HailReports    = members.Count,
                    WindReports    = assocWind.Count,
                    RelevancyScore = score,
                });
            }

            return clusters.OrderByDescending(c => c.RelevancyScore).ToList();
        }

        // ─────────────────────────────────────────────────────────────────
        // 3. Regrid Parcel API  –  property owner names
        //    Requires a free Regrid token (25 lookups/day on free Starter plan).
        //    Sign up at: https://app.regrid.com
        //    Add token to appsettings.json: "Regrid": { "Token": "..." }
        //    Docs: https://support.regrid.com/api/using-the-parcel-api-v1
        // ─────────────────────────────────────────────────────────────────
        // Parcel data returned by Regrid — owner name + year the home was built
        public record RegridParcelData(string? OwnerName, int? YearBuilt);

        public async Task<RegridParcelData?> GetRegridParcelDataAsync(double lat, double lng, string? address = null)
        {
            var token = _config["Regrid:Token"];
            if (string.IsNullOrWhiteSpace(token)) return null;

            // Regrid v2 API — address search uses "query" param
            // NOTE: trial sandbox tokens are restricted to 7 counties only
            string url;
            if (!string.IsNullOrWhiteSpace(address))
            {
                var clean = address
                    .Replace(", USA", "")
                    .Replace(", United States", "")
                    .Trim();

                url = $"https://app.regrid.com/api/v2/parcels/address" +
                      $"?query={Uri.EscapeDataString(clean)}" +
                      $"&token={token}&limit=1&return_enhanced_ownership=true";
            }
            else
            {
                url = $"https://app.regrid.com/api/v2/parcels/point" +
                      $"?lat={lat}&lon={lng}&token={token}&limit=1&radius=200&return_enhanced_ownership=true";
            }
            try
            {
                using var client = _httpFactory.CreateClient("regrid");
                var resp = await client.GetAsync(url);
                var json = await resp.Content.ReadAsStringAsync();

                // Log full response for debugging (will trim in production once working)
                _logger.LogInformation("Regrid {Status} for {Lat},{Lng} — body: {Body}",
                    resp.StatusCode, lat, lng, json.Length > 2000 ? json[..2000] : json);

                if (!resp.IsSuccessStatusCode) return null;
                try { return ParseRegridParcelData(json); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Regrid parse failed — raw JSON: {Json}", json);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Regrid API call failed");
                return null;
            }
        }

        private static RegridParcelData? ParseRegridParcelData(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Navigate to features array
            JsonElement features;
            if (root.TryGetProperty("parcels", out var parcels) &&
                parcels.TryGetProperty("features", out features))
            { /* v2 */ }
            else if (root.TryGetProperty("features", out features))
            { /* root FeatureCollection */ }
            else return null;

            if (features.GetArrayLength() == 0) return null;

            var first = features[0];
            if (!first.TryGetProperty("properties", out var props)) return null;

            string?  ownerName = null;
            int?     yearBuilt = null;

            if (props.TryGetProperty("fields", out var fields) &&
                fields.ValueKind == JsonValueKind.Object)
            {
                // Owner name
                foreach (var n in new[] { "owner", "owner1", "owner_name", "ownerName", "OWNER_NAME" })
                {
                    if (fields.TryGetProperty(n, out var v) && v.GetString() is { Length: > 0 } raw)
                    { ownerName = TitleCase(raw); break; }
                }

                // Year built — county assessors use many field names
                foreach (var n in new[] { "yearbuilt", "year_built", "yrbuilt", "yr_built",
                                          "YearBuilt", "YEARBUILT", "YR_BUILT", "effyearbuilt" })
                {
                    if (!fields.TryGetProperty(n, out var v)) continue;
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var yr) && yr > 1800)
                    { yearBuilt = yr; break; }
                    if (v.ValueKind == JsonValueKind.String &&
                        int.TryParse(v.GetString(), out yr) && yr > 1800)
                    { yearBuilt = yr; break; }
                }
            }

            // Fallback: enhanced_ownership for owner name
            if (ownerName == null &&
                props.TryGetProperty("enhanced_ownership", out var eoArr) &&
                eoArr.ValueKind == JsonValueKind.Array &&
                eoArr.GetArrayLength() > 0)
            {
                var eo = eoArr[0];
                foreach (var n in new[] { "eo_owner", "eo_ownerlast", "owner_name", "owner" })
                {
                    if (eo.TryGetProperty(n, out var v) && v.GetString() is { Length: > 0 } raw)
                    { ownerName = TitleCase(raw); break; }
                }
            }

            return ownerName != null || yearBuilt != null
                ? new RegridParcelData(ownerName, yearBuilt)
                : null;
        }

        private static string TitleCase(string s) =>
            System.Globalization.CultureInfo.CurrentCulture
                  .TextInfo.ToTitleCase(s.ToLower().Trim());

        // ─────────────────────────────────────────────────────────────────
        // 6. Tomorrow.io Historical Weather  –  fills the NOAA 120-day lag
        //    Free plan: 500 req/day, 5+ years of history.
        //    Uses the Timelines API with daily timesteps to find ice-pellet /
        //    hail days (precipitationType == 4 or weatherCode 7000-7102).
        //    We query at the search CENTER — events are then scored against
        //    every property using the same haversine proximity filter as NOAA.
        //    Docs: https://docs.tomorrow.io/reference/timelines
        // ─────────────────────────────────────────────────────────────────
        // Cache Tomorrow.io hail lookups per rough region + calendar day.
        //
        // Why: this fires 2 API calls (two 1-year windows) on every single report
        // generation, unconditionally — and Tomorrow.io's free tier is a hard 500
        // calls/day *system-wide*, with no published per-call rate beyond that
        // (overage is quote-based, contact-sales only). 500 calls/day = 250
        // reports/day before cost becomes an unknown. That ceiling is exactly what
        // a real storm-surge day looks like for this product: a neighborhood gets
        // hit, and many users pull reports for nearby addresses the same day.
        // Bucketing to a ~0.05° grid (~3-4 miles — matching the margin
        // MeshSwathService already uses for containment queries) means those
        // nearby properties share one cache entry instead of each re-fetching.
        //
        // 12-hour expiry: Tomorrow.io is only filling NOAA's ~90-120 day radar
        // lag with near-real-time data here, not serving a live nowcast, so
        // day-old freshness is fine — trading a little staleness for a large cut
        // in call volume on exactly the days call volume is highest.
        private const double TomorrowIoBucketDegrees = 0.05;
        private static readonly TimeSpan TomorrowIoCacheDuration = TimeSpan.FromHours(12);

        // Hard system-wide daily call cap, on top of the caching above. The cache
        // raises the ceiling (nearby same-day lookups share one hit) but doesn't
        // guarantee we stay under Tomorrow.io's free tier on a big enough storm —
        // this is the backstop that turns "unplanned overage charge" into "today's
        // reports quietly stop getting the Tomorrow.io fill." Counter is process-
        // local (not persisted), so a restart resets it early — acceptable, since
        // under-counting only risks a few extra calls, never an outage.
        private readonly object _tomorrowIoCounterLock = new();
        private DateTime _tomorrowIoCounterDate = DateTime.MinValue;
        private int      _tomorrowIoCallsToday  = 0;

        private bool TryReserveTomorrowIoCalls(int callsNeeded)
        {
            var cap = _config.GetValue<int?>("TomorrowIo:DailyCallCap") ?? 480;
            lock (_tomorrowIoCounterLock)
            {
                var today = DateTime.UtcNow.Date;
                if (_tomorrowIoCounterDate != today)
                {
                    _tomorrowIoCounterDate = today;
                    _tomorrowIoCallsToday  = 0;
                }

                if (_tomorrowIoCallsToday + callsNeeded > cap)
                    return false;

                _tomorrowIoCallsToday += callsNeeded;
                return true;
            }
        }

        public async Task<List<HailEvent>> GetTomorrowIoHailAsync(
            double lat, double lng, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return new List<HailEvent>();

            var bucketLat = Math.Round(lat / TomorrowIoBucketDegrees) * TomorrowIoBucketDegrees;
            var bucketLng = Math.Round(lng / TomorrowIoBucketDegrees) * TomorrowIoBucketDegrees;
            var cacheKey  = $"tomorrowio-hail:{bucketLat:F2},{bucketLng:F2}:{DateTime.UtcNow:yyyyMMdd}";

            if (_cache.TryGetValue(cacheKey, out List<HailEvent>? cachedResults) && cachedResults != null)
            {
                _logger.LogInformation(
                    "Tomorrow.io cache hit for {Lat},{Lng} (bucket {BucketLat},{BucketLng}) — {Count} events, no API call made",
                    lat, lng, bucketLat, bucketLng, cachedResults.Count);
                return cachedResults;
            }

            var results   = new List<HailEvent>();
            var endTime   = DateTime.UtcNow;
            var startTime = endTime.AddYears(-2);   // 2-year look-back

            // Split into two 1-year windows — keeps each request small and
            // well within Tomorrow.io's per-request data limits.
            var windows = new[]
            {
                (startTime,          startTime.AddYears(1)),
                (startTime.AddYears(1), endTime)
            };

            if (!TryReserveTomorrowIoCalls(windows.Length))
            {
                _logger.LogWarning(
                    "Tomorrow.io daily call cap reached ({CallsToday}/{Cap}) — skipping fill for {Lat},{Lng}; report falls back to NOAA/LSR/StormEvents/MESH only",
                    _tomorrowIoCallsToday, _config.GetValue<int?>("TomorrowIo:DailyCallCap") ?? 480, lat, lng);
                return results;
            }

            async Task FetchWindow((DateTime from, DateTime to) w)
            {
                var url = "v4/timelines" +
                          $"?location={lat},{lng}" +
                          "&fields=precipitationType,precipitationIntensity,weatherCode" +
                          "&timesteps=1d" +
                          $"&startTime={w.from:yyyy-MM-ddTHH:mm:ssZ}" +
                          $"&endTime={w.to:yyyy-MM-ddTHH:mm:ssZ}" +
                          "&units=imperial" +
                          $"&apikey={apiKey}";
                try
                {
                    using var client = _httpFactory.CreateClient("tomorrow");
                    var resp = await client.GetAsync(url);
                    var body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Tomorrow.io returned {Status}: {Body}",
                            resp.StatusCode, body.Length > 300 ? body[..300] : body);
                        return;
                    }
                    lock (results) ParseTomorrowIoTimeline(body, lat, lng, results);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Tomorrow.io fetch failed for window {From:yyyy-MM-dd}",
                        w.from);
                }
            }

            await Task.WhenAll(windows.Select(FetchWindow));

            _logger.LogInformation("Tomorrow.io returned {Count} hail events near {Lat},{Lng}",
                results.Count, lat, lng);

            _cache.Set(cacheKey, results, TomorrowIoCacheDuration);

            return results;
        }

        private static void ParseTomorrowIoTimeline(
            string json, double lat, double lng, List<HailEvent> results)
        {
            try
            {
                using var doc  = JsonDocument.Parse(json);
                var       root = doc.RootElement;

                // Navigate: data.timelines[0].intervals[]
                if (!root.TryGetProperty("data", out var data))             return;
                if (!data.TryGetProperty("timelines", out var timelines))   return;
                if (timelines.GetArrayLength() == 0)                        return;

                var timeline = timelines[0];
                if (!timeline.TryGetProperty("intervals", out var intervals)) return;

                foreach (var interval in intervals.EnumerateArray())
                {
                    if (!interval.TryGetProperty("values", out var vals)) continue;

                    // precipitationType 4 = Ice Pellets (closest to hail)
                    // weatherCode 7000–7102 = Ice Pellet events
                    var precipType  = GetDoubleField(vals, "precipitationType");
                    var weatherCode = GetDoubleField(vals, "weatherCode");
                    var intensity   = GetDoubleField(vals, "precipitationIntensity") ?? 0;

                    bool isHail = precipType == 4
                               || (weatherCode >= 7000 && weatherCode <= 7102);

                    if (!isHail) continue;

                    // Parse the interval start time
                    DateTime date = DateTime.UtcNow.AddYears(-1);
                    if (interval.TryGetProperty("startTime", out var st))
                        DateTime.TryParse(st.GetString(), null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out date);

                    // Estimate hail size from precipitation intensity.
                    // Tomorrow.io free tier does not provide actual hail diameter,
                    // so we use a conservative default in the penny–quarter range.
                    // intensity is in in/hr on imperial units.
                    double sizeInches = intensity >= 0.15 ? 1.00   // quarter-sized (roof damage threshold)
                                      : intensity >= 0.05 ? 0.88   // penny-sized
                                      :                     0.75;  // pea-sized

                    results.Add(new HailEvent
                    {
                        Lat        = lat,
                        Lng        = lng,
                        SizeInches = sizeInches,
                        Date       = date,
                        Source     = "tomorrow"
                    });
                }
            }
            catch (Exception)
            {
                // Silently swallow parse errors — caller handles partial results
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // 2f. LSR Hail Swath  –  ground-truth polygon overlay
        //     Source: Iowa State Mesonet LSR (same feed as Storm Explorer).
        //     Groups same-day LSR hail reports into buffered polygons showing
        //     where each storm hit and how large the hail was.  Covers the
        //     entire US.  NHP ArcGIS data was Canadian-only with no MESH field.
        // ─────────────────────────────────────────────────────────────────

        // Valid empty FeatureCollection — safe for Leaflet L.geoJSON()
        private const string EmptyFeatureCollection = "{\"type\":\"FeatureCollection\",\"features\":[]}";

        public async Task<string> GetMrmsHailSwathGeoJsonAsync(
            double minLat, double maxLat, double minLng, double maxLng, int lookbackDays)
        {
            // LSR-based swath generation: cluster same-day hail reports into size-banded
            // convex-hull polygons.  Each size band gets its own polygon per cluster so the
            // frontend can render concentric rings (large outer = pea hail, small inner = golf ball+).
            try
            {
                double centerLat = (minLat + maxLat) / 2;
                double centerLng = (minLng + maxLng) / 2;

                var stateAbbr = await GetStateFromLatLngAsync(centerLat, centerLng);
                if (string.IsNullOrWhiteSpace(stateAbbr))
                    return EmptyFeatureCollection;

                var endTime   = DateTime.UtcNow;
                var startTime = endTime.AddDays(-lookbackDays);

                // Radius: half the diagonal of the visible bbox, capped at 250 miles
                double radiusMiles = Math.Min(
                    HaversineDistanceMiles(minLat, minLng, maxLat, maxLng) * 0.75, 250.0);

                var lsrEvents = await GetMesonetLsrHailInWindowAsync(
                    centerLat, centerLng, radiusMiles, stateAbbr, startTime, endTime);

                // Trim to bbox with a small margin
                double margin = 0.05;
                var inBox = lsrEvents
                    .Where(e => e.Lat >= minLat - margin && e.Lat <= maxLat + margin &&
                                e.Lng >= minLng - margin && e.Lng <= maxLng + margin)
                    .ToList();

                if (inBox.Count == 0)
                    return EmptyFeatureCollection;

                // Size bands rendered smallest→largest so larger hail polygons sit on top.
                // Each band includes all points >= that threshold.
                double[] sizeBands = new double[] { 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 };

                using var ms     = new System.IO.MemoryStream();
                using var writer = new System.Text.Json.Utf8JsonWriter(ms);
                writer.WriteStartObject();
                writer.WriteString("type", "FeatureCollection");
                writer.WriteStartArray("features");

                // Outer band first (largest area, lowest z), inner bands on top.
                foreach (var dayGrp in inBox.GroupBy(e => e.Date.Date).OrderByDescending(g => g.Key))
                {
                    var dayPoints = dayGrp.ToList();

                    // Emit bands from smallest threshold to largest (outer → inner).
                    foreach (var band in sizeBands)
                    {
                        var bandPoints = dayPoints.Where(p => p.SizeInches >= band).ToList();
                        if (bandPoints.Count == 0) continue;

                        foreach (var cluster in ClusterHailPoints(bandPoints, 20.0))
                        {
                            var ring = BuildConvexHullRing(cluster);
                            if (ring == null) continue;

                            writer.WriteStartObject();
                            writer.WriteString("type", "Feature");

                            writer.WriteStartObject("geometry");
                            writer.WriteString("type", "Polygon");
                            writer.WriteStartArray("coordinates");
                            writer.WriteStartArray();
                            foreach (var (rLat, rLng) in ring)
                            {
                                writer.WriteStartArray();
                                writer.WriteNumberValue(Math.Round(rLng, 5));
                                writer.WriteNumberValue(Math.Round(rLat, 5));
                                writer.WriteEndArray();
                            }
                            writer.WriteEndArray();
                            writer.WriteEndArray();
                            writer.WriteEndObject(); // geometry

                            writer.WriteStartObject("properties");
                            writer.WriteNumber("sizeBand",   band);
                            writer.WriteNumber("maxHailIn",  Math.Round(cluster.Max(p => p.SizeInches), 2));
                            writer.WriteString("date",       dayGrp.Key.ToString("yyyy-MM-dd"));
                            writer.WriteNumber("reportCount", cluster.Count);
                            writer.WriteEndObject(); // properties

                            writer.WriteEndObject(); // feature
                        }
                    }
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LSR hail swath generation failed");
                return EmptyFeatureCollection;
            }
        }

        /// <summary>Cluster hail points: each seed grabs all un-clustered points within maxRadiusMiles.</summary>
        private static List<List<HailEvent>> ClusterHailPoints(List<HailEvent> points, double maxRadiusMiles)
        {
            var clusters = new List<List<HailEvent>>();
            var used     = new bool[points.Count];

            for (int i = 0; i < points.Count; i++)
            {
                if (used[i]) continue;
                var cluster = new List<HailEvent> { points[i] };
                used[i] = true;

                for (int j = i + 1; j < points.Count; j++)
                {
                    if (used[j]) continue;
                    if (HaversineDistanceMiles(points[i].Lat, points[i].Lng,
                                               points[j].Lat, points[j].Lng) <= maxRadiusMiles)
                    { cluster.Add(points[j]); used[j] = true; }
                }
                clusters.Add(cluster);
            }
            return clusters;
        }

        /// <summary>
        /// Build a closed GeoJSON ring for a cluster using convex hull + outward buffer.
        /// 1 point  → 16-gon circle (~5 mi radius).
        /// 2 points → capsule approximation (16-gon around each endpoint, merged).
        /// 3+ pts   → Andrew's monotone-chain convex hull, then each vertex pushed
        ///            outward from the centroid by BufDeg (~5 miles).
        /// </summary>
        private static List<(double lat, double lng)>? BuildConvexHullRing(List<HailEvent> cluster)
        {
            if (cluster.Count == 0) return null;
            const double BufDeg = 0.07;   // ~5 miles outward buffer
            const int    CirclePts = 16;

            if (cluster.Count == 1)
            {
                // Single point → circle
                var cLat = cluster[0].Lat;
                var cLng = cluster[0].Lng;
                var ring = new List<(double lat, double lng)>();
                for (int i = 0; i <= CirclePts; i++)
                {
                    double a = 2 * Math.PI * i / CirclePts;
                    ring.Add((lat: cLat + BufDeg * Math.Sin(a), lng: cLng + BufDeg * Math.Cos(a)));
                }
                return ring;
            }

            if (cluster.Count == 2)
            {
                // Two points → stadium shape: circles at each end merged into a closed ring
                var pts = new List<(double lat, double lng)>();
                foreach (var p in cluster)
                {
                    for (int i = 0; i < CirclePts; i++)
                    {
                        double a = 2 * Math.PI * i / CirclePts;
                        pts.Add((lat: p.Lat + BufDeg * Math.Sin(a), lng: p.Lng + BufDeg * Math.Cos(a)));
                    }
                }
                // Fall through to convex hull on the expanded point cloud
                var hull2 = ConvexHull(pts);
                if (hull2.Count < 3) return null;
                hull2.Add(hull2[0]); // close ring
                return hull2;
            }

            // 3+ points: compute convex hull then buffer each vertex outward
            var rawPts = cluster.Select(p => (lat: p.Lat, lng: p.Lng)).ToList();
            var hull   = ConvexHull(rawPts);
            if (hull.Count < 3) return null;

            // Centroid of hull
            double cntLat = hull.Average(p => p.lat);
            double cntLng = hull.Average(p => p.lng);

            // Push each hull vertex outward from centroid
            var buffered = hull.Select(p =>
            {
                double dLat = p.lat - cntLat;
                double dLng = p.lng - cntLng;
                double len  = Math.Sqrt(dLat * dLat + dLng * dLng);
                if (len < 1e-9) return (lat: p.lat + BufDeg, lng: p.lng + BufDeg);
                double scale = (len + BufDeg) / len;
                return (lat: cntLat + dLat * scale, lng: cntLng + dLng * scale);
            }).ToList();

            buffered.Add(buffered[0]); // close the ring
            return buffered;
        }

        /// <summary>
        /// Andrew's monotone-chain convex hull.  Returns vertices in CCW order (open — caller closes).
        /// Input: list of (lat, lng) as (y, x).
        /// </summary>
        private static List<(double lat, double lng)> ConvexHull(List<(double lat, double lng)> points)
        {
            int n = points.Count;
            if (n < 3) return new List<(double lat, double lng)>(points);

            // Sort by x (lng) then y (lat)
            var sorted = points.OrderBy(p => p.lng).ThenBy(p => p.lat).ToList();

            var hull = new List<(double lat, double lng)>(2 * n);

            // Lower hull
            foreach (var p in sorted)
            {
                while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }

            // Upper hull
            int lower = hull.Count + 1;
            for (int i = n - 2; i >= 0; i--)
            {
                var p = sorted[i];
                while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1); // last point == first
            return hull;
        }

        /// <summary>Cross product of vectors OA and OB (positive = left turn).</summary>
        private static double Cross(
            (double lat, double lng) O,
            (double lat, double lng) A,
            (double lat, double lng) B)
            => (A.lng - O.lng) * (B.lat - O.lat) - (A.lat - O.lat) * (B.lng - O.lng);

        // ─────────────────────────────────────────────────────────────────
        // Hail Reports GeoJSON  –  individual LSR + SPC events as Point features
        //   Used by the "Hail Reports" map overlay toggle.
        //   Returns up to 500 events within the viewport bbox, sorted largest first.
        // ─────────────────────────────────────────────────────────────────
        public async Task<string> GetHailEventsGeoJsonAsync(
            double minLat, double maxLat, double minLng, double maxLng, int lookbackDays)
        {
            double centerLat  = (minLat + maxLat) / 2.0;
            double centerLng  = (minLng + maxLng) / 2.0;
            // Radius = distance from center to NE corner, padded 20 % to avoid edge gaps
            double radiusMiles = Math.Min(
                HaversineDistanceMiles(centerLat, centerLng, maxLat, maxLng) * 1.2,
                200.0);

            var stateAbbr = await GetStateFromLatLngAsync(centerLat, centerLng);

            var endTime   = DateTime.UtcNow;
            var startTime = endTime.AddDays(-lookbackDays);

            var lsrTask = GetMesonetLsrHailInWindowAsync(
                centerLat, centerLng, radiusMiles, stateAbbr, startTime, endTime);
            var spcTask = GetSpcRecentHailAsync(
                centerLat, centerLng, radiusMiles, Math.Min(lookbackDays, 3));

            await Task.WhenAll(lsrTask, spcTask);

            // Merge LSR + SPC, deduplicating within 5 mi on same day
            var allEvents = new List<HailEvent>(lsrTask.Result);
            foreach (var spcEvt in spcTask.Result)
            {
                bool dup = allEvents.Any(e =>
                    e.Date.Date == spcEvt.Date.Date &&
                    HaversineDistanceMiles(e.Lat, e.Lng, spcEvt.Lat, spcEvt.Lng) < 5.0);
                if (!dup) allEvents.Add(spcEvt);
            }

            // Filter to exact bbox, sort largest hail first, cap at 500
            var filtered = allEvents
                .Where(e => e.Lat >= minLat && e.Lat <= maxLat &&
                            e.Lng >= minLng && e.Lng <= maxLng)
                .OrderByDescending(e => e.SizeInches)
                .Take(500)
                .ToList();

            _logger.LogInformation(
                "HailEventsGeoJson: {Count} events in bbox [{MinLat},{MinLng}→{MaxLat},{MaxLng}]",
                filtered.Count, minLat, minLng, maxLat, maxLng);

            using var ms     = new System.IO.MemoryStream();
            using var writer = new System.Text.Json.Utf8JsonWriter(ms);
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WriteStartArray("features");

            foreach (var ev in filtered)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "Feature");

                writer.WriteStartObject("geometry");
                writer.WriteString("type", "Point");
                writer.WriteStartArray("coordinates");
                writer.WriteNumberValue(Math.Round(ev.Lng, 5));
                writer.WriteNumberValue(Math.Round(ev.Lat, 5));
                writer.WriteEndArray();
                writer.WriteEndObject(); // geometry

                writer.WriteStartObject("properties");
                writer.WriteNumber("maxHailIn", Math.Round(ev.SizeInches, 2));
                writer.WriteString("date",      ev.Date.ToString("yyyy-MM-dd"));
                writer.WriteString("source",    ev.Source);
                writer.WriteEndObject(); // properties

                writer.WriteEndObject(); // feature
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        // ─────────────────────────────────────────────────────────────────
        // Haversine distance helper (used by RoofHealthController)
        // ─────────────────────────────────────────────────────────────────
        public static double HaversineDistanceMiles(
            double lat1, double lng1, double lat2, double lng2)
        {
            const double R    = 3958.8;
            var          dLat = (lat2 - lat1) * Math.PI / 180;
            var          dLng = (lng2 - lng1) * Math.PI / 180;
            var          a    = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                              + Math.Cos(lat1 * Math.PI / 180)
                              * Math.Cos(lat2 * Math.PI / 180)
                              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        // ─────────────────────────────────────────────────────────────────
        // Shared helper
        // ─────────────────────────────────────────────────────────────────

        private static double? GetDoubleField(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var d))
                return d;
            return null;
        }

        // ─────────────────────────────────────────────────────────────────
        // DTOs
        // ─────────────────────────────────────────────────────────────────
        public record OsmAddress
        {
            public string FullAddress { get; init; } = "";
            public string HouseNumber { get; init; } = "";
            public string Street      { get; init; } = "";
            public string City        { get; init; } = "";
            public string State       { get; init; } = "";
            public double Lat         { get; init; }
            public double Lng         { get; init; }
        }

        public record HailEvent
        {
            public double   Lat        { get; init; }
            public double   Lng        { get; init; }
            public double   SizeInches { get; init; }
            public DateTime Date       { get; init; }
            public string   Source     { get; init; } = "noaa";
        }


        // ─────────────────────────────────────────────────────────────────
        // 4. BatchSkipTracing  -  phone + email lookup
        //    Sign up: https://batchskiptracing.com
        //    Config:  "BatchSkipTracing": { "ApiKey": "..." }
        // ─────────────────────────────────────────────────────────────────
        public record BstContactData(string? Phone, string? Email);

        public async Task<BstContactData?> GetBstContactAsync(
            string apiKey, string? ownerName, string address)
        {
            try
            {
                var nameParts = (ownerName ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var firstName = nameParts.Length > 0 ? nameParts[0] : "";
                var lastName  = nameParts.Length > 1 ? nameParts[1] : "";

                var cleaned  = address.Replace(", USA", "").Replace(", United States", "");
                var parts    = cleaned.Split(',');
                var street   = parts.Length > 0 ? parts[0].Trim() : cleaned;
                var city     = parts.Length > 1 ? parts[1].Trim() : "";
                var stateZip = parts.Length > 2 ? parts[2].Trim().Split(' ') : System.Array.Empty<string>();
                var state    = stateZip.Length > 0 ? stateZip[0].ToUpperInvariant() : "";
                var zip      = stateZip.Length > 1 ? stateZip[1] : "";

                var payload = new { firstName, lastName, address = street, city, state, zip };
                var json    = JsonSerializer.Serialize(payload);
                var content = new System.Net.Http.StringContent(
                    json, System.Text.Encoding.UTF8, "application/json");

                using var client = _httpFactory.CreateClient("bst");
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                client.Timeout = TimeSpan.FromSeconds(20);

                var resp = await client.PostAsync(
                    "https://api.batchskiptracing.com/api/lead", content);
                var body = await resp.Content.ReadAsStringAsync();

                _logger.LogInformation("BST {Status} for {Address}: {Body}",
                    resp.StatusCode, address, body.Length > 400 ? body[..400] : body);

                if (!resp.IsSuccessStatusCode) return null;
                return ParseBstResponse(body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BST call failed for {Address}", address);
                return null;
            }
        }

        private static BstContactData? ParseBstResponse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var output = doc.RootElement.TryGetProperty("output", out var o)
                               ? o : doc.RootElement;

                string? phone = null;
                string? email = null;

                if (output.TryGetProperty("phones", out var phones) &&
                    phones.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in phones.EnumerateArray())
                    {
                        var num = p.TryGetProperty("phone", out var pv) ? pv.GetString() : null;
                        var type = p.TryGetProperty("phoneType", out var tv) ? tv.GetString() : null;
                        if (num == null) continue;
                        if (phone == null) phone = num;
                        if (type != null &&
                            type.Contains("Mobile", StringComparison.OrdinalIgnoreCase))
                        { phone = num; break; }
                    }
                }

                if (output.TryGetProperty("emails", out var emails) &&
                    emails.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in emails.EnumerateArray())
                    {
                        var addr = e.TryGetProperty("email", out var ev) ? ev.GetString() : null;
                        if (addr != null) { email = addr; break; }
                    }
                }

                return (phone == null && email == null) ? null
                       : new BstContactData(phone, email);
            }
            catch { return null; }
        }

        // ─────────────────────────────────────────────────────────────────
        // 5. Whitepages Pro  -  phone + email from name + address
        //    Sign up: https://pro.whitepages.com
        //    Config:  "WhitepagesPro": { "ApiKey": "..." }
        //    Docs:    https://proapi.whitepages.com/3.0/person
        // ─────────────────────────────────────────────────────────────────
        public record WpContactData(string? OwnerName, string? Phone, string? Email, string ContactType = "owner");

        public async Task<List<WpContactData>> GetWhitepagesContactAsync(
            string apiKey, string? ownerName, string address)
        {
            try
            {
                // v2 property endpoint — reverse address lookup, key in X-Api-Key header
                var cleaned  = address.Replace(", USA", "").Replace(", United States", "");
                var parts    = cleaned.Split(',');
                var street   = parts.Length > 0 ? parts[0].Trim() : cleaned;
                var city     = parts.Length > 1 ? parts[1].Trim() : "";
                var stateZip = parts.Length > 2 ? parts[2].Trim().Split(' ') : System.Array.Empty<string>();
                var state    = stateZip.Length > 0 ? stateZip[0].ToUpperInvariant() : "";

                var qs  = $"street={Uri.EscapeDataString(street)}" +
                          $"&city={Uri.EscapeDataString(city)}" +
                          $"&state_code={Uri.EscapeDataString(state)}";
                var url = $"https://api.whitepages.com/v2/property/?{qs}";

                using var client = _httpFactory.CreateClient("whitepages");
                client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();

                _logger.LogInformation("Whitepages {Status} for {Address}: {Body}",
                    resp.StatusCode, address, body.Length > 800 ? body[..800] : body);

                if (!resp.IsSuccessStatusCode) return new();
                return ParseWhitepagesResponse(body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Whitepages call failed for {Address}", address);
                return new();
            }
        }

        // Public wrapper for testing without real API calls
        public List<WpContactData> ParseWpResponsePublic(string json) => ParseWhitepagesResponse(json);

        private static List<WpContactData> ParseWhitepagesResponse(string json)
        {
            // result.ownership_info.person_owners[] — owners (preferred, labeled "owner")
            // result.residents[]                    — residents (labeled "resident")
            // Deduplicate by person id across both arrays.
            var results = new List<WpContactData>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("result", out var result))
                    return results;

                var seen = new HashSet<string>();

                void ExtractPerson(JsonElement person, string contactType)
                {
                    var id   = person.TryGetProperty("id",   out var iv) ? iv.GetString() ?? "" : "";
                    var name = person.TryGetProperty("name", out var nv) ? nv.GetString() : null;

                    // Deduplicate by id (same person can appear in both owners + residents)
                    if (!string.IsNullOrEmpty(id) && !seen.Add(id)) return;

                    // Best phone — prefer Mobile
                    string? phone = null;
                    if (person.TryGetProperty("phones", out var phones) &&
                        phones.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in phones.EnumerateArray())
                        {
                            var num  = p.TryGetProperty("number", out var pv) ? pv.GetString() : null;
                            var type = p.TryGetProperty("type",   out var tv) ? tv.GetString() : null;
                            if (num == null) continue;
                            if (phone == null) phone = num;
                            if (type != null && type.Equals("Mobile", StringComparison.OrdinalIgnoreCase))
                            { phone = num; break; }
                        }
                    }

                    // First email
                    string? email = null;
                    if (person.TryGetProperty("emails", out var emails) &&
                        emails.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in emails.EnumerateArray())
                        {
                            var addr = e.TryGetProperty("email", out var ev) ? ev.GetString() : null;
                            if (addr != null) { email = addr; break; }
                        }
                    }

                    if (phone != null || email != null)
                        results.Add(new WpContactData(name, phone, email, contactType));
                }

                if (result.TryGetProperty("ownership_info", out var oi) &&
                    oi.TryGetProperty("person_owners", out var owners) &&
                    owners.ValueKind == JsonValueKind.Array)
                    foreach (var o in owners.EnumerateArray()) ExtractPerson(o, "owner");

                if (result.TryGetProperty("residents", out var residents) &&
                    residents.ValueKind == JsonValueKind.Array)
                    foreach (var r in residents.EnumerateArray()) ExtractPerson(r, "resident");
            }
            catch { }
            return results;
        }

    }
}