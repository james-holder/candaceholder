using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using CandaceHolder.Services;
using System.Security.Claims;

namespace CandaceHolder.Controllers
{
    [Route("[controller]")]
    public class AdminController : Controller
    {
        private readonly AppDbContext        _db;
        private readonly EmailService        _email;
        private readonly string              _adminEmail;

        public AdminController(AppDbContext db, EmailService email, IConfiguration config)
        {
            _db         = db;
            _email      = email;
            _adminEmail = config["AdminEmail"] ?? "";
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

            ViewBag.IsSuperAdmin = IsSuperAdmin();
            ViewBag.TotalUsers   = await _db.Users.CountAsync();
            ViewBag.TotalLeads   = await _db.Leads.CountAsync();
            ViewBag.LeadsMonth   = await _db.Leads.CountAsync(l => l.SavedAt >= som);
            ViewBag.TotalEnrich  = await _db.Enrichments.CountAsync();
            ViewBag.EnrichMonth  = await _db.Enrichments.CountAsync(e => e.CreatedAt >= som);

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
                    OrgId       = u.OrgId
                })
                .ToListAsync();

            ViewBag.Users = users;

            return View();
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

        // ── POST /Admin/Users/{id}/Email ───────────────────────────────
        // Ad-hoc outreach from the admin panel — subject/body come from the
        // modal on Index.cshtml (either a canned template or freehand text).
        // Sent via the same EmailService/SMTP setup as password resets,
        // so no separate mail configuration is needed.
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
        }
    }
}
