using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using CandaceHolder.Filters;
using CandaceHolder.Services;
using System.Security.Claims;

namespace CandaceHolder.Controllers
{
    [Route("[controller]")]
    [SkipTrialGate]
    public class AdminController : Controller
    {
        private readonly AppDbContext        _db;
        private readonly ReportCreditService _reportCredits;
        private readonly EmailService        _email;
        private readonly string              _adminEmail;
        private readonly bool                _enrichmentEnabled;
        private readonly bool                _reportCreditsEnabled;

        public AdminController(AppDbContext db, ReportCreditService reportCredits, EmailService email, IConfiguration config)
        {
            _db                   = db;
            _reportCredits        = reportCredits;
            _email                = email;
            _adminEmail           = config["AdminEmail"] ?? "";
            _enrichmentEnabled    = config.GetValue<bool>("FeatureFlags:EnrichmentEnabled");
            _reportCreditsEnabled = config.GetValue<bool>("FeatureFlags:ReportCreditsEnabled");
        }

        private string CurrentAdminRole =>
            User.FindFirst("admin_role")?.Value ?? "";

        // Admin panel access: role is "admin" or "super_admin", OR the legacy
        // single config email (appsettings "AdminEmail") — kept as a fallback
        // for sessions signed in before the admin_role claim existed.
        private bool IsAdmin() =>
            CurrentAdminRole is "admin" or "super_admin" ||
            string.Equals(User.FindFirst(ClaimTypes.Email)?.Value ?? "", _adminEmail, StringComparison.OrdinalIgnoreCase);

        // Only super admins can add/remove other admins (SetRole below).
        private bool IsSuperAdmin() =>
            CurrentAdminRole == "super_admin" ||
            string.Equals(User.FindFirst(ClaimTypes.Email)?.Value ?? "", _adminEmail, StringComparison.OrdinalIgnoreCase);

        private long? CurrentUserId =>
            long.TryParse(User.FindFirst("user_db_id")?.Value, out var id) ? id : null;

        // ── GET /Admin ───────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (!IsAdmin()) return Redirect("/");

            var now = DateTime.UtcNow;
            var som = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            ViewBag.EnrichmentEnabled    = _enrichmentEnabled;
            ViewBag.ReportCreditsEnabled = _reportCreditsEnabled;
            ViewBag.IsSuperAdmin         = IsSuperAdmin();
            ViewBag.TotalUsers  = await _db.Users.CountAsync();
            ViewBag.TotalLeads  = await _db.Leads.CountAsync();
            ViewBag.LeadsMonth  = await _db.Leads.CountAsync(l => l.SavedAt >= som);
            if (_enrichmentEnabled)
            {
                ViewBag.TotalEnrich = await _db.Enrichments.CountAsync();
                ViewBag.EnrichMonth = await _db.Enrichments.CountAsync(e => e.CreatedAt >= som);
            }

            // Platform-wide report-credit activity, from the ledger (not the
            // grant table) so it reflects actual usage, not just what's banked.
            ViewBag.ReportsGenerated = await _db.OrgCreditTransactions
                .CountAsync(t => t.CreditType == "report" && t.Amount < 0);
            ViewBag.ReportsMonth = await _db.OrgCreditTransactions
                .CountAsync(t => t.CreditType == "report" && t.Amount < 0 && t.CreatedAt >= som);

            var users = await _db.Users
                .OrderByDescending(u => u.CreatedAt)
                .Select(u => new UserRow
                {
                    Id          = u.Id,
                    Email       = u.Email ?? "",
                    DisplayName = u.DisplayName ?? "",
                    Provider    = u.Provider,
                    Role        = u.Role,
                    CreatedAt   = u.CreatedAt,
                    LeadCount   = u.Leads.Count(),
                    EnrichCount = u.Enrichments.Count(),
                    LastLeadAt  = u.Leads
                                   .OrderByDescending(l => l.SavedAt)
                                   .Select(l => (DateTime?)l.SavedAt)
                                   .FirstOrDefault(),
                    OrgId       = u.OrgId,
                    Plan        = u.Org != null ? u.Org.Plan : null,
                    TrialEndsAt = u.Org != null ? u.Org.TrialEndsAt : null
                })
                .ToListAsync();

            // Report-credit balances are per-batch (rollover cap/expiration —
            // see ReportCreditGrant) so they can't be projected in the query
            // above; sum non-expired grants per org separately and merge in.
            var nowUtc = DateTime.UtcNow;
            var balances = await _db.ReportCreditGrants
                .Where(g => g.Remaining > 0 && (g.ExpiresAt == null || g.ExpiresAt > nowUtc))
                .GroupBy(g => g.OrgId)
                .Select(g => new { OrgId = g.Key, Balance = g.Sum(x => x.Remaining) })
                .ToDictionaryAsync(x => x.OrgId, x => x.Balance);

