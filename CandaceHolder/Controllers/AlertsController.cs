using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using CandaceHolder.Data.Models;
using CandaceHolder.Services;
using System.Security.Claims;

namespace CandaceHolder.Controllers
{
    [Authorize]
    [Route("[controller]")]
    public class AlertsController : Controller
    {
        private readonly AppDbContext             _db;
        private readonly RealDataService          _realData;
        private readonly ILogger<AlertsController> _logger;
        private readonly bool                     _alertsEnabled;

        public AlertsController(AppDbContext db, RealDataService realData, ILogger<AlertsController> logger, IConfiguration config)
        {
            _db       = db;
            _realData = realData;
            _logger   = logger;
            _alertsEnabled = config.GetValue<bool>("FeatureFlags:AlertsEnabled");
        }

        private long? CurrentUserId =>
            long.TryParse(User.FindFirst("user_db_id")?.Value, out var id) ? id : null;

        private long? CurrentOrgId =>
            long.TryParse(User.FindFirst("user_org_id")?.Value, out var id) ? id : null;

        // ── GET /Alerts ───────────────────────────────────────────────────────
        // Not rolled out yet (2026-07-23) — bounce straight to home if someone hits this
        // URL directly (bookmark, old link) while the nav link is hidden everywhere else.
        [HttpGet("")]
        public IActionResult Index() => _alertsEnabled ? View() : Redirect("/");

        // ── GET /Alerts/List — returns JSON list of watched areas ─────────────
        [HttpGet("List")]
        public async Task<IActionResult> List()
        {
            var orgId = CurrentOrgId;
            var areas  = await _db.WatchedAreas
                .Where(w => w.OrgId == orgId || w.OrgId == null)
                .OrderByDescending(w => w.CreatedAt)
                .Select(w => new {
                    w.Id, w.Label, w.CenterLat, w.CenterLng,
                    w.RadiusMiles, w.MinHailSizeInches, w.AlertsEnabled, w.CreatedAt
                })
                .ToListAsync();

            return Json(areas);
        }

        // ── POST /Alerts — create a new watched area ──────────────────────────
        [HttpPost("")]
        public async Task<IActionResult> Create([FromBody] CreateWatchedAreaRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Address))
                return BadRequest(new { error = "Address is required." });

            // Geocode the address to get lat/lng
            var geoKey = HttpContext.RequestServices
                .GetRequiredService<IConfiguration>()["GoogleMaps:ApiKey"] ?? "";

            var geo = await GeocodeAsync(req.Address, geoKey);
            if (geo == null)
                return BadRequest(new { error = "Could not geocode that address. Try a more specific address or zip code." });

            var area = new WatchedArea
            {
                UserId            = CurrentUserId,
                OrgId             = CurrentOrgId,
                Label             = geo.FormattedAddress,
                CenterLat         = geo.Lat,
                CenterLng         = geo.Lng,
                RadiusMiles       = req.RadiusMiles,
                MinHailSizeInches = req.MinHailSizeInches,
                AlertsEnabled     = true,
                CreatedAt         = DateTime.UtcNow
            };

            _db.WatchedAreas.Add(area);
            await _db.SaveChangesAsync();

