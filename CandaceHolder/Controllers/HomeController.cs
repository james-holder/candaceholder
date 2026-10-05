using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using CandaceHolder.Filters;
using CandaceHolder.Models;

namespace CandaceHolder.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly IConfiguration          _config;
    private readonly AppDbContext            _db;

    public HomeController(ILogger<HomeController> logger, IConfiguration config, AppDbContext db)
    {
        _logger = logger;
        _config = config;
        _db     = db;
    }

    private long? CurrentOrgId =>
        long.TryParse(User.FindFirst("user_org_id")?.Value, out var id) ? id : null;

    public async Task<IActionResult> Index()
    {
        if (!User.Identity?.IsAuthenticated ?? true)
            return RedirectToAction("Landing");
        ViewBag.GoogleMapsApiKey       = _config["GoogleMaps:ApiKey"] ?? "";
        ViewBag.MapTilerApiKey         = _config["MapTiler:ApiKey"] ?? "";
        ViewBag.HailSwathPolygons      = _config.GetValue<bool>("FeatureFlags:HailSwathPolygons");
        // Note: MESH visibility is no longer gated by a ViewBag/button — Storm
        // Explorer always tries to render it per selected date; the server-side
        // FeatureFlags:MeshSwaths check in RoofHealthController controls whether
        // that returns real data. See docs/mesh-phase2-handoff.md.

        var orgId = CurrentOrgId;

        // ── "Continue" widget — most recently saved leads (server-rendered
        // rather than a client-side fetch; the previous storm-activity widget
        // used a fetch call and silently showed nothing when it failed, which
        // is exactly the failure mode this avoids). Added 2026-07-23.
        ViewBag.RecentLeads = await _db.Leads
            .Where(l => (l.OrgId == orgId || l.OrgId == null) && l.DeletedAt == null)
            .OrderByDescending(l => l.SavedAt)
            .Take(5)
            .Select(l => new RecentLeadVm
            {
                Id       = l.Id,
                Address  = l.Address,
                RiskLevel = l.RiskLevel,
                SavedAt  = l.SavedAt
            })
            .ToListAsync();

        // ── Trial/Starter upgrade nudge — added 2026-07-23. Deliberately
        // plan-based only (not tied to the top _TrialBanner's countdown),
        // since Starter subscribers aren't on a trial at all but are still a
        // relevant upgrade audience for Pro's unlimited reports.
        if (orgId.HasValue)
        {
            var plan = await _db.Orgs.Where(o => o.Id == orgId).Select(o => o.Plan).FirstOrDefaultAsync();
            ViewBag.ShowUpgradeNudge = plan is "trial" or "starter";
        }

        return View();
    }

    [SkipTrialGate]
    public IActionResult Landing()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index");
        return View();
    }

    [SkipTrialGate]
    public IActionResult Privacy()
    {
        return View();
    }

    [SkipTrialGate]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}

/// <summary>Lightweight projection for the Home "Continue" widget — deliberately not the full Lead entity.</summary>
public class RecentLeadVm
{
    public long      Id        { get; set; }
    public string    Address   { get; set; } = "";
    public string?   RiskLevel { get; set; }
    public DateTime  SavedAt   { get; set; }
}
