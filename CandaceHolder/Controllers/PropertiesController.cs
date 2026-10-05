using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using CandaceHolder.Services;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CandaceHolder.Controllers
{
    /// <summary>
    /// Region → address lookup for the map. Every endpoint returns the same
    /// JSON shape: { centerAddress, lat, lng, count, properties: [{ address, lat, lng }] }.
    /// Addresses come from OpenStreetMap, falling back to a Google
    /// reverse-geocode grid when OSM has nothing for the area.
    /// </summary>
    [Authorize]
    [Route("[controller]")]
    public class PropertiesController : Controller
    {
        private readonly RealDataService                _realData;
        private readonly IMemoryCache                   _cache;
        private readonly string                         _apiKey;
        private readonly IWebHostEnvironment            _env;
        private readonly ILogger<PropertiesController>  _logger;

        private const string GeocodingBase = "https://maps.googleapis.com/maps/api/geocode/json";

        // OSM caps a single scan; keep the list to the searched address + 149 neighbours.
        private const int MaxNeighbours = 149;

        private static readonly JsonSerializerOptions CamelCase = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented        = false
        };

        public PropertiesController(RealDataService realData, IMemoryCache cache, IConfiguration config,
                                    IWebHostEnvironment env, ILogger<PropertiesController> logger)
        {
            _realData = realData;
            _cache    = cache;
            _env      = env;
            _logger   = logger;
            _apiKey   = config["GoogleMaps:ApiKey"] ?? "";
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /Properties/Neighborhood?address=...&radius=0.5
        // Every address within `radius` miles of the searched address.
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("Neighborhood")]
        public async Task<IActionResult> Neighborhood(
            string address, double radius = 0.5, double lat = 0, double lng = 0)
        {
            if (string.IsNullOrWhiteSpace(address))
                return BadRequest(new { error = "Address is required." });

            string formattedAddress = address;
            if (lat == 0 || lng == 0)
            {
                var center = await GeocodeAsync(address);
                if (center == null)
                    return BadRequest(new { error = "Could not geocode the provided address." });
                lat              = center.Lat;
                lng              = center.Lng;
                formattedAddress = center.FormattedAddress;
            }

            var properties = await GetPropertiesAsync(formattedAddress, lat, lng, radius);
            return Json(new { centerAddress = formattedAddress, lat, lng, count = properties.Count, properties }, CamelCase);
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /Properties/SingleAddress?address=...&lat=...&lng=...
        // Exactly one property — no neighbour scan.
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("SingleAddress")]
        public async Task<IActionResult> SingleAddress(string address, double lat = 0, double lng = 0)
        {
            if (string.IsNullOrWhiteSpace(address))
                return BadRequest(new { error = "Address is required." });

            string formattedAddress = address;
            if (lat == 0 || lng == 0)
            {
                var center = await GeocodeAsync(address);
                if (center == null)
                    return BadRequest(new { error = "Could not geocode the provided address." });
                lat              = center.Lat;
                lng              = center.Lng;
                formattedAddress = center.FormattedAddress;
            }

            var record = new PropertyRecord { Address = formattedAddress, Lat = Math.Round(lat, 6), Lng = Math.Round(lng, 6) };
            return Json(new { centerAddress = formattedAddress, lat, lng, count = 1, properties = new[] { record } }, CamelCase);
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /Properties/Area?north=..&south=..&east=..&west=..
        // The map's "draw a rectangle" tool. Scans the smallest circle that
        // covers the rectangle, then keeps only what's actually inside it.
        //
        // KNOWN LIMITATION: the scan caps neighbours at 149, proximity-sorted
        // from the circle's center — for a very elongated rectangle some of
        // those can land in the circle but outside the box, leaving fewer
        // results than a true bbox query would.
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("Area")]
        public async Task<IActionResult> Area(double north, double south, double east, double west)
        {
            if (north <= south || east <= west)
                return BadRequest(new { error = "Invalid area — draw a rectangle on the map first." });

            double centerLat = (north + south) / 2.0;
            double centerLng = (east + west) / 2.0;

            // Small margin so results right at the edge aren't clipped by
            // OSM's own radius search before we filter them.
            double radiusMiles = RealDataService.HaversineDistanceMiles(centerLat, centerLng, north, east) + 0.25;
            if (radiusMiles > 5.0)
                return BadRequest(new { error = "Selected area is too large — draw a smaller box (keep it under ~5 miles across)." });

            var properties = await GetPropertiesAsync($"{centerLat:F5},{centerLng:F5}", centerLat, centerLng, radiusMiles,
                                                      includeCenter: false);

            var inBox = properties
                .Where(p => p.Lat <= north && p.Lat >= south && p.Lng <= east && p.Lng >= west)
                .ToList();

            return Json(new { centerAddress = "Selected area", lat = centerLat, lng = centerLng, count = inBox.Count, properties = inBox }, CamelCase);
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /Properties/Export?address=...&radius=0.5
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("Export")]
        public async Task<IActionResult> Export(string address, double radius = 0.5, double lat = 0, double lng = 0)
        {
            if (string.IsNullOrWhiteSpace(address))
                return BadRequest("Address is required.");

            if (lat == 0 || lng == 0)
            {
                var center = await GeocodeAsync(address);
                if (center == null)
                    return BadRequest("Could not geocode the provided address.");
                lat = center.Lat;
                lng = center.Lng;
            }

            var properties = await GetPropertiesAsync(address, lat, lng, radius);

            var sb = new StringBuilder();
            sb.AppendLine("Address,Latitude,Longitude");
            foreach (var p in properties)
                sb.AppendLine($"\"{p.Address.Replace("\"", "\"\"")}\",{p.Lat},{p.Lng}");

            var bytes    = Encoding.UTF8.GetBytes(sb.ToString());
            var fileName = $"Addresses_{DateTime.Now:yyyyMMdd_HHmm}.csv";
            return File(bytes, "text/csv", fileName);
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /Properties/RegridDebug?address=...   (Development only)
        // Raw Regrid response, for checking owner-name lookups.
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("RegridDebug")]
        public async Task<IActionResult> RegridDebug(string address)
        {
            if (!_env.IsDevelopment())
                return NotFound();
            if (string.IsNullOrWhiteSpace(address))
                return BadRequest(new { error = "address is required" });

            var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var token  = config["Regrid:Token"] ?? "";
            if (string.IsNullOrWhiteSpace(token))
                return Json(new { error = "No Regrid token configured in appsettings.json" });

            var clean = address.Replace(", USA", "").Replace(", United States", "").Trim();
            var url   = $"https://app.regrid.com/api/v2/parcels/address" +
                        $"?query={Uri.EscapeDataString(clean)}&token={token}&limit=1&return_enhanced_ownership=true";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            try
            {
                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                return Json(new
                {
                    httpStatus  = (int)resp.StatusCode,
                    address     = clean,
                    bodyPreview = body.Length > 1000 ? body[..1000] : body,
                    totalLength = body.Length
                }, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /Properties/GridDebug?lat=..&lng=..&radius=0.5   (Development only)
        // Tests the Google reverse-geocode grid fallback directly.
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("GridDebug")]
        public async Task<IActionResult> GridDebug(double lat, double lng, double radius = 0.5)
        {
            if (!_env.IsDevelopment())
                return NotFound();

            var gridAddresses = await _realData.GetAddressesViaGoogleGridAsync(lat, lng, radius, _apiKey);
            return Json(new { count = gridAddresses.Count, sample = gridAddresses.Take(5) },
                        new JsonSerializerOptions { WriteIndented = true });
        }

        // ─────────────────────────────────────────────────────────────────
        // Core lookup: searched address first, then up to 149 neighbours
        // sorted by distance so the whole street shows before distant blocks.
        // ─────────────────────────────────────────────────────────────────
        private async Task<List<PropertyRecord>> GetPropertiesAsync(
            string centerAddress, double centerLat, double centerLng, double radiusMiles,
            bool includeCenter = true)
        {
            // OSM data changes rarely — cache each scan for 30 minutes.
            var cacheKey = $"osm:{centerLat:F4}:{centerLng:F4}:r{radiusMiles:F2}";
            if (!_cache.TryGetValue(cacheKey, out List<RealDataService.OsmAddress>? addresses) || addresses == null)
            {
                addresses = await SafeOsm(centerLat, centerLng, radiusMiles);

                // OSM is often empty in newer subdivisions — fall back to a Google reverse-geocode grid.
                if (addresses.Count == 0 && !string.IsNullOrWhiteSpace(_apiKey))
                    addresses = await _realData.GetAddressesViaGoogleGridAsync(centerLat, centerLng, radiusMiles, _apiKey);

                // Don't cache an empty result — it's usually a transient Overpass
                // timeout, and caching it would block retries for 30 minutes.
                if (addresses.Count > 0)
                    _cache.Set(cacheKey, addresses, TimeSpan.FromMinutes(30));
            }

            // Skip anything within ~150 ft of the center so the searched
            // address isn't listed twice under a slightly different spelling.
            const double dedupeThresholdMiles = 0.03;
            var neighbours = addresses
                .Select(a => new { a, dist = RealDataService.HaversineDistanceMiles(a.Lat, a.Lng, centerLat, centerLng) })
                .Where(x => !includeCenter || x.dist > dedupeThresholdMiles)
                .OrderBy(x => x.dist)
                .Take(MaxNeighbours)
                .Select(x => new PropertyRecord
                {
                    Address = x.a.FullAddress,
                    Lat     = Math.Round(x.a.Lat, 6),
                    Lng     = Math.Round(x.a.Lng, 6)
                });

            var records = new List<PropertyRecord>();
            if (includeCenter)
                records.Add(new PropertyRecord { Address = centerAddress, Lat = Math.Round(centerLat, 6), Lng = Math.Round(centerLng, 6) });
            records.AddRange(neighbours);
            return records;
        }

        private async Task<List<RealDataService.OsmAddress>> SafeOsm(double lat, double lng, double r)
        {
            try   { return await _realData.GetNearbyAddressesAsync(lat, lng, r); }
            catch (Exception ex) { _logger.LogError(ex, "OSM fetch failed"); return new(); }
        }

        // ─────────────────────────────────────────────────────────────────
        // Google Maps geocoding
        // ─────────────────────────────────────────────────────────────────
        private async Task<GeoResult?> GeocodeAsync(string address)
        {
            if (string.IsNullOrWhiteSpace(_apiKey)) return null;

            using var client = new HttpClient();
            var url = $"{GeocodingBase}?address={Uri.EscapeDataString(address)}&key={_apiKey}";
            try
            {
                var json = await client.GetStringAsync(url);
                using var doc  = JsonDocument.Parse(json);
                var       root = doc.RootElement;

                if (root.GetProperty("status").GetString() != "OK") return null;

                var result = root.GetProperty("results")[0];
                var loc    = result.GetProperty("geometry").GetProperty("location");
                return new GeoResult
                {
                    FormattedAddress = result.GetProperty("formatted_address").GetString() ?? address,
                    Lat              = loc.GetProperty("lat").GetDouble(),
                    Lng              = loc.GetProperty("lng").GetDouble()
                };
            }
            catch { return null; }
        }

        // DTOs

        private class GeoResult
        {
            public string FormattedAddress { get; set; } = "";
            public double Lat              { get; set; }
            public double Lng              { get; set; }
        }

        private class PropertyRecord
        {
            [JsonPropertyName("address")] public string Address { get; set; } = "";
            [JsonPropertyName("lat")]     public double Lat     { get; set; }
            [JsonPropertyName("lng")]     public double Lng     { get; set; }
        }
    }
}
