using Microsoft.AspNetCore.Authorization;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using CandaceHolder.Data.Models;
using CandaceHolder.Services;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CandaceHolder.Controllers
{
    [Authorize]
    [Route("[controller]")]
    public class LeadsController : Controller 
    {
        private readonly AppDbContext        _db;
        private readonly IWebHostEnvironment _env;
        private readonly HailReportService   _reports;
        private readonly RealDataService     _realData;
        private readonly MeshSwathService    _mesh;
        private readonly IConfiguration      _config;
        private readonly ReportCreditService _reportCredits;

        public LeadsController(AppDbContext db, IWebHostEnvironment env, HailReportService reports, RealDataService realData, MeshSwathService mesh, IConfiguration config, ReportCreditService reportCredits)
        {
            _db       = db;
            _env      = env;
            _reports  = reports;
            _realData = realData;
            _mesh     = mesh;
            _config   = config;
            _reportCredits = reportCredits;
        }

        private long? CurrentUserId =>
            long.TryParse(User.FindFirst("user_db_id")?.Value, out var id) ? id : null;

        private long? CurrentOrgId =>
            long.TryParse(User.FindFirst("user_org_id")?.Value, out var id) ? id : null;

        private string CurrentOrgRole =>
            User.FindFirst("user_org_role")?.Value ?? "rep";

        private bool CanEnrich =>
            _config.GetValue<bool>("FeatureFlags:EnrichmentEnabled")
            && CurrentOrgRole is "owner" or "manager";

        // ── GET /Leads/Saved → HTML page ────────────────────────────
        [HttpGet("Saved")]
        public IActionResult Saved() => View();

        // ── GET /Leads/{id} → per-address detail page ───────────────
        [HttpGet("{id:long}")]
        public async Task<IActionResult> Detail(long id)
        {
            var orgId = CurrentOrgId;
            var lead  = await _db.Leads
                .Include(l => l.Contacts)
                .Include(l => l.Enrichments)
                .FirstOrDefaultAsync(l => l.Id == id &&
                    (l.OrgId == orgId || l.OrgId == null) &&
                    l.DeletedAt == null);

            if (lead == null) return NotFound();

            ViewBag.CanEnrich = CanEnrich;
            return View(lead);
        }

        // ── GET /Leads?tab=pipeline|closed|archived ──────────────────
        // "New Leads" tab retired — new/unstarted leads now live in the pipeline
        // (enrichment is disabled, so there's no pre-pipeline step anymore).
        [HttpGet]
        public async Task<IActionResult> Index(string tab = "pipeline")
        {
            var orgId = CurrentOrgId;

            // Pipeline = everything active and not yet closed, including brand-new leads.
            var pipelineStatuses = new[] { "new", "contacted", "appointment_set" };
            var closedStatuses   = new[] { "closed_won", "closed_lost" };

            IQueryable<Lead> query;
            if (tab == "archived")
            {
                query = _db.Leads
                    .Where(l => (l.OrgId == orgId || l.OrgId == null) && l.DeletedAt != null);
            }
            else
            {
                query = _db.Leads
                    .Where(l => (l.OrgId == orgId || l.OrgId == null) && l.DeletedAt == null);
                query = tab switch
                {
                    "closed"   => query.Where(l => closedStatuses.Contains(l.Status)),
                    _          => query.Where(l => l.Status == null || pipelineStatuses.Contains(l.Status))  // pipeline (default)
                };
            }

            var leads = await query
                .OrderBy(l => l.RiskLevel == "High" ? 0 : l.RiskLevel == "Medium" ? 1 : 2)
                .ThenByDescending(l => l.SavedAt)
                .Select(l => new
                {
                    l.Id, l.Address, l.Lat, l.Lng,
                    l.RiskLevel, l.LastStormDate, l.HailSize,
                    l.EstimatedDamage, l.PropertyType,
                    l.SourceAddress, l.SavedAt, l.Notes,
                    l.OwnerName, l.OwnerPhone, l.OwnerEmail,
                    l.YearBuilt, l.IsEnriched, l.Status,
                    Contacts = l.Contacts.Select(c => new {
                        c.Id, c.Name, c.Phone, c.Email,
                        c.ContactType, c.IsPrimary, c.Source
                    }).ToList()
                })
                .ToListAsync();

            return Json(leads);
        }

        // ── POST /Leads/Save ─────────────────────────────────────────
        [HttpPost("Save")]
        public async Task<IActionResult> Save([FromBody] SaveRequest req)
        {
            if (req?.Properties == null || req.Properties.Length == 0)
                return BadRequest(new { error = "No properties provided." });

            var userId = CurrentUserId;
            var orgId  = CurrentOrgId;
            int saved = 0, updated = 0;

            foreach (var p in req.Properties)
            {
                if (string.IsNullOrWhiteSpace(p.Address)) continue;

                var existing = await _db.Leads.FirstOrDefaultAsync(l => l.Address == p.Address);
                if (existing != null)
                {
                    // Restore if previously archived
                    existing.DeletedAt       = null;
                    existing.Lat             = p.Lat;
                    existing.Lng             = p.Lng;
                    existing.RiskLevel       = p.RiskLevel;
                    existing.LastStormDate   = p.LastStormDate;
                    existing.HailSize        = p.HailSize;
                    existing.EstimatedDamage = p.EstimatedDamage;
                    existing.RoofAge         = p.RoofAge;
                    existing.PropertyType    = p.PropertyType;
                    existing.SourceAddress   = req.SourceAddress;
                    existing.SavedAt         = DateTime.UtcNow;
                    existing.UserId          = userId;
                    existing.OrgId           = orgId;
                    updated++;
                }
                else
                {
                    _db.Leads.Add(new Lead
                    {
                        Address         = p.Address,
                        Lat             = p.Lat,
                        Lng             = p.Lng,
                        RiskLevel       = p.RiskLevel,
                        LastStormDate   = p.LastStormDate,
                        HailSize        = p.HailSize,
                        EstimatedDamage = p.EstimatedDamage,
                        RoofAge         = p.RoofAge,
                        PropertyType    = p.PropertyType,
                        SourceAddress   = req.SourceAddress,
                        SavedAt         = DateTime.UtcNow,
                        UserId          = userId,
                        OrgId           = orgId
                    });
                    saved++;
                }
            }

            await _db.SaveChangesAsync();
            return Json(new { saved, updated });
        }

        // ── PATCH /Leads/{id}/Owner ──────────────────────────────────
        [HttpPatch("{id:long}/Owner")]
        public async Task<IActionResult> UpdateOwner(long id, [FromBody] OwnerDto dto)
        {
            var lead = await _db.Leads.FindAsync(id);
            if (lead == null) return NotFound();

            lead.OwnerName  = dto.OwnerName;
            lead.OwnerPhone = dto.OwnerPhone;
            lead.OwnerEmail = dto.OwnerEmail;

            await _db.SaveChangesAsync();
            return NoContent();
        }

        // ── POST /Leads/{id}/Enrich ──────────────────────────────────
        [HttpPost("{id:long}/Enrich")]
        public async Task<IActionResult> Enrich(long id)
        {
            if (!_config.GetValue<bool>("FeatureFlags:EnrichmentEnabled"))
                return StatusCode(503, new { error = "Enrichment is currently disabled." });
            if (!CanEnrich)
                return StatusCode(403, new { error = "Reps cannot run enrichment. Ask an owner or manager." });

            var orgId = CurrentOrgId;
            var credit = orgId.HasValue
                ? await _db.OrgCredits.FirstOrDefaultAsync(c => c.OrgId == orgId && c.CreditType == "enrichment")
                : null;
            if (credit != null && credit.Balance <= 0)
                return StatusCode(402, new { error = "No enrichment credits remaining. Top up your plan to continue." });

            var lead = await _db.Leads.FindAsync(id);
            if (lead == null) return NotFound(new { error = "Lead not found." });

            return Json(await EnrichLeadAsync(lead, credit));
        }

        // ── GET /Leads/{id}/Report — download hail damage PDF ────────
        // Gated on report-credit balance behind FeatureFlags:ReportCreditsEnabled
        // (see ReportCreditService, docs/pricing-refresh-punchlist.md). The flag
        // is OFF by default so this stays unlimited/unchanged until existing
        // orgs have a starting balance and Stripe checkout exists for orgs that
        // run out — flip it on only after both are true.
        [HttpGet("{id:long}/Report")]
        public async Task<IActionResult> Report(long id)
        {
            var orgId = CurrentOrgId;
            var lead  = await _db.Leads
                .FirstOrDefaultAsync(l => l.Id == id &&
                    (l.OrgId == orgId || l.OrgId == null) &&
                    l.DeletedAt == null);

            if (lead == null) return NotFound();

            var reportCreditsEnabled = _config.GetValue<bool>("FeatureFlags:ReportCreditsEnabled");
            var isUnlimitedPlan = false;
            if (reportCreditsEnabled && orgId.HasValue)
            {
                var creditOrg = await _db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId.Value);
                isUnlimitedPlan = PricingCatalog.IsUnlimitedPlan(creditOrg?.Plan);
                if (!isUnlimitedPlan)
                {
                    var balance = await _reportCredits.GetBalanceAsync(orgId.Value);
                    if (balance <= 0)
                        return StatusCode(402, new { error = "No report credits remaining. Upgrade your plan or buy more reports to continue." });
                }
            }

            // Fetch storm history when we have coordinates
            List<RealDataService.HailEvent>? hailHistory = null;
            List<RealDataService.WindEvent>?  windHistory = null;

            if (lead.Lat.HasValue && lead.Lng.HasValue)
            {
                var lat = lead.Lat.Value;
                var lng = lead.Lng.Value;

                // Auto-detect state for LSR queries
                var stateAbbr = "";
                if (!string.IsNullOrWhiteSpace(lead.Address))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        lead.Address, @"\b([A-Z]{2})\b\s*\d{5}");
                    if (m.Success) stateAbbr = m.Groups[1].Value;
                }
                if (stateAbbr.Length != 2)
                    stateAbbr = await _realData.GetStateFromLatLngAsync(lat, lng);

                const double fetch   = 10.0;
                // Widened from 2 mi to match storm history — rural reports log at the
                // nearest town, so 2 mi hid real storms. Report shows each event's distance.
                const double display = 10.0;
                var fiveYearsAgo = DateTime.UtcNow.AddYears(-5);
                var oneYearAgo   = DateTime.UtcNow.AddYears(-1);

                var swdiTask   = _realData.GetSwdiHailEventsAsync(lat, lng, fetch);
                var lsrTask    = string.IsNullOrWhiteSpace(stateAbbr)
                    ? Task.FromResult(new List<RealDataService.HailEvent>())
                    : _realData.GetMesonetLsrHailAsync(lat, lng, fetch, stateAbbr);
                var seTask     = string.IsNullOrWhiteSpace(stateAbbr)
                    ? Task.FromResult(new List<RealDataService.HailEvent>())
                    : _realData.GetStormEventsHailAsync(lat, lng, fetch, stateAbbr);
                var windTask   = string.IsNullOrWhiteSpace(stateAbbr)
                    ? Task.FromResult(new List<RealDataService.WindEvent>())
                    : _realData.GetMesonetLsrWindAsync(lat, lng, fetch, stateAbbr, lookbackDays: 365);
                // Tomorrow.io fills the NOAA ~90–120 day radar lag with near-real-time hail —
                // property search includes it, so the report must too or recent storms are missed.
                var tomorrowKey = HttpContext.RequestServices.GetService<IConfiguration>()?["TomorrowIo:ApiKey"] ?? "";
                var tomorrowTask = string.IsNullOrWhiteSpace(tomorrowKey)
                    ? Task.FromResult(new List<RealDataService.HailEvent>())
                    : _realData.GetTomorrowIoHailAsync(lat, lng, tomorrowKey);

                try { await Task.WhenAll(swdiTask, lsrTask, seTask, windTask, tomorrowTask); } catch { /* partial results OK */ }

                var allHail = new List<RealDataService.HailEvent>();
                if (swdiTask.IsCompletedSuccessfully)     allHail.AddRange(swdiTask.Result);
                if (lsrTask.IsCompletedSuccessfully)      allHail.AddRange(lsrTask.Result);
                if (seTask.IsCompletedSuccessfully)       allHail.AddRange(seTask.Result);
                if (tomorrowTask.IsCompletedSuccessfully) allHail.AddRange(tomorrowTask.Result);

                var radiusHailHistory = allHail
                    .Where(e => e.Date >= fiveYearsAgo &&
                                RealDataService.HaversineDistanceMiles(lat, lng, e.Lat, e.Lng) <= display)
                    .GroupBy(e => e.Date.Date)
                    .Select(g => g.OrderByDescending(e => e.SizeInches).First())
                    .OrderByDescending(e => e.Date)
                    .ToList();

                // Location-specific accuracy pass (docs/pdf-report-accuracy-punchlist.md item 3).
                // Point-radius attribution wildly over-includes hail for a given property — validated
                // 2026-07-16 against two real addresses via /RoofHealth/SwathCompareDebug: one showed
                // only 2 of 21 radius-attributed dates actually confirmed by the radar swath at that
                // exact parcel, the other only 1 of 28. For each radius candidate date, ask whether
                // MRMS MESH actually covers this property that day:
                //   Contained      -> keep the day, but use the radar-confirmed size band.
                //   ConfirmedClear -> drop the day — the storm genuinely never reached this address.
                //   Unknown        -> pipeline failed or date unsupported; keep the original
                //                      point-radius entry rather than treat "couldn't verify" as
                //                      "no hail" (see MeshSwathService.GetContainmentAsync).
                // Gated behind FeatureFlags:MeshSwaths so this can be rolled back instantly (falls
                // back to plain point-radius, the previously-shipped behavior) without a redeploy.
                // Not capped per-report — each candidate date is a separate grib+GDAL run on a cache
                // miss, throttled by MeshSwathService's own 2-concurrent pipeline gate; both
                // validation runs (21 and 28 candidate dates) completed without issue, but a
                // storm-heavy property could still make this request noticeably slower than before.
                if (_config.GetValue<bool>("FeatureFlags:MeshSwaths"))
                {
                    var refined = new List<RealDataService.HailEvent>();
                    foreach (var e in radiusHailHistory)
                    {
                        var containment = await _mesh.GetContainmentAsync(lat, lng, e.Date);
                        switch (containment.Status)
                        {
                            case MeshContainmentStatus.Contained:
                                refined.Add(e with { SizeInches = containment.SizeBandInches ?? e.SizeInches, Source = "mesh" });
                                break;
                            case MeshContainmentStatus.ConfirmedClear:
                                break;
                            case MeshContainmentStatus.Unknown:
                            default:
                                refined.Add(e);
                                break;
                        }
                    }
                    hailHistory = refined.OrderByDescending(e => e.Date).ToList();
                }
                else
                {
                    hailHistory = radiusHailHistory;
                }

                if (windTask.IsCompletedSuccessfully)
                    windHistory = windTask.Result
                        .Where(w => w.Date >= oneYearAgo &&
                                    RealDataService.HaversineDistanceMiles(lat, lng, w.Lat, w.Lng) <= display)
                        .GroupBy(w => w.Date.Date)
                        .Select(g => g.OrderByDescending(w => w.SpeedMph).First())
                        .OrderByDescending(w => w.Date)
                        .ToList();
            }

            // Load org branding
            Data.Models.Org? org = null;
            byte[]? logoBytes = null;
            string? logoSvgContent = null;
            if (orgId.HasValue)
            {
                org = await _db.Orgs.FirstOrDefaultAsync(o => o.Id == orgId);
                if (org != null && !string.IsNullOrWhiteSpace(org.LogoPath))
                {
                    var logosDir = Path.Combine(_env.ContentRootPath, "App_Data", "logos");
                    var logoPath = Path.Combine(logosDir, Path.GetFileName(org.LogoPath));
                    if (System.IO.File.Exists(logoPath))
                    {
                        // SVG is a valid upload format (CompanyController) but is a vector format —
                        // QuestPDF's .Image() only decodes raster bytes and throws "Cannot load or
                        // decode provided image" on SVG, which was crashing report generation with a
                        // 500 (found 2026-07-23 after Jordan uploaded an SVG logo). SVGs need QuestPDF's
                        // separate .Svg() element, which takes markup text rather than raw bytes.
                        if (string.Equals(Path.GetExtension(logoPath), ".svg", StringComparison.OrdinalIgnoreCase))
                            logoSvgContent = await System.IO.File.ReadAllTextAsync(logoPath);
                        else
                            logoBytes = await System.IO.File.ReadAllBytesAsync(logoPath);
                    }
                }
            }

            // Fetch static map image for the property
            byte[]? mapBytes = null;
            if (lead.Lat.HasValue && lead.Lng.HasValue)
            {
                try
                {
                    var mapsKey = HttpContext.RequestServices
                        .GetService<IConfiguration>()?["GoogleMaps:ApiKey"] ?? "";
                    if (!string.IsNullOrWhiteSpace(mapsKey))
                    {
                        var mapUrl = $"https://maps.googleapis.com/maps/api/staticmap" +
                            $"?center={lead.Lat:F6},{lead.Lng:F6}" +
                            $"&zoom=16&size=480x260&maptype=roadmap" +
                            $"&markers=color:red|{lead.Lat:F6},{lead.Lng:F6}" +
                            $"&key={mapsKey}";
                        using var http = new System.Net.Http.HttpClient();
                        http.Timeout = TimeSpan.FromSeconds(8);
                        mapBytes = await http.GetByteArrayAsync(mapUrl);
                    }
                }
                catch { /* map is optional — don't fail PDF generation */ }
            }

            var generatedBy = User.Identity?.Name ?? "StormLead Pro User";
            var pdf         = _reports.Generate(lead, generatedBy, hailHistory, windHistory, org, logoBytes, mapBytes, logoSvgContent);

            if (reportCreditsEnabled && orgId.HasValue && !isUnlimitedPlan)
            {
                await _reportCredits.ConsumeAsync(orgId.Value, 1, CurrentUserId,
                    referenceId: lead.Id.ToString(), referenceType: "lead_report",
                    description: $"Report generated: {lead.Address}");
            }

            var filename = $"HailReport-{SlugifyAddress(lead.Address)}-{DateTime.Now:yyyyMMdd}.pdf";
            return File(pdf, "application/pdf", filename);
        }

        // Builds a filesystem-safe slug from a street address for use in download filenames,
        // e.g. "123 Main St, Denver, CO" -> "123-Main-St-Denver-CO".
        private static string SlugifyAddress(string? address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return "Report";

            var slug = Regex.Replace(address, @"[^A-Za-z0-9]+", "-").Trim('-');
            return string.IsNullOrEmpty(slug) ? "Report" : slug;
        }

        // ── POST /Leads/BulkEnrich ───────────────────────────────────
        [HttpPost("BulkEnrich")]
        public async Task<IActionResult> BulkEnrich([FromBody] BulkRequest req)
        {
            if (!_config.GetValue<bool>("FeatureFlags:EnrichmentEnabled"))
                return StatusCode(503, new { error = "Enrichment is currently disabled." });
            if (!CanEnrich)
                return StatusCode(403, new { error = "Reps cannot run enrichment. Ask an owner or manager." });

            if (req?.Ids == null || req.Ids.Length == 0)
                return BadRequest(new { error = "No lead IDs provided." });

            var orgId  = CurrentOrgId;
            var credit = orgId.HasValue
                ? await _db.OrgCredits.FirstOrDefaultAsync(c => c.OrgId == orgId && c.CreditType == "enrichment")
                : null;
            if (credit != null && credit.Balance <= 0)
                return StatusCode(402, new { error = "No enrichment credits remaining. Top up your plan to continue." });

            var leads  = await _db.Leads
                .Where(l => req.Ids.Contains(l.Id) &&
                            (l.OrgId == orgId || l.OrgId == null) &&
                            !l.IsEnriched && l.DeletedAt == null)
                .ToListAsync();

            var results = new List<object>();
            foreach (var lead in leads)
            {
                // Re-check balance before each enrich in a bulk run
                if (credit != null && credit.Balance <= 0)
                {
                    results.Add(new { id = lead.Id, result = new { status = "no_credits" } });
                    continue;
                }
                var r = await EnrichLeadAsync(lead, credit);
                results.Add(new { id = lead.Id, result = r });
            }

            return Json(new { processed = results.Count, results });
        }

        // ── PATCH /Leads/{id}/Notes ─────────────────────────────────
        [HttpPatch("{id:long}/Notes")]
        public async Task<IActionResult> PatchNotes(long id, [FromBody] PatchNotesRequest req)
        {
            var orgId = CurrentOrgId;
            var lead  = await _db.Leads.FindAsync(id);
            if (lead == null || (lead.OrgId != orgId && lead.OrgId != null))
                return NotFound(new { error = "Lead not found." });

            lead.Notes = req.Notes?.Trim();
            await _db.SaveChangesAsync();
            return Json(new { saved = true });
        }

        // ── PATCH /Leads/{id}/Status ─────────────────────────────────
        [HttpPatch("{id:long}/Status")]
        public async Task<IActionResult> PatchStatus(long id, [FromBody] PatchStatusRequest req)
        {
            var valid = new[] { "new", "contacted", "appointment_set", "closed_won", "closed_lost" };
            if (!valid.Contains(req.Status))
                return BadRequest(new { error = "Invalid status value." });

            var orgId = CurrentOrgId;
            var lead  = await _db.Leads.FindAsync(id);
            if (lead == null || (lead.OrgId != orgId && lead.OrgId != null))
                return NotFound(new { error = "Lead not found." });

            lead.Status = req.Status;
            await _db.SaveChangesAsync();
            return Json(new { saved = true, status = req.Status });
        }

        // ── POST /Leads/{id}/Restore ─────────────────────────────────
        [HttpPost("{id:long}/Restore")]
        public async Task<IActionResult> Restore(long id)
        {
            var orgId = CurrentOrgId;
            var lead  = await _db.Leads.FindAsync(id);
            if (lead == null || (lead.OrgId != orgId && lead.OrgId != null))
                return NotFound(new { error = "Lead not found." });

            lead.DeletedAt = null;
            await _db.SaveChangesAsync();
            return Json(new { restored = true });
        }

        // ── POST /Leads/BulkArchive ──────────────────────────────────
        // Soft-deletes leads — enriched leads are protected and skipped.
        [HttpPost("BulkArchive")]
        public async Task<IActionResult> BulkArchive([FromBody] BulkRequest req)
        {
            if (req?.Ids == null || req.Ids.Length == 0)
                return BadRequest(new { error = "No lead IDs provided." });

            var orgId  = CurrentOrgId;
            var leads  = await _db.Leads
                .Where(l => req.Ids.Contains(l.Id) &&
                            (l.OrgId == orgId || l.OrgId == null) &&
                            !l.IsEnriched && l.DeletedAt == null)
                .ToListAsync();

            var now = DateTime.UtcNow;
            foreach (var lead in leads)
                lead.DeletedAt = now;

            await _db.SaveChangesAsync();
            return Json(new { archived = leads.Count });
        }

        // ── POST /Leads/BulkDelete ───────────────────────────
        // Soft-deletes all matching leads owned by the current org
        // (enriched or not). Used by the bulk-actions toolbar.
        [HttpPost("BulkDelete")]
        public async Task<IActionResult> BulkDelete([FromBody] BulkRequest req)
        {
            if (req?.Ids == null || req.Ids.Length == 0)
                return BadRequest(new { error = "No lead IDs provided." });

            var orgId  = CurrentOrgId;
            var leads  = await _db.Leads
                .Where(l => req.Ids.Contains(l.Id) &&
                            (l.OrgId == orgId || l.OrgId == null) &&
                            l.DeletedAt == null)
                .ToListAsync();

            var now = DateTime.UtcNow;
            foreach (var lead in leads)
                lead.DeletedAt = now;

            await _db.SaveChangesAsync();
            return Json(new { archived = leads.Count });
        }

        // ── GET /Leads/Stats ─────────────────────────────────────────
        [HttpGet("Stats")]
        public async Task<IActionResult> Stats()
        {
            var orgId  = CurrentOrgId;
            var userId = CurrentUserId;
            var now    = DateTime.UtcNow;
            var som    = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            var allLeadsQ    = _db.Leads.Where(l => l.OrgId == orgId || l.OrgId == null);
            var activeLeadsQ = allLeadsQ.Where(l => l.DeletedAt == null);
            var enrichmentsQ = _db.Enrichments.Where(e => e.UserId == userId);

            var enrichCredit = orgId.HasValue
                ? await _db.OrgCredits.FirstOrDefaultAsync(c => c.OrgId == orgId && c.CreditType == "enrichment")
                : null;

            return Json(new
            {
                totalLeads              = await activeLeadsQ.CountAsync(),
                leadsThisMonth          = await activeLeadsQ.CountAsync(l => l.SavedAt >= som),
                unenrichedCount         = await activeLeadsQ.CountAsync(l => l.Status == "new" || l.Status == null),
                pipelineCount           = await activeLeadsQ.CountAsync(l => l.Status == null || new[] { "new", "contacted", "appointment_set" }.Contains(l.Status)),
                closedCount             = await activeLeadsQ.CountAsync(l => new[] { "closed_won", "closed_lost" }.Contains(l.Status)),
                archivedCount           = await allLeadsQ.CountAsync(l => l.DeletedAt != null),
                totalEnrichments        = await enrichmentsQ.CountAsync(),
                enrichmentsThisMonth    = await enrichmentsQ.CountAsync(e => e.CreatedAt >= som),
                enrichCreditsRemaining  = enrichCredit?.Balance,
                enrichCreditsUsed       = enrichCredit?.UsedThisPeriod,
                canEnrich               = CanEnrich
            });
        }

        // ── DELETE /Leads/{id} — soft delete ─────────────────────────
        [HttpDelete("{id:long}")]
        public async Task<IActionResult> Delete(long id)
        {
            var lead = await _db.Leads.FindAsync(id);
            if (lead == null) return NotFound();
            // Enrichment guard removed — leads are deletable regardless of enrichment state

            lead.DeletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return NoContent();
        }

        // ─────────────────────────────────────────────────────────────
        // Shared enrichment logic
        // ─────────────────────────────────────────────────────────────
        private async Task<object> EnrichLeadAsync(Lead lead, Data.Models.OrgCredit? credit = null)
        {
            var services  = HttpContext.RequestServices;
            var config    = services.GetService<IConfiguration>();
            var realData  = services.GetRequiredService<RealDataService>();
            var bstApiKey = config?["BatchSkipTracing:ApiKey"];
            var wpApiKey  = config?["WhitepagesPro:ApiKey"];

            string? ownerName = null;
            int?    yearBuilt = null;
            string  provider  = "regrid";
            string  status    = "not_found";

            // ── Step 1: Regrid — owner name + year built ─────────────
            var parcel = await realData.GetRegridParcelDataAsync(
                lead.Lat ?? 0, lead.Lng ?? 0, lead.Address);

            if (parcel != null)
            {
                ownerName = parcel.OwnerName;
                yearBuilt = parcel.YearBuilt;
                status    = "completed";

                if (ownerName != null && lead.OwnerName == null)
                    lead.OwnerName = ownerName;
                if (yearBuilt != null)
                    lead.YearBuilt = yearBuilt;
            }

            // ── Step 2: Whitepages Pro — phone + email ────────────────
            if (!string.IsNullOrWhiteSpace(wpApiKey))
            {
                provider = "whitepages";
                var contacts = await realData.GetWhitepagesContactAsync(
                    wpApiKey, lead.OwnerName, lead.Address);

                if (contacts.Count > 0)
                {
                    // Remove stale contacts from a previous enrich
                    var old = _db.LeadContacts.Where(c => c.LeadId == lead.Id);
                    _db.LeadContacts.RemoveRange(old);

                    for (int i = 0; i < contacts.Count; i++)
                    {
                        var c       = contacts[i];
                        var primary = i == 0;

                        _db.LeadContacts.Add(new Data.Models.LeadContact
                        {
                            LeadId      = lead.Id,
                            Name        = c.OwnerName,
                            Phone       = c.Phone,
                            Email       = c.Email,
                            ContactType = c.ContactType,
                            IsPrimary   = primary,
                            Source      = "whitepages",
                            CreatedAt   = DateTime.UtcNow
                        });

                        // Populate legacy fields from the primary contact
                        if (primary)
                        {
                            if (c.OwnerName != null && lead.OwnerName == null)
                                lead.OwnerName = c.OwnerName;
                            if (c.Phone != null && lead.OwnerPhone == null)
                                lead.OwnerPhone = c.Phone;
                            if (c.Email != null && lead.OwnerEmail == null)
                                lead.OwnerEmail = c.Email;
                        }
                    }

                    status = "completed";
                }
            }
            // ── Step 2b: BatchSkipTracing fallback (if WP not configured) ─
            else if (!string.IsNullOrWhiteSpace(bstApiKey))
            {
                provider = "batchskiptracing";
                var contact = await realData.GetBstContactAsync(
                    bstApiKey, lead.OwnerName, lead.Address);

                if (contact != null)
                {
                    if (contact.Phone != null && lead.OwnerPhone == null)
                        lead.OwnerPhone = contact.Phone;
                    if (contact.Email != null && lead.OwnerEmail == null)
                        lead.OwnerEmail = contact.Email;

                    status = "completed";
                }
            }

            // Always mark as enriched once a lookup has been attempted —
            // even "not_found" results move the lead out of the unenriched queue
            // so it doesn't get retried repeatedly. The Enrichments record captures
            // the actual outcome (completed vs not_found).
            lead.IsEnriched = true;

            _db.Enrichments.Add(new Enrichment
            {
                UserId      = CurrentUserId,
                LeadId      = lead.Id,
                Address     = lead.Address,
                Status      = status,
                Provider    = provider,
                CreditsUsed = 1,
                CreatedAt   = DateTime.UtcNow
            });

            // Deduct one enrichment credit and write an immutable ledger entry
            if (credit != null)
            {
                credit.Balance        = Math.Max(0, credit.Balance - 1);
                credit.UsedThisPeriod += 1;
                credit.UpdatedAt      = DateTime.UtcNow;

                _db.OrgCreditTransactions.Add(new Data.Models.OrgCreditTransaction
                {
                    OrgId         = credit.OrgId,
                    UserId        = CurrentUserId,
                    CreditType    = "enrichment",
                    Amount        = -1,
                    BalanceAfter  = credit.Balance,
                    Description   = $"Lead enriched: {lead.Address}",
                    ReferenceId   = lead.Id.ToString(),
                    ReferenceType = "lead",
                    CreatedAt     = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync();

            return new { status, ownerName, yearBuilt, ownerPhone = lead.OwnerPhone, ownerEmail = lead.OwnerEmail };
        }

        // ── GET /Leads/WpDebug?name=John+Smith&address=123+Main+St,Dallas,TX+75201 ──
        // Dev-only — blocked in production (non-Development environments)
        [HttpGet("WpDebug")]
        public async Task<IActionResult> WpDebug(string? name, string? address, bool mock = false)
        {
            if (!_env.IsDevelopment())
                return NotFound();

            var config   = HttpContext.RequestServices.GetService<IConfiguration>();
            var realData = HttpContext.RequestServices.GetRequiredService<RealDataService>();
            var apiKey   = config?["WhitepagesPro:ApiKey"];

            // ?mock=true — parse the sample response without hitting the API
            if (mock)
            {
                const string sampleJson = """
                {
                  "result": {
                    "ownership_info": {
                      "owner_type": "Business",
                      "business_owners": [{ "name": "United States Of America" }],
                      "person_owners": [{
                        "id": "PX3vr2aM2E3",
                        "name": "Donald Duck",
                        "phones": [{ "number": "12015215520", "type": "Landline" }],
                        "emails": [{ "email": "sample.email@gmail.com" }]
                      }]
                    },
                    "residents": [{
                      "id": "PX3vr2aM2E3",
                      "name": "Donald Duck",
                      "phones": [{ "number": "12015215520", "type": "Landline" }],
                      "emails": [{ "email": "sample.email@gmail.com" }]
                    }]
                  }
                }
                """;
                var parsed = realData.ParseWpResponsePublic(sampleJson);
                return Json(new { mock = true, contacts = parsed });
            }

            if (string.IsNullOrWhiteSpace(apiKey))
                return Content("WhitepagesPro:ApiKey not configured", "text/plain");
            if (string.IsNullOrWhiteSpace(address))
                return Content("Pass ?address=123+Main+St,City,ST+Zip  or  ?mock=true", "text/plain");

            // Parse name + address the same way the service does
            var cleaned  = address.Replace(", USA","").Replace(", United States","");
            var parts    = cleaned.Split(',');
            var street   = parts.Length > 0 ? parts[0].Trim() : cleaned;
            var city     = parts.Length > 1 ? parts[1].Trim() : "";
            var stateZip = parts.Length > 2 ? parts[2].Trim().Split(' ') : Array.Empty<string>();
            var state    = stateZip.Length > 0 ? stateZip[0] : "";

            var qs  = $"street={Uri.EscapeDataString(street)}&city={Uri.EscapeDataString(city)}&state_code={Uri.EscapeDataString(state)}";
            var url = $"https://api.whitepages.com/v2/property/?{qs}";

            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
            var resp = await http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();

            return Content($"Status: {(int)resp.StatusCode} {resp.StatusCode}\nURL: {url}\n\n{body}", "text/plain");
        }

        // ── DTOs ─────────────────────────────────────────────────────

        public class SaveRequest
        {
            [JsonPropertyName("sourceAddress")] public string?       SourceAddress { get; set; }
            [JsonPropertyName("properties")]    public PropertyDto[]? Properties   { get; set; }
        }

        public class PropertyDto
        {
            [JsonPropertyName("address")]         public string? Address         { get; set; }
            [JsonPropertyName("lat")]             public double  Lat             { get; set; }
            [JsonPropertyName("lng")]             public double  Lng             { get; set; }
            [JsonPropertyName("riskLevel")]       public string? RiskLevel       { get; set; }
            [JsonPropertyName("lastStormDate")]   public string? LastStormDate   { get; set; }
            [JsonPropertyName("hailSize")]        public string? HailSize        { get; set; }
            [JsonPropertyName("estimatedDamage")] public string? EstimatedDamage { get; set; }
            [JsonPropertyName("roofAge")]         public int     RoofAge         { get; set; }
            [JsonPropertyName("propertyType")]    public string? PropertyType    { get; set; }
        }

        public class OwnerDto
        {
            [JsonPropertyName("ownerName")]  public string? OwnerName  { get; set; }
            [JsonPropertyName("ownerPhone")] public string? OwnerPhone { get; set; }
            [JsonPropertyName("ownerEmail")] public string? OwnerEmail { get; set; }
        }

        public class BulkRequest
        {
            [JsonPropertyName("ids")] public long[]? Ids { get; set; }
        }

        public class PatchNotesRequest
        {
            [JsonPropertyName("notes")] public string? Notes { get; set; }
        }

        public class PatchStatusRequest
        {
            [JsonPropertyName("status")] public string Status { get; set; } = "new";
        }
    }
}
