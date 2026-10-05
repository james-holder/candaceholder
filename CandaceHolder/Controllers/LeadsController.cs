using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using CandaceHolder.Data.Models;
using CandaceHolder.Services;
using System.Security.Claims;
using System.Text.Json.Serialization;

namespace CandaceHolder.Controllers
{
    [Authorize]
    [Route("[controller]")]
    public class LeadsController : Controller 
    {
        private readonly AppDbContext        _db;
        private readonly IWebHostEnvironment _env;
        private readonly RealDataService     _realData;
        private readonly IConfiguration      _config;

        public LeadsController(AppDbContext db, IWebHostEnvironment env, RealDataService realData, IConfiguration config)
        {
            _db       = db;
            _env      = env;
            _realData = realData;
            _config   = config;
        }

        private long? CurrentUserId =>
            long.TryParse(User.FindFirst("user_db_id")?.Value, out var id) ? id : null;

        private long? CurrentOrgId =>
            long.TryParse(User.FindFirst("user_org_id")?.Value, out var id) ? id : null;

        private string CurrentOrgRole =>
            User.FindFirst("user_org_role")?.Value ?? "rep";

        // Skip tracing costs money per lookup, so only owners/managers can run it.
        private bool CanEnrich => CurrentOrgRole is "owner" or "manager";

        // True when at least one skip-trace provider has a key. Without one, a
        // "trace" would just mark leads traced with nothing found.
        private bool HasSkipTraceProvider =>
            !string.IsNullOrWhiteSpace(_config["Regrid:Token"]) ||
            !string.IsNullOrWhiteSpace(_config["WhitepagesPro:ApiKey"]) ||
            !string.IsNullOrWhiteSpace(_config["BatchSkipTracing:ApiKey"]);

        private const string NoProviderError =
            "No skip-trace provider is set up. Add a Regrid, Whitepages Pro, or BatchSkipTracing key.";

        // Loads a lead only if it belongs to the caller's org.
        private Task<Lead?> FindOwnLeadAsync(long id)
        {
            var orgId = CurrentOrgId;
            return _db.Leads.FirstOrDefaultAsync(l => l.Id == id && (l.OrgId == orgId || l.OrgId == null));
        }

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
                .OrderByDescending(l => l.SavedAt)
                .Select(l => new
                {
                    l.Id, l.Address, l.Lat, l.Lng,
                    l.PropertyType,
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

                var existing = await _db.Leads.FirstOrDefaultAsync(l => l.Address == p.Address && l.OrgId == orgId);
                if (existing != null)
                {
                    // Restore if previously archived
                    existing.DeletedAt       = null;
                    existing.Lat             = p.Lat;
                    existing.Lng             = p.Lng;
                    existing.PropertyType    = p.PropertyType;
                    existing.SourceAddress   = req.SourceAddress;
                    existing.SavedAt         = DateTime.UtcNow;
                    existing.UserId          = userId;
                    updated++;
                }
                else
                {
                    _db.Leads.Add(new Lead
                    {
                        Address         = p.Address,
                        Lat             = p.Lat,
                        Lng             = p.Lng,
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
            var lead = await FindOwnLeadAsync(id);
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
            if (!CanEnrich)
                return StatusCode(403, new { error = "Reps cannot run skip tracing. Ask an owner or manager." });

            if (!HasSkipTraceProvider)
                return StatusCode(503, new { error = NoProviderError });

            var lead = await FindOwnLeadAsync(id);
            if (lead == null) return NotFound(new { error = "Lead not found." });

            return Json(await EnrichLeadAsync(lead));
        }

        // ── POST /Leads/BulkEnrich ───────────────────────────────────
        // Each lookup is billed by the skip-trace provider and runs
        // sequentially in this request, so cap the batch size.
        private const int MaxBulkTrace = 100;

        [HttpPost("BulkEnrich")]
        public async Task<IActionResult> BulkEnrich([FromBody] BulkRequest req)
        {
            if (!CanEnrich)
                return StatusCode(403, new { error = "Reps cannot run skip tracing. Ask an owner or manager." });

            if (req?.Ids == null || req.Ids.Length == 0)
                return BadRequest(new { error = "No lead IDs provided." });
            if (req.Ids.Length > MaxBulkTrace)
                return BadRequest(new { error = $"Skip trace up to {MaxBulkTrace} leads at a time." });
            if (!HasSkipTraceProvider)
                return StatusCode(503, new { error = NoProviderError });

            var orgId  = CurrentOrgId;
            var leads  = await _db.Leads
                .Where(l => req.Ids.Contains(l.Id) &&
                            (l.OrgId == orgId || l.OrgId == null) &&
                            !l.IsEnriched && l.DeletedAt == null)
                .ToListAsync();

            var results = new List<object>();
            foreach (var lead in leads)
            {
                var r = await EnrichLeadAsync(lead);
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

        // ── POST /Leads/BulkDelete ───────────────────────────
        // Soft-deletes (archives) all matching leads owned by the current
        // org. Used by the bulk-actions toolbar; restorable via Restore.
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
                canEnrich               = CanEnrich
            });
        }

        // ── DELETE /Leads/{id} — soft delete ─────────────────────────
        [HttpDelete("{id:long}")]
        public async Task<IActionResult> Delete(long id)
        {
            var lead = await FindOwnLeadAsync(id);
            if (lead == null) return NotFound();

            lead.DeletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return NoContent();
        }

        // ─────────────────────────────────────────────────────────────
        // Shared enrichment logic
        // ─────────────────────────────────────────────────────────────
        private async Task<object> EnrichLeadAsync(Lead lead)
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