            foreach (var u in users)
                u.ReportCredits = u.OrgId.HasValue && balances.TryGetValue(u.OrgId.Value, out var b) ? b : 0;

            // PDFs generated per login — same ledger as the platform-wide
            // ReportsGenerated stat above (OrgCreditTransactions debits),
            // grouped by the UserId who triggered each one instead of by org.
            // UserId is null for system/Stripe-originated transactions, so
            // those are naturally excluded here (a user can't "generate" one).
            var pdfCounts = await _db.OrgCreditTransactions
                .Where(t => t.CreditType == "report" && t.Amount < 0 && t.UserId != null)
                .GroupBy(t => t.UserId!.Value)
                .Select(g => new { UserId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.UserId, x => x.Count);

            foreach (var u in users)
                u.PdfCount = pdfCounts.TryGetValue(u.Id, out var c) ? c : 0;

            ViewBag.Users = users;

            return View();
        }

        // ── POST /Admin/Users/{id}/Plan ──────────────────────────────
        // Sets the plan tier on the user's org. Affects the whole org.
        [HttpPost("Users/{id:long}/Plan")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetPlan(long id, string plan)
        {
            if (!IsAdmin()) return Redirect("/");

            // trial/starter/pro are the current report-based tiers (see
            // PricingCatalog, docs/pricing-refresh-punchlist.md); free/agency
            // are kept for labeling continuity on orgs created under the old
            // enrichment-credit model. "pro" is reused — it now means the
            // $39.99/unlimited-reports tier (changed 2026-07-23 from 40
            // reports/mo, revised 2026-07-16 from $29.99/75-report; existing
            // subscribers on prior Stripe Prices are grandfathered at their
            // old price, but the unlimited/no-rollover behavior applies to
            // everyone on "pro" regardless of which Price they're billed on
            // — see the punchlist's rollout note),
            // not the legacy $99 enrichment tier (Plan is a display/
            // grouping label only; nothing gates functionality on its value
            // except the report-credit grant amounts in ReportCreditService).
            var valid = new[] { "trial", "starter", "pro", "free", "agency" };
            if (!valid.Contains(plan))
            {
                TempData["AdminError"] = "Invalid plan value.";
                return RedirectToAction(nameof(Index));
            }

            var org = await ResolveOrgForUserAsync(id);
            if (org == null)
            {
                TempData["AdminError"] = "That user has no organization.";
                return RedirectToAction(nameof(Index));
            }

            org.Plan = plan;
            await _db.SaveChangesAsync();
            TempData["AdminOk"] = $"Plan set to \"{plan}\".";
            return RedirectToAction(nameof(Index));
        }

        // ── POST /Admin/Users/{id}/Trial ─────────────────────────────
        // op = set | extend | unlimited | expire.  Affects the whole org.
        //   set       → TrialEndsAt = now + days
        //   extend    → add days to the current end (or from now if past/none)
        //   unlimited → TrialEndsAt = null (never gated)
        //   expire    → end the trial immediately
        [HttpPost("Users/{id:long}/Trial")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetTrial(long id, string op, int days = 0)
        {
            if (!IsAdmin()) return Redirect("/");

            var org = await ResolveOrgForUserAsync(id);
            if (org == null)
            {
                TempData["AdminError"] = "That user has no organization.";
                return RedirectToAction(nameof(Index));
            }

            var now = DateTime.UtcNow;
            days = Math.Max(0, days);

            switch (op)
            {
                case "unlimited":
                    org.TrialEndsAt = null;
                    TempData["AdminOk"] = "Trial set to unlimited (never gated).";
                    break;
                case "expire":
                    org.TrialEndsAt = now.AddMinutes(-1);
                    TempData["AdminOk"] = "Trial expired immediately.";
                    break;
                case "extend":
                    var basis = (org.TrialEndsAt.HasValue && org.TrialEndsAt.Value > now)
                        ? org.TrialEndsAt.Value : now;
                    org.TrialEndsAt = basis.AddDays(days);
                    TempData["AdminOk"] = $"Trial extended by {days} day(s).";
                    break;
                default: // "set"
                    org.TrialEndsAt = now.AddDays(days);
                    TempData["AdminOk"] = $"Trial set to {days} day(s) from now.";
                    break;
            }

            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        // ── POST /Admin/Users/{id}/Role ────────────────────────────────
        // role = user | admin. Promoting/demoting to/from "admin" only —
        // super_admin is never assignable here; it's reserved for whoever
        // authenticates via the Auth:AdminEmail/AdminPassword break-glass
        // credential (see AuthController.LoginPost).
        [HttpPost("Users/{id:long}/Role")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRole(long id, string role)
        {
            if (!IsAdmin()) return Redirect("/");
            if (!IsSuperAdmin())
            {
                TempData["AdminError"] = "Only super admins can add or remove admins.";
                return RedirectToAction(nameof(Index));
            }

            if (id == CurrentUserId)
            {
                TempData["AdminError"] = "You can't change your own role.";
                return RedirectToAction(nameof(Index));
            }

            if (role != "user" && role != "admin")
            {
                TempData["AdminError"] = "Invalid role.";
                return RedirectToAction(nameof(Index));
            }

            var user = await _db.Users.FindAsync(id);
            if (user == null)
            {
                TempData["AdminError"] = "User not found.";
                return RedirectToAction(nameof(Index));
            }

            if (user.Role == "super_admin")
            {
                TempData["AdminError"] = "Super admins can't be changed from here.";
                return RedirectToAction(nameof(Index));
            }

            user.Role = role;
            await _db.SaveChangesAsync();
            TempData["AdminOk"] = role == "admin"
                ? $"{(string.IsNullOrEmpty(user.Email) ? "User" : user.Email)} is now an admin."
                : $"{(string.IsNullOrEmpty(user.Email) ? "User" : user.Email)} is no longer an admin.";

            return RedirectToAction(nameof(Index));
        }

        // ── POST /Admin/Users/{id}/ReportCredits ──────────────────────
        // op = grant | forfeit.
        //   grant source=subscription      → monthly allotment for the org's current plan (starter/pro); no rollover —
        //                                    resets to 0 first, then grants fresh (no-op grant for "pro", which is unlimited)
        //   grant source=purchase_topup    → +3, never expires
        //   grant source=purchase_pack50   → +50, never expires
        //   grant source=admin_grant       → +amount (manual), never expires — e.g. seeding an existing org before enabling the gate
        //   forfeit                        → zeroes remaining subscription credits only (purchase_*/admin_grant credits are never touched)
        [HttpPost("Users/{id:long}/ReportCredits")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GrantReportCredits(long id, string op, string? source = null, int amount = 0)
        {
            if (!IsAdmin()) return Redirect("/");

            var org = await ResolveOrgForUserAsync(id);
            if (org == null)
            {
                TempData["AdminError"] = "That user has no organization.";
                return RedirectToAction(nameof(Index));
            }

            var adminUserId = CurrentUserId;

            switch (op)
            {
                case "forfeit":
                    var forfeited = await _reportCredits.ForfeitSubscriptionCreditsAsync(
                        org.Id, adminUserId, "Admin: forfeited subscription credits via /Admin");
                    TempData["AdminOk"] = forfeited > 0
                        ? $"Forfeited {forfeited} subscription report credit(s)."
                        : "No subscription credits to forfeit.";
                    break;

                case "grant":
                    switch (source)
                    {
                        case "subscription":
                            var planInfo = PricingCatalog.PlanByKey(org.Plan);
                            if (planInfo == null)
                            {
                                TempData["AdminError"] = $"\"{org.Plan}\" isn't a recurring report-credit plan (starter/pro only).";
                                break;
                            }
                            if (planInfo.IsUnlimited)
                            {
                                TempData["AdminOk"] = $"\"{org.Plan}\" is unlimited — no credit grant needed.";
                                break;
                            }
                            var granted = await _reportCredits.GrantSubscriptionAllotmentAsync(org.Id, org.Plan, adminUserId);
                            TempData["AdminOk"] = $"Granted {granted} report credit(s) — monthly allotment for \"{org.Plan}\" (no rollover; prior balance reset first).";
                            break;
                        case "purchase_topup":
                            await _reportCredits.GrantPurchaseAsync(org.Id, PricingCatalog.Topup.ReportAmount, "purchase_topup",
                                adminUserId, $"Admin: top-up ({PricingCatalog.Topup.ReportAmount} reports, {PricingCatalog.Topup.PriceLabel})");
                            TempData["AdminOk"] = $"Granted {PricingCatalog.Topup.ReportAmount} report credits (top-up).";
                            break;
                        case "purchase_pack50":
                            await _reportCredits.GrantPurchaseAsync(org.Id, PricingCatalog.Pack50.ReportAmount, "purchase_pack50",
                                adminUserId, $"Admin: 50-report pack ({PricingCatalog.Pack50.PriceLabel})");
                            TempData["AdminOk"] = $"Granted {PricingCatalog.Pack50.ReportAmount} report credits (pack).";
                            break;
                        case "admin_grant":
                            if (amount <= 0)
                            {
                                TempData["AdminError"] = "Enter a positive amount for a manual grant.";
                                break;
                            }
                            await _reportCredits.GrantAdminAsync(org.Id, amount, adminUserId, $"Admin: manual grant ({amount})");
                            TempData["AdminOk"] = $"Granted {amount} report credit(s).";
                            break;
                        default:
                            TempData["AdminError"] = "Unknown report-credit source.";
                            break;
                    }
                    break;

                default:
                    TempData["AdminError"] = "Unknown report-credit operation.";
                    break;
            }

            return RedirectToAction(nameof(Index));
        }

        // ── POST /Admin/Users/{id}/Email ───────────────────────────────
        // Ad-hoc outreach from the admin panel — subject/body come from the
        // modal on Index.cshtml (either a canned template or freehand text).
        // Sent via the same EmailService/SMTP setup as password resets and
        // storm alerts, so no separate mail configuration is needed.
        [HttpPost("Users/{id:long}/Email")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EmailUser(long id, string subject, string body)
        {
            if (!IsAdmin()) return Redirect("/");

            var user = await _db.Users.FindAsync(id);
            if (user == null || string.IsNullOrWhiteSpace(user.Email))
            {
                TempData["AdminError"] = "That user has no email on file.";
                return RedirectToAction(nameof(Index));
            }

            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
            {
                TempData["AdminError"] = "Subject and message are both required.";
                return RedirectToAction(nameof(Index));
            }

            // The composer is a plain textarea, not rich text — encode then
            // turn line breaks into <br> so paragraphs survive as HTML email.
            var htmlBody = "<p>" + System.Net.WebUtility.HtmlEncode(body).Replace("\n", "<br>") + "</p>";

            var sent = await _email.SendAsync(user.Email, subject, htmlBody);
            TempData[sent ? "AdminOk" : "AdminError"] = sent
                ? $"Email sent to {user.Email}."
                : $"Failed to send email to {user.Email} — check the app logs for the SMTP error.";

            return RedirectToAction(nameof(Index));
        }

        // ── POST /Admin/EmailAll ────────────────────────────────────────
        // Broadcast to every user with an email on file, via a single BCC
        // send (recipients never see each other's addresses). Same modal/
        // templates as the per-user Email action, just a different target.
        [HttpPost("EmailAll")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EmailAllUsers(string subject, string body)
        {
            if (!IsAdmin()) return Redirect("/");

            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
            {
                TempData["AdminError"] = "Subject and message are both required.";
                return RedirectToAction(nameof(Index));
            }

            var emails = await _db.Users
                .Where(u => u.Email != null && u.Email != "")
                .Select(u => u.Email!)
                .Distinct()
                .ToListAsync();

            if (emails.Count == 0)
            {
                TempData["AdminError"] = "No users with an email on file.";
                return RedirectToAction(nameof(Index));
            }

            var htmlBody = "<p>" + System.Net.WebUtility.HtmlEncode(body).Replace("\n", "<br>") + "</p>";

            var sent = await _email.SendBccBlastAsync(emails, subject, htmlBody);
            TempData[sent ? "AdminOk" : "AdminError"] = sent
                ? $"Email sent to {emails.Count} user(s)."
                : "Failed to send the broadcast — check the app logs for the SMTP error.";

            return RedirectToAction(nameof(Index));
        }

        // Resolve the org that owns a given user id (trial/plan live on the org).
        private async Task<Data.Models.Org?> ResolveOrgForUserAsync(long userId)
        {
            var user = await _db.Users.FindAsync(userId);
            if (user?.OrgId == null) return null;
            return await _db.Orgs.FindAsync(user.OrgId.Value);
        }

        public class UserRow
        {
            public long      Id            { get; set; }
            public string    Email         { get; set; } = "";
            public string    DisplayName   { get; set; } = "";
            public string    Provider      { get; set; } = "";
            public string    Role          { get; set; } = "user";
            public DateTime  CreatedAt     { get; set; }
            public int       LeadCount     { get; set; }
            public int       EnrichCount   { get; set; }
            public DateTime? LastLeadAt    { get; set; }
            public long?     OrgId         { get; set; }
            public string?   Plan          { get; set; }
            public DateTime? TrialEndsAt   { get; set; }
            public int       ReportCredits { get; set; }
            public int       PdfCount      { get; set; }
        }
    }
}
