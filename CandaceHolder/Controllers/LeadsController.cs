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
            !string.IsNullOrWhiteSpace(_config["BatchData:ApiKey"]);

        private const string NoProviderError =
            "No skip-trace provider is set up. Add a BatchData (or Whitepages Pro / Regrid) key.";

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

            ViewBag.CanEnrich  = CanEnrich;
            ViewBag.EmailSends = await _db.EmailSends.AsNoTracking()
                .Where(s => s.LeadId == lead.Id && s.OrgId == orgId)
                .OrderByDescending(s => s.SentAt)
                .ToListAsync();
            return View(lead);
        }

        // ── GET /Leads?tab=untraced|traced|contacted|closed|archived ─
        // untraced / traced split the new (not yet worked) leads by whether
        // they've been skip traced; contacted = reached out / appointment set.
        [HttpGet]
        public async Task<IActionResult> Index(string tab = "untraced")
        {
            var orgId = CurrentOrgId;

            // Open = everything active and not yet closed, including brand-new leads.
            var contactedStatuses = new[] { "contacted", "appointment_set" };
            var closedStatuses    = new[] { "closed_won", "closed_lost" };

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
                    "closed"    => query.Where(l => closedStatuses.Contains(l.Status)),
                    "contacted" => query.Where(l => contactedStatuses.Contains(l.Status)),
                    _           => query.Where(l => l.Status == null || l.Status == "new")
                };
                if (tab == "untraced") query = query.Where(l => !l.IsEnriched);
                if (tab == "traced")   query = query.Where(l => l.IsEnriched);
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
                    Contacts = l.Contacts
                        .OrderByDescending(c => c.IsPrimary)
                        .ThenByDescending(c => c.PhoneScore)
                        .Select(c => new {
                        c.Id, c.Name, c.Phone, c.Email,
                        c.PhoneType, c.IsDnc, c.IsLitigator, c.PhoneScore, c.PhoneTested, c.PhoneReachable,
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

            var status = (await SkipTraceAsync(new List<Lead> { lead }))[lead.Id];
            if (status == "error")
                return StatusCode(502, new { error = ProviderFailedError });

            return Json(new { status, ownerName = lead.OwnerName, yearBuilt = lead.YearBuilt,
                              ownerPhone = lead.OwnerPhone, ownerEmail = lead.OwnerEmail });
        }

        // ── POST /Leads/BulkEnrich ───────────────────────────────────
        // Each match is billed by the skip-trace provider, so cap the
        // batch at one BatchData request's worth.
        private const int MaxBulkTrace = RealDataService.BatchDataMaxPerRequest;

        private const string ProviderFailedError =
            "The skip-trace provider request failed — check the API key and account balance, then try again.";

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

            var outcome = await SkipTraceAsync(leads);
            var errors  = outcome.Values.Count(v => v == "error");
            if (leads.Count > 0 && errors == leads.Count)
                return StatusCode(502, new { error = ProviderFailedError });

            return Json(new
            {
                processed = leads.Count,
                errors,
                results   = outcome.Select(o => new { id = o.Key, result = new { status = o.Value } })
            });
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
            var openLeadsQ   = activeLeadsQ.Where(l => l.Status == null || new[] { "new", "contacted", "appointment_set" }.Contains(l.Status));
            var enrichmentsQ = _db.Enrichments.Where(e => e.UserId == userId);

            return Json(new
            {
                totalLeads              = await activeLeadsQ.CountAsync(),
                leadsThisMonth          = await activeLeadsQ.CountAsync(l => l.SavedAt >= som),
                unenrichedCount         = await activeLeadsQ.CountAsync(l => l.Status == "new" || l.Status == null),
                pipelineCount           = await openLeadsQ.CountAsync(),
                untracedCount           = await activeLeadsQ.CountAsync(l => (l.Status == null || l.Status == "new") && !l.IsEnriched),
                tracedCount             = await activeLeadsQ.CountAsync(l => (l.Status == null || l.Status == "new") && l.IsEnriched),
                contactedCount          = await activeLeadsQ.CountAsync(l => l.Status == "contacted" || l.Status == "appointment_set"),
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
        // Shared skip-trace logic, used by single and bulk endpoints.
        //   1. Regrid (if configured)    — owner name + year built
        //   2. BatchData (if configured) — owner name, phones, emails;
        //      one request per 100 leads. Otherwise Whitepages Pro, one
        //      request per lead.
        // Returns each lead's outcome: "completed", "not_found", or
        // "error" (provider request failed — lead is left untraced so it
        // can be retried).
        // ─────────────────────────────────────────────────────────────
        private async Task<Dictionary<long, string>> SkipTraceAsync(List<Lead> leads)
        {
            var bdKey    = _config["BatchData:ApiKey"];
            var wpKey    = _config["WhitepagesPro:ApiKey"];
            var outcome  = leads.ToDictionary(l => l.Id, _ => "not_found");
            // Leads the paid provider (BatchData / Whitepages) actually matched —
            // the ones it bills for, and so the ones on the monthly usage invoice.
            var billable = new HashSet<long>();
            var provider = !string.IsNullOrWhiteSpace(bdKey) ? "batchdata"
                         : !string.IsNullOrWhiteSpace(wpKey) ? "whitepages"
                         : "regrid";

            // ── Step 1: Regrid — owner name + year built ─────────────
            if (!string.IsNullOrWhiteSpace(_config["Regrid:Token"]))
            {
                foreach (var lead in leads)
                {
                    var parcel = await _realData.GetRegridParcelDataAsync(lead.Lat ?? 0, lead.Lng ?? 0, lead.Address);
                    if (parcel == null) continue;
                    if (parcel.OwnerName != null && lead.OwnerName == null) lead.OwnerName = parcel.OwnerName;
                    if (parcel.YearBuilt != null) lead.YearBuilt = parcel.YearBuilt;
                    outcome[lead.Id] = "completed";
                }
            }

            // ── Step 2a: BatchData — batched ─────────────────────────
            if (!string.IsNullOrWhiteSpace(bdKey))
            {
                foreach (var chunk in leads.Chunk(RealDataService.BatchDataMaxPerRequest))
                {
                    var results = await _realData.BatchDataSkipTraceAsync(bdKey, chunk.Select(l => l.Address).ToList());
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        var lead = chunk[i];
                        if (results == null) { outcome[lead.Id] = "error"; continue; }
                        var r = results[i];
                        if (r == null) continue;

                        // Best numbers first: tested-dead lines last, then by confidence.
                        var phones = r.Phones
                            .OrderBy(ph => ph.Tested == true && ph.Reachable == false)
                            .ThenByDescending(ph => ph.Score ?? -1)
                            .ToList();

                        var contacts = new List<LeadContact>();
                        for (int p = 0; p < phones.Count; p++)
                        {
                            contacts.Add(new LeadContact
                            {
                                Name           = r.OwnerName,
                                Phone          = phones[p].Number,
                                PhoneType      = phones[p].Type,
                                IsDnc          = phones[p].IsDnc,
                                PhoneScore     = phones[p].Score,
                                PhoneTested    = phones[p].Tested,
                                PhoneReachable = phones[p].Reachable,
                                IsLitigator    = r.IsLitigator,
                                Email          = p < r.Emails.Count ? r.Emails[p] : null
                            });
                        }
                        // Any emails beyond the number of phones get their own rows.
                        foreach (var email in r.Emails.Skip(phones.Count))
                            contacts.Add(new LeadContact { Name = r.OwnerName, Email = email, IsLitigator = r.IsLitigator });

                        ReplaceContacts(lead, r.OwnerName, contacts, "batchdata");
                        outcome[lead.Id] = "completed";
                        billable.Add(lead.Id);
                    }
                }
            }
            // ── Step 2b: Whitepages Pro — one lead at a time ─────────
            else if (!string.IsNullOrWhiteSpace(wpKey))
            {
                foreach (var lead in leads)
                {
                    var found = await _realData.GetWhitepagesContactAsync(wpKey, lead.OwnerName, lead.Address);
                    if (found.Count == 0) continue;
                    var contacts = found.Select(c => new LeadContact
                    {
                        Name = c.OwnerName, Phone = c.Phone, Email = c.Email, ContactType = c.ContactType
                    }).ToList();
                    ReplaceContacts(lead, found[0].OwnerName, contacts, "whitepages");
                    outcome[lead.Id] = "completed";
                    billable.Add(lead.Id);
                }
            }

            // Record every attempt. Leads whose lookup actually ran are marked
            // traced (even with no match) so they aren't re-billed by accident;
            // provider errors leave the lead untraced so it can be retried.
            foreach (var lead in leads)
            {
                if (outcome[lead.Id] != "error") lead.IsEnriched = true;
                _db.Enrichments.Add(new Enrichment
                {
                    UserId      = CurrentUserId,
                    LeadId      = lead.Id,
                    Address     = lead.Address,
                    Status      = outcome[lead.Id],
                    Provider    = provider,
                    CreditsUsed = billable.Contains(lead.Id) ? 1 : 0,   // 1 = billed match (see Admin → Usage invoice)
                    CreatedAt   = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync();
            return outcome;
        }

        // Swaps a lead's contacts for a fresh skip-trace result and fills the
        // lead's owner fields (without overwriting anything typed in by hand).
        private void ReplaceContacts(Lead lead, string? ownerName, List<LeadContact> contacts, string source)
        {
            _db.LeadContacts.RemoveRange(_db.LeadContacts.Where(c => c.LeadId == lead.Id));

            for (int i = 0; i < contacts.Count; i++)
            {
                var c = contacts[i];
                c.LeadId    = lead.Id;
                c.IsPrimary = i == 0;
                c.Source    = source;
                c.CreatedAt = DateTime.UtcNow;
                _db.LeadContacts.Add(c);
            }

            if (ownerName != null && lead.OwnerName == null)
                lead.OwnerName = ownerName;
            // Prefer a callable number: first phone that isn't on a Do Not Call list.
            // Contacts arrive best-first, so this is the highest-confidence callable line.
            var phone = contacts.FirstOrDefault(c => c.Phone != null && !c.IsDnc && !c.IsLitigator && c.PhoneReachable != false)?.Phone
                     ?? contacts.FirstOrDefault(c => c.Phone != null && !c.IsDnc && !c.IsLitigator)?.Phone
                     ?? contacts.FirstOrDefault(c => c.Phone != null)?.Phone;
            if (phone != null && lead.OwnerPhone == null)
                lead.OwnerPhone = phone;
            var email = contacts.FirstOrDefault(c => c.Email != null)?.Email;
            if (email != null && lead.OwnerEmail == null)
                lead.OwnerEmail = email;
        }

        // ── GET /Leads/BatchDataDebug — parse BatchData's documented sample ──
        // Dev-only — checks the response parser without spending credits.
        [HttpGet("BatchDataDebug")]
        public IActionResult BatchDataDebug()
        {
            if (!_env.IsDevelopment())
                return NotFound();

            const string sampleJson = """
            {
              "status": { "code": 200, "text": "OK" },
              "results": {
                "persons": [
                  {
                    "dnc": { "tcpa": false },
                    "emails": [ { "email": "johndoe@gmail.net" } ],
                    "name": { "first": "john", "last": "doe" },
                    "phoneNumbers": [
                      { "number": "1111111111", "type": "Mobile", "tested": true, "reachable": true, "score": 100 },
                      { "number": "2222222222", "type": "Land Line", "dnc": true, "tested": false, "reachable": false, "score": 95 }
                    ],
                    "litigator": false,
                    "meta": { "matched": true, "error": false }
                  },
                  { "meta": { "matched": false, "error": false } }
                ],
                "meta": { "results": { "requestCount": 2, "matchCount": 1, "noMatchCount": 1, "errorCount": 0 } }
              }
            }
            """;
            return Json(RealDataService.ParseBatchDataResponse(sampleJson, 2));
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