            return Json(new {
                area.Id, area.Label, area.CenterLat, area.CenterLng,
                area.RadiusMiles, area.MinHailSizeInches, area.AlertsEnabled
            });
        }

        // ── PATCH /Alerts/{id} — update radius, threshold, or toggle ─────────
        [HttpPatch("{id:long}")]
        public async Task<IActionResult> Update(long id, [FromBody] UpdateWatchedAreaRequest req)
        {
            var orgId = CurrentOrgId;
            var area  = await _db.WatchedAreas.FirstOrDefaultAsync(
                w => w.Id == id && (w.OrgId == orgId || w.OrgId == null));

            if (area == null) return NotFound();

            if (req.RadiusMiles.HasValue)       area.RadiusMiles       = req.RadiusMiles.Value;
            if (req.MinHailSizeInches.HasValue)  area.MinHailSizeInches = req.MinHailSizeInches.Value;
            if (req.AlertsEnabled.HasValue)      area.AlertsEnabled     = req.AlertsEnabled.Value;

            await _db.SaveChangesAsync();
            return NoContent();
        }

        // ── DELETE /Alerts/{id} ───────────────────────────────────────────────
        [HttpDelete("{id:long}")]
        public async Task<IActionResult> Delete(long id)
        {
            var orgId = CurrentOrgId;
            var area  = await _db.WatchedAreas.FirstOrDefaultAsync(
                w => w.Id == id && (w.OrgId == orgId || w.OrgId == null));

            if (area == null) return NotFound();

            _db.WatchedAreas.Remove(area);
            await _db.SaveChangesAsync();
            return NoContent();
        }

        // ── GET /Alerts/TestEmail — sends a sample alert to the current user ──
        [HttpGet("TestEmail")]
        public async Task<IActionResult> TestEmail()
        {
            var email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
            if (string.IsNullOrWhiteSpace(email))
            {
                _logger.LogWarning("TestEmail: no email address on current user");
                return BadRequest(new { error = "No email address found on your account. Sign in with a real account to test alerts." });
            }

            var emailSvc = HttpContext.RequestServices.GetRequiredService<EmailService>();

            if (!emailSvc.IsConfigured)
            {
                _logger.LogWarning("TestEmail: SMTP not configured");
                return BadRequest(new { error = "SMTP is not configured. Fill in Email settings in appsettings.json." });
            }

            _logger.LogInformation("TestEmail: sending sample alert to {Email}", email);

            var html = $"""
                <div style="font-family:sans-serif;max-width:560px;margin:0 auto;background:#0f172a;color:#e2e8f0;padding:32px;border-radius:12px;">
                    <div style="margin-bottom:24px;">
                        <span style="background:#f97316;color:white;padding:6px 14px;border-radius:999px;font-size:12px;font-weight:700;letter-spacing:.05em;">⚡ TEST ALERT</span>
                    </div>
                    <h1 style="font-size:22px;font-weight:800;margin:0 0 8px;">Hail Alert — Dallas, TX 75201</h1>
                    <p style="color:#94a3b8;font-size:14px;margin:0 0 24px;">This is a test email from StormLead Pro confirming your alert emails are working correctly.</p>
                    <div style="background:#1e293b;border-radius:10px;padding:20px;margin-bottom:24px;">
                        <table style="width:100%;font-size:14px;">
                            <tr><td style="color:#64748b;padding:4px 0;">Watch Area</td><td style="text-align:right;color:#f1f5f9;font-weight:600;">Dallas, TX (10-mile radius)</td></tr>
                            <tr><td style="color:#64748b;padding:4px 0;">Hail Size</td><td style="text-align:right;color:#f97316;font-weight:700;">1.75" ⛳ Golf Ball</td></tr>
                            <tr><td style="color:#64748b;padding:4px 0;">Event Date</td><td style="text-align:right;color:#f1f5f9;">{DateTime.UtcNow:MMMM d, yyyy}</td></tr>
                        </table>
                    </div>
                    <a href="https://stormlead.pro/?address=Dallas%2C+TX&radius=10" style="display:inline-block;background:#f97316;color:white;padding:12px 24px;border-radius:8px;font-weight:700;text-decoration:none;font-size:14px;">
                        🔍 Find Leads in This Area
                    </a>
                    <p style="color:#334155;font-size:12px;margin-top:24px;">
                        You received this because you have a watched area configured in StormLead Pro.<br/>
                        Sent to: {email}
                    </p>
                </div>
            """;

            var sent = await emailSvc.SendAsync(email, "⚡ [TEST] Storm Alert — Dallas, TX", html);

            if (sent)
            {
                _logger.LogInformation("TestEmail: successfully delivered to {Email}", email);
                return Json(new { ok = true, message = $"Test alert sent to {email}. Check your inbox (and spam folder)." });
            }
            else
            {
                _logger.LogError("TestEmail: SendAsync returned false for {Email}", email);
                return StatusCode(500, new { error = "Email send failed. Check the server logs for details." });
            }
        }

        // ── GET /Alerts/History — recent sent alerts for this org ────────────
        [HttpGet("History")]
        public async Task<IActionResult> History()
        {
            var orgId  = CurrentOrgId;
            var alerts = await _db.SentAlerts
                .Include(s => s.WatchedArea)
                .Where(s => s.OrgId == orgId || s.OrgId == null)
                .OrderByDescending(s => s.SentAt)
                .Take(50)
                .Select(s => new {
                    s.Id, s.EventDate, s.HailSizeInches, s.SentAt,
                    areaLabel = s.WatchedArea == null ? "" : s.WatchedArea.Label
                })
                .ToListAsync();

            return Json(alerts);
        }

        // ── Geocoding helper ──────────────────────────────────────────────────
        private static async Task<GeoResult?> GeocodeAsync(string address, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return null;
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var url  = $"https://maps.googleapis.com/maps/api/geocode/json" +
                           $"?address={Uri.EscapeDataString(address)}&key={apiKey}";
                var json = await client.GetStringAsync(url);
                using var doc  = System.Text.Json.JsonDocument.Parse(json);
                var       root = doc.RootElement;
                if (root.GetProperty("status").GetString() != "OK") return null;
                var result    = root.GetProperty("results")[0];
                var loc       = result.GetProperty("geometry").GetProperty("location");
                var formatted = result.GetProperty("formatted_address").GetString() ?? address;
                return new GeoResult(formatted, loc.GetProperty("lat").GetDouble(), loc.GetProperty("lng").GetDouble());
            }
            catch { return null; }
        }

        // ── DTOs ──────────────────────────────────────────────────────────────
        private record GeoResult(string FormattedAddress, double Lat, double Lng);

        public record CreateWatchedAreaRequest(
            string Address,
            double RadiusMiles       = 10.0,
            double MinHailSizeInches = 1.0);

        public record UpdateWatchedAreaRequest(
            double? RadiusMiles,
            double? MinHailSizeInches,
            bool?   AlertsEnabled);
    }
}
