using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using System.Security.Claims;

namespace CandaceHolder.Controllers
{
    [Authorize]
    [Route("[controller]")]
    public class DashboardController : Controller
    {
        private readonly AppDbContext _db;
        private readonly string       _adminEmail;

        public DashboardController(AppDbContext db, IConfiguration config)
        {
            _db         = db;
            _adminEmail = config["AdminEmail"] ?? "";
        }

        private long? CurrentUserId =>
            long.TryParse(User.FindFirst("user_db_id")?.Value, out var id) ? id : null;

        private long? CurrentOrgId =>
            long.TryParse(User.FindFirst("user_org_id")?.Value, out var id) ? id : null;

        private bool IsAdmin() =>
            User.FindFirst("admin_role")?.Value is "admin" or "super_admin" ||
            string.Equals(User.FindFirst(ClaimTypes.Email)?.Value ?? "", _adminEmail, StringComparison.OrdinalIgnoreCase);

        // ── GET /Dashboard ───────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var orgId  = CurrentOrgId;
            var userId = CurrentUserId;
            var now    = DateTime.UtcNow;
            var som    = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            var leadsQ           = _db.Leads.Where(l => (l.OrgId == orgId || l.OrgId == null) && l.DeletedAt == null);
            var pipelineStatuses = new[] { "new", "contacted", "appointment_set" };
            var closedWonCount   = await leadsQ.CountAsync(l => l.Status == "closed_won");
            var closedLostCount  = await leadsQ.CountAsync(l => l.Status == "closed_lost");
            var totalClosed      = closedWonCount + closedLostCount;

            ViewBag.UserName           = User.Identity?.Name ?? "Guest";
            ViewBag.IsAdmin            = IsAdmin();
            ViewBag.TotalLeads         = await leadsQ.CountAsync();
            ViewBag.LeadsThisMonth     = await leadsQ.CountAsync(l => l.SavedAt >= som);
            ViewBag.PipelineCount      = await leadsQ.CountAsync(l => pipelineStatuses.Contains(l.Status));
            ViewBag.ClosedWonCount     = closedWonCount;
            ViewBag.WinRate            = totalClosed > 0 ? (int)Math.Round(closedWonCount * 100.0 / totalClosed) : (int?)null;

            var rawLeads = await leadsQ
                .OrderByDescending(l => l.SavedAt)
                .Take(8)
                .Select(l => new { l.Id, l.Address, l.OwnerName, l.IsEnriched, l.SavedAt })
                .ToListAsync();

            ViewBag.RecentLeads = rawLeads
                .Select(l => (
                    Id:         l.Id,
                    Address:    l.Address ?? "",
                    Owner:      l.OwnerName ?? "",
                    IsEnriched: l.IsEnriched,
                    SavedAt:    l.SavedAt.ToString("o")
                ))
                .ToList();

            var enrichmentsQ = _db.Enrichments.Where(e => e.UserId == userId);
            ViewBag.TotalEnrich     = await enrichmentsQ.CountAsync();
            ViewBag.EnrichThisMonth = await enrichmentsQ.CountAsync(e => e.CreatedAt >= som);
            var rawEnrich = await enrichmentsQ
                .OrderByDescending(e => e.CreatedAt)
                .Take(5)
                .Select(e => new { e.Address, e.Status, e.CreatedAt })
                .ToListAsync();
            ViewBag.RecentEnrich = rawEnrich
                .Select(e => (
                    Address:   e.Address   ?? "",
                    Status:    e.Status    ?? "",
                    CreatedAt: e.CreatedAt.ToString("o")
                ))
                .ToList();

            return View();
        }
    }
}
