using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using CandaceHolder.Data.Models;
using CandaceHolder.Services;
using System.Security.Claims;
using System.Text.Json.Serialization;

namespace CandaceHolder.Controllers
{
    /// <summary>
    /// Email templates and sending them to traced leads. Every email gets a
    /// footer with the business mailing address and an unsubscribe link, and
    /// opted-out addresses are always skipped (CAN-SPAM).
    /// </summary>
    [Authorize]
    [Route("[controller]")]
    public class EmailController : Controller
    {
        // Keep batches small: Gmail allows ~500 recipients/day, and smaller
        // sends make a bad template easier to catch before it goes to everyone.
        public const int MaxPerSend = 50;

        // A skip trace often finds several emails for one owner, and none is
        // scored — so each lead is emailed at every found address, up to this many.
        public const int MaxEmailsPerLead = 3;

        /// <summary>
        /// The addresses a lead is emailed at: its main email first, then every
        /// email the skip trace found — de-duplicated, valid only, capped.
        /// Pass <paramref name="exclude"/> (opt-outs) so they don't use up a slot.
        /// </summary>
        public static List<string> RecipientEmails(Lead lead, ISet<string>? exclude = null) =>
            new[] { lead.OwnerEmail }
                .Concat(lead.Contacts.OrderByDescending(c => c.IsPrimary).Select(c => c.Email))
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e!.Trim())
                .Where(e => System.Net.Mail.MailAddress.TryCreate(e, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(e => exclude == null || !exclude.Contains(e))
                .Take(MaxEmailsPerLead)
                .ToList();

        private readonly AppDbContext          _db;
        private readonly EmailService          _email;
        private readonly IDataProtector        _unsubTokens;
        private readonly ILogger<EmailController> _logger;
        private readonly IWebHostEnvironment   _env;

        public EmailController(AppDbContext db, EmailService email, IDataProtectionProvider dp, ILogger<EmailController> logger,
                               IWebHostEnvironment env)
        {
            _env         = env;
            _db          = db;
            _email       = email;
            _unsubTokens = dp.CreateProtector("CandaceHolder.Unsubscribe");
            _logger      = logger;
        }

        private long? CurrentOrgId  => long.TryParse(User.FindFirst("user_org_id")?.Value, out var id) ? id : null;
        private long? CurrentUserId => long.TryParse(User.FindFirst("user_db_id")?.Value, out var id) ? id : null;
        // Same rule as skip tracing: owners and managers.
        private bool  CanSend       => User.FindFirst("user_org_role")?.Value is "owner" or "manager";

        // ── GET /Email/Templates — editor page ───────────────────────
        [HttpGet("Templates")]
        public IActionResult Templates()
        {
            ViewBag.CanEdit = CanSend;
            return View();
        }

        // ── GET /Email/Templates/List ────────────────────────────────
        [HttpGet("Templates/List")]
        public async Task<IActionResult> ListTemplates()
        {
            var orgId = CurrentOrgId;
            var list = await _db.EmailTemplates.AsNoTracking()
                .Where(t => t.OrgId == orgId)
                .OrderBy(t => t.Name)
                .Select(t => new { t.Id, t.Name, t.Subject, t.Body, t.Branded, t.UpdatedAt })
                .ToListAsync();
            return Json(list);
        }

        // ── POST /Email/Templates/Save ───────────────────────────────
        [HttpPost("Templates/Save")]
        public async Task<IActionResult> SaveTemplate([FromBody] TemplateDto dto)
        {
            if (!CanSend) return StatusCode(403, new { error = "Only owners and managers can edit templates." });
            var orgId = CurrentOrgId;
            if (orgId == null) return BadRequest(new { error = "No team found for your account." });

            var name    = (dto.Name ?? "").Trim();
            var subject = (dto.Subject ?? "").Trim();
            var body    = (dto.Body ?? "").Trim();
            if (name.Length == 0 || subject.Length == 0 || body.Length == 0)
                return BadRequest(new { error = "Name, subject and message are all required." });
            if (name.Length > 100 || subject.Length > 200 || body.Length > 20000)
                return BadRequest(new { error = "That template is too long." });

            EmailTemplate? t;
            if (dto.Id is long id)
            {
                t = await _db.EmailTemplates.FirstOrDefaultAsync(x => x.Id == id && x.OrgId == orgId);
                if (t == null) return NotFound(new { error = "Template not found." });
            }
            else
            {
                t = new EmailTemplate { OrgId = orgId.Value, CreatedAt = DateTime.UtcNow };
                _db.EmailTemplates.Add(t);
            }
            t.Name = name; t.Subject = subject; t.Body = body; t.Branded = dto.Branded ?? true; t.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Json(new { t.Id, t.Name, t.Subject, t.Body, t.Branded, t.UpdatedAt });
        }

        // ── DELETE /Email/Templates/{id} ─────────────────────────────
        [HttpDelete("Templates/{id:long}")]
        public async Task<IActionResult> DeleteTemplate(long id)
        {
            if (!CanSend) return StatusCode(403, new { error = "Only owners and managers can delete templates." });
            var orgId = CurrentOrgId;
            var t = await _db.EmailTemplates.FirstOrDefaultAsync(x => x.Id == id && x.OrgId == orgId);
            if (t == null) return NotFound(new { error = "Template not found." });
            _db.EmailTemplates.Remove(t);
            await _db.SaveChangesAsync();
            return Json(new { deleted = true });
        }

        // ── POST /Email/Preview ──────────────────────────────────────
        // Renders a subject/body for one lead (or sample data) exactly as it
        // would be sent, footer included.
        [HttpPost("Preview")]
        public async Task<IActionResult> Preview([FromBody] PreviewDto dto)
        {
            var orgId = CurrentOrgId;
            var org   = await _db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId);

            Lead? lead = null;
            if (dto.LeadId is long leadId)
                lead = await _db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leadId && l.OrgId == orgId);

            var ctx = lead != null
                ? ContextFor(lead, org)
                : new TemplateRenderer.Context("Jane Smith", "jane.smith@example.com",
                      "123 Main Street, Dallas, TX 75201", SenderName, CompanyName(org));

            var subject = TemplateRenderer.Render(dto.Subject ?? "", ctx);
            var body    = TemplateRenderer.Render(dto.Body ?? "", ctx);
            var logoUrl   = LogoUrl(org);
            var (html, _) = EmailLayout.Build(body, org, logoUrl, "#unsubscribe-link-preview", dto.Branded ?? true);

            return Json(new { subject, html, to = ctx.Email, missingAddress = string.IsNullOrWhiteSpace(org?.Address),
                              svgLogo = !string.IsNullOrWhiteSpace(org?.LogoPath) && !EmailLayout.HasEmailLogo(org),
                              // {{logo}} used but there's nothing to show
                              noLogo  = logoUrl == null && TemplateRenderer.LogoToken.IsMatch(body) });
        }

