using System.Text.Json;

namespace CandaceHolder.Services
{
    /// <summary>
    /// Wraps the external data sources:
    ///   1. OpenStreetMap Overpass API  — real nearby addresses   (no key needed)
    ///   2. Google reverse-geocode grid — address fallback        (GoogleMaps:ApiKey)
    ///   3. Regrid Parcel API           — property owner names     (free 25/day token)
    ///   4. BatchData / Whitepages Pro  — skip tracing (owner, phones, emails)
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

        public RealDataService(IHttpClientFactory factory, IConfiguration config, ILogger<RealDataService> logger)
        {
            _httpFactory = factory;
            _config      = config;
            _logger      = logger;
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
        // Nominatim reverse geocode — detect state for a map location
        //   Uses the same "overpass" HttpClient (no key, browser-style UA required)
        // ─────────────────────────────────────────────────────────────────
        public async Task<string> GetStateFromLatLngAsync(double lat, double lng)
        {
            try
            {
                using var client = _httpFactory.CreateClient("overpass");
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent", "CandaceHolder/1.0");
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
        // Haversine distance helper (used by PropertiesController)
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


        // ─────────────────────────────────────────────────────────────────
        // 4. BatchData  -  owner name + phones + emails (skip trace)
        //    Sign up: https://batchdata.com   (formerly BatchSkipTracing)
        //    Config:  "BatchData": { "ApiKey": "..." }
        //    Docs:    https://developer.batchdata.com/docs/batchdata/batchdata-v1/operations/create-a-property-skip-trace
        //    Billed per MATCHED property only. Up to 100 properties per request.
        //    By default BatchData drops TCPA-blacklisted phones and puts mobiles first.
        // ─────────────────────────────────────────────────────────────────
        public const int BatchDataMaxPerRequest = 100;

        public record SkipTracePhone(string Number, string? Type, bool IsDnc);
        public record SkipTraceResult(string? OwnerName, List<SkipTracePhone> Phones, List<string> Emails, bool IsLitigator);

        /// <summary>
        /// Skip traces up to 100 addresses in one request. The returned list lines up
        /// with <paramref name="addresses"/>: a result for a match, null for no match.
        /// Returns null (the whole list) if the request itself failed.
        /// </summary>
        public async Task<List<SkipTraceResult?>?> BatchDataSkipTraceAsync(
            string apiKey, IReadOnlyList<string> addresses)
        {
            if (addresses.Count == 0) return new();
            if (addresses.Count > BatchDataMaxPerRequest)
                throw new ArgumentException($"BatchData accepts at most {BatchDataMaxPerRequest} properties per request.");

            var payload = new
            {
                requests = addresses.Select(a =>
                {
                    var (street, city, state, zip) = SplitAddress(a);
                    return new { propertyAddress = new { street, city, state, zip } };
                }).ToArray()
            };

            try
            {
                using var client  = _httpFactory.CreateClient("batchdata");
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://api.batchdata.com/api/v1/property/skip-trace")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload),
                                                System.Text.Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Accept.ParseAdd("application/json");

                var resp = await client.SendAsync(request);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("BatchData skip trace returned {Status}: {Body}",
                        (int)resp.StatusCode, body.Length > 500 ? body[..500] : body);
                    return null;
                }

                var results = ParseBatchDataResponse(body, addresses.Count);
                _logger.LogInformation("BatchData skip trace: {Matched}/{Total} matched",
                    results.Count(r => r != null), addresses.Count);
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BatchData skip trace failed for {Count} address(es)", addresses.Count);
                return null;
            }
        }

        public static List<SkipTraceResult?> ParseBatchDataResponse(string json, int expected)
        {
            var list = new List<SkipTraceResult?>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                !results.TryGetProperty("persons", out var persons) ||
                persons.ValueKind != JsonValueKind.Array)
                return Enumerable.Repeat<SkipTraceResult?>(null, expected).ToList();

            // V1 returns one entry per requested property, in request order.
            foreach (var p in persons.EnumerateArray())
            {
                var matched = p.TryGetProperty("meta", out var meta) &&
                              meta.TryGetProperty("matched", out var m) && m.ValueKind == JsonValueKind.True;
                if (!matched) { list.Add(null); continue; }

                string? ownerName = null;
                if (p.TryGetProperty("name", out var name))
                {
                    var first = name.TryGetProperty("first", out var f) ? f.GetString() : null;
                    var last  = name.TryGetProperty("last",  out var l) ? l.GetString() : null;
                    var full  = string.Join(' ', new[] { first, last }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (full.Length > 0) ownerName = TitleCase(full);
                }

                // Person-level "dnc" object — exact shape isn't documented, so treat any
                // true-valued flag inside it as "this person is on a Do Not Call list".
                var personDnc = p.TryGetProperty("dnc", out var dncObj) && AnyTrue(dncObj);

                var phones = new List<SkipTracePhone>();
                if (p.TryGetProperty("phoneNumbers", out var nums) && nums.ValueKind == JsonValueKind.Array)
                {
                    foreach (var n in nums.EnumerateArray())
                    {
                        var number = n.TryGetProperty("number", out var nv) ? nv.GetString() : null;
                        if (string.IsNullOrWhiteSpace(number)) continue;
                        var type = n.TryGetProperty("type", out var tv) ? tv.GetString() : null;
                        var dnc  = personDnc ||
                                   (n.TryGetProperty("dnc", out var dv) && AnyTrue(dv));
                        phones.Add(new SkipTracePhone(FormatPhone(number), type, dnc));
                    }
                }

                var emails = new List<string>();
                if (p.TryGetProperty("emails", out var ems) && ems.ValueKind == JsonValueKind.Array)
                    foreach (var e in ems.EnumerateArray())
                        if (e.TryGetProperty("email", out var ev) && ev.GetString() is { Length: > 0 } addr)
                            emails.Add(addr);

                var litigator = p.TryGetProperty("litigator", out var lit) && lit.ValueKind == JsonValueKind.True;
                list.Add(new SkipTraceResult(ownerName, phones, emails, litigator));
            }

            while (list.Count < expected) list.Add(null);
            return list;
        }

        // True for a JSON true, or an object/array containing any true value.
        private static bool AnyTrue(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.True   => true,
            JsonValueKind.Object => el.EnumerateObject().Any(p => AnyTrue(p.Value)),
            JsonValueKind.Array  => el.EnumerateArray().Any(AnyTrue),
            _                    => false
        };

        // ─────────────────────────────────────────────────────────────────
        // BatchData wallet balance — free to call (no credits used).
        //   GET https://api.batchdata.com/api/v1/wallet/balance
        //   Needs a token with the wallet-balance permission.
        //   Docs: https://developer.batchdata.com/docs/batchdata/batchdata-v1/operations/get-a-wallet-balance
        // ─────────────────────────────────────────────────────────────────
        public record WalletBalance(decimal Balance, string Currency, DateTimeOffset? AsOf);

        /// <summary>Returns the balance, or null with an error message if the request failed.</summary>
        public async Task<(WalletBalance? Balance, string? Error)> GetBatchDataWalletBalanceAsync(string apiKey)
        {
            try
            {
                using var client  = _httpFactory.CreateClient("batchdata");
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.batchdata.com/api/v1/wallet/balance");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Accept.ParseAdd("application/json");

                var resp = await client.SendAsync(request);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("BatchData wallet balance returned {Status}: {Body}",
                        (int)resp.StatusCode, body.Length > 300 ? body[..300] : body);
                    return (null, (int)resp.StatusCode switch
                    {
                        401 => "BatchData rejected the API token.",
                        403 => "The BatchData token doesn't have the wallet-balance permission.",
                        _   => $"BatchData returned HTTP {(int)resp.StatusCode}."
                    });
                }

                using var doc = JsonDocument.Parse(body);
                var r = doc.RootElement.GetProperty("results");
                var balance  = r.GetProperty("balance").GetDecimal();
                var currency = r.TryGetProperty("currency", out var c) ? c.GetString() ?? "USD" : "USD";
                DateTimeOffset? asOf = r.TryGetProperty("asOf", out var a) && DateTimeOffset.TryParse(a.GetString(), out var t) ? t : null;
                return (new WalletBalance(balance, currency, asOf), null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BatchData wallet balance call failed");
                return (null, "Couldn't reach BatchData.");
            }
        }

        // "2145551234" → "(214) 555-1234"; anything else is returned as-is.
        private static string FormatPhone(string raw)
        {
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            if (digits.Length == 11 && digits[0] == '1') digits = digits[1..];
            return digits.Length == 10 ? $"({digits[..3]}) {digits[3..6]}-{digits[6..]}" : raw;
        }

        // "123 Main St, Dallas, TX 75201, USA" → ("123 Main St", "Dallas", "TX", "75201")
        public static (string Street, string City, string State, string Zip) SplitAddress(string address)
        {
            var cleaned  = address.Replace(", USA", "").Replace(", United States", "");
            var parts    = cleaned.Split(',');
            var street   = parts.Length > 0 ? parts[0].Trim() : cleaned.Trim();
            var city     = parts.Length > 1 ? parts[1].Trim() : "";
            var stateZip = parts.Length > 2 ? parts[2].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
            var state    = stateZip.Length > 0 ? stateZip[0].ToUpperInvariant() : "";
            var zip      = stateZip.Length > 1 ? stateZip[1] : "";
            return (street, city, state, zip);
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