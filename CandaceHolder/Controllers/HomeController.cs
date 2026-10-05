using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
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
        // Private site — no public landing page; send visitors straight to sign-in.
        if (!User.Identity?.IsAuthenticated ?? true)
            return Redirect("/Auth/Login");
        ViewBag.GoogleMapsApiKey       = _config["GoogleMaps:ApiKey"] ?? "";
        ViewBag.MapTilerApiKey         = _config["MapTiler:ApiKey"] ?? "";

        var orgId = CurrentOrgId;

        // ── "Continue" widget — most recently saved leads (server-rendered
        // so it can't silently show nothing if a client-side fetch fails).
        ViewBag.RecentLeads = await _db.Leads
            .Where(l => (l.OrgId == orgId || l.OrgId == null) && l.DeletedAt == null)
            .OrderByDescending(l => l.SavedAt)
            .Take(5)
            .Select(l => new RecentLeadVm
            {
                Id         = l.Id,
                Address    = l.Address,
                IsEnriched = l.IsEnriched,
                SavedAt    = l.SavedAt
            })
            .ToListAsync();

        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}

/// <summary>Lightweight projection for the Home "Continue" widget — deliberately not the full Lead entity.</summary>
public class RecentLeadVm
{
    public long      Id         { get; set; }
    public string    Address    { get; set; } = "";
    public bool      IsEnriched { get; set; }
    public DateTime  SavedAt    { get; set; }
}