        // ── POST /Email/Send ─────────────────────────────────────────
        [HttpPost("Send")]
        public async Task<IActionResult> Send([FromBody] SendDto dto)
        {
            if (!CanSend) return StatusCode(403, new { error = "Only owners and managers can send email." });
            if (!_email.IsConfigured) return BadRequest(new { error = "Email isn't set up yet (Admin → Email settings)." });
            if (dto.LeadIds == null || dto.LeadIds.Length == 0) return BadRequest(new { error = "No leads selected." });
            if (dto.LeadIds.Length > MaxPerSend)
                return BadRequest(new { error = $"Send to at most {MaxPerSend} leads at a time." });

            var orgId = CurrentOrgId;
            var org   = await _db.Orgs.FirstOrDefaultAsync(o => o.Id == orgId);
            if (org == null) return BadRequest(new { error = "No team found for your account." });
            var logoUrl = LogoUrl(org);
            if (string.IsNullOrWhiteSpace(org.Address))
                return BadRequest(new { error = "Add your business mailing address in Company Profile first — the law (CAN-SPAM) requires it in every marketing email." });

            var template = await _db.EmailTemplates.FirstOrDefaultAsync(t => t.Id == dto.TemplateId && t.OrgId == orgId);
            if (template == null) return NotFound(new { error = "Template not found." });

            var leads = await _db.Leads
                .Include(l => l.Contacts)
                .Where(l => dto.LeadIds.Contains(l.Id) && l.OrgId == orgId && l.DeletedAt == null)
                .ToListAsync();

            var optedOut = (await _db.EmailOptOuts.Where(o => o.OrgId == orgId).Select(o => o.Email).ToListAsync())
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // "lead:address" pairs that already got this template.
            var alreadySent = (await _db.EmailSends
                    .Where(s => s.OrgId == orgId && s.TemplateId == template.Id && s.Status == "sent" && dto.LeadIds.Contains(s.LeadId))
                    .Select(s => new { s.LeadId, s.ToEmail }).ToListAsync())
                .Select(s => $"{s.LeadId}:{s.ToEmail.ToLowerInvariant()}")
                .ToHashSet();

            int noEmail = 0, skippedOptOut = 0, skippedDuplicate = 0;
            var batch = new List<(Lead Lead, EmailService.OutgoingEmail Email)>();
            foreach (var lead in leads)
            {
                var found     = RecipientEmails(lead);
                var addresses = RecipientEmails(lead, optedOut);
                if (found.Count == 0) { noEmail++; continue; }
                skippedOptOut += lead.Contacts.Select(c => c.Email).Append(lead.OwnerEmail)
                    .Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(optedOut.Contains);

                foreach (var to in addresses)
                {
                    if (!dto.AllowRepeat && alreadySent.Contains($"{lead.Id}:{to.ToLowerInvariant()}"))   { skippedDuplicate++; continue; }

                    // {{email}} is the address this copy is going to.
                    var ctx     = ContextFor(lead, org) with { Email = to };
                    var subject = TemplateRenderer.Render(template.Subject, ctx);
                    var body    = TemplateRenderer.Render(template.Body, ctx);
                    var unsub   = UnsubscribeUrl(org.Id, to);
                    var (html, text) = EmailLayout.Build(body, org, logoUrl, unsub, template.Branded);
                    batch.Add((lead, new EmailService.OutgoingEmail(to, subject, html, text, unsub)));
                }
            }

            var results = await _email.SendManyAsync(batch.Select(b => b.Email).ToList());

            var failures = new List<object>();
            for (int i = 0; i < batch.Count; i++)
            {
                var (lead, email) = batch[i];
                var error = results[i];
                _db.EmailSends.Add(new EmailSend
                {
                    OrgId = org.Id, LeadId = lead.Id, TemplateId = template.Id, UserId = CurrentUserId,
                    ToEmail = email.To, Subject = email.Subject,
                    Status = error == null ? "sent" : "failed", Error = error, SentAt = DateTime.UtcNow
                });
                if (error == null && (lead.Status == "new" || lead.Status == null))
                    lead.Status = "contacted";
                if (error != null) failures.Add(new { address = lead.Address, to = email.To, error });
            }
            await _db.SaveChangesAsync();

            var sent      = results.Count(r => r == null);
            var leadsSent = batch.Where((b, i) => results[i] == null).Select(b => b.Lead.Id).Distinct().Count();
            _logger.LogInformation("Template {TemplateId}: {Sent} email(s) to {Leads}/{Total} lead(s) by user {UserId}",
                template.Id, sent, leadsSent, leads.Count, CurrentUserId);

            return Json(new { sent, leads = leadsSent, noEmail, skippedOptOut, skippedDuplicate, failed = failures });
        }

        // ── GET /Email/Unsubscribe/{token} — public ──────────────────
        [HttpGet("~/u/{token}")]
        [AllowAnonymous]
        public IActionResult Unsubscribe(string token)
        {
            ViewBag.Valid = TryReadToken(token, out _, out var email);
            ViewBag.Email = email;
            ViewBag.Token = token;
            return View();
        }

        // ── POST /u/{token} — confirm (also mail apps' one-click unsubscribe) ─
        [HttpPost("~/u/{token}")]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> UnsubscribeConfirm(string token)
        {
            if (!TryReadToken(token, out var orgId, out var email))
            {
                ViewBag.Valid = false;
                return View("Unsubscribe");
            }

            var normalized = email.ToLowerInvariant();
            if (!await _db.EmailOptOuts.AnyAsync(o => o.OrgId == orgId && o.Email == normalized))
            {
                _db.EmailOptOuts.Add(new EmailOptOut { OrgId = orgId, Email = normalized, CreatedAt = DateTime.UtcNow });
                await _db.SaveChangesAsync();
                _logger.LogInformation("Email opt-out recorded for org {OrgId}", orgId);
            }

            ViewBag.Valid = true;
            ViewBag.Done  = true;
            ViewBag.Email = email;
            return View("Unsubscribe");
        }

        // ── Helpers ──────────────────────────────────────────────────
        private string? SenderName => User.FindFirst(ClaimTypes.Name)?.Value;

        private static string? CompanyName(Data.Models.Org? org) => EmailLayout.CompanyName(org);

        // Absolute link to the Company Profile logo; ?v= changes when the file
        // does, so email clients that cache images pick up a new logo.
        private string? LogoUrl(Data.Models.Org? org)
        {
            if (!EmailLayout.HasEmailLogo(org)) return null;
            var file = Path.Combine(_env.ContentRootPath, "App_Data", "logos", Path.GetFileName(org!.LogoPath!));
            if (!System.IO.File.Exists(file)) return null;
            return $"{Request.Scheme}://{Request.Host}/Company/Logo/{org.Id}?v={System.IO.File.GetLastWriteTimeUtc(file).Ticks}";
        }

        private TemplateRenderer.Context ContextFor(Lead lead, Data.Models.Org? org) =>
            new(lead.OwnerName, lead.OwnerEmail, lead.Address, SenderName, CompanyName(org));

        private string UnsubscribeUrl(long orgId, string email) =>
            $"{Request.Scheme}://{Request.Host}/u/{_unsubTokens.Protect($"{orgId}|{email.ToLowerInvariant()}")}";

        private bool TryReadToken(string token, out long orgId, out string email)
        {
            orgId = 0; email = "";
            try
            {
                var parts = _unsubTokens.Unprotect(token).Split('|', 2);
                if (parts.Length != 2 || !long.TryParse(parts[0], out orgId)) return false;
                email = parts[1];
                return true;
            }
            catch { return false; }
        }

        // ── DTOs ─────────────────────────────────────────────────────
        public class TemplateDto
        {
            [JsonPropertyName("id")]      public long?   Id      { get; set; }
            [JsonPropertyName("name")]    public string? Name    { get; set; }
            [JsonPropertyName("subject")] public string? Subject { get; set; }
            [JsonPropertyName("body")]    public string? Body    { get; set; }
            [JsonPropertyName("branded")] public bool?   Branded { get; set; }
        }

        public class PreviewDto
        {
            [JsonPropertyName("subject")] public string? Subject { get; set; }
            [JsonPropertyName("body")]    public string? Body    { get; set; }
            [JsonPropertyName("leadId")]  public long?   LeadId  { get; set; }
            [JsonPropertyName("branded")] public bool?   Branded { get; set; }
        }

        public class SendDto
        {
            [JsonPropertyName("templateId")]  public long    TemplateId  { get; set; }
            [JsonPropertyName("leadIds")]     public long[]? LeadIds     { get; set; }
            /// <summary>Send even to leads that already got this template.</summary>
            [JsonPropertyName("allowRepeat")] public bool    AllowRepeat { get; set; }
        }
    }
}
