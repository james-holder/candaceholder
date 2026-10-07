using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using System.Security.Claims;

namespace CandaceHolder.Controllers
{
    [Authorize]
    [Route("[controller]")]
    public class CompanyController : Controller
    {
        private readonly AppDbContext        _db;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<CompanyController> _logger;
        private readonly string              _adminEmail;

        private long? CurrentOrgId =>
            long.TryParse(User.FindFirst("user_org_id")?.Value, out var id) ? id : null;

        private bool IsAdmin() =>
            User.FindFirst("admin_role")?.Value is "admin" or "super_admin" ||
            string.Equals(User.FindFirst(ClaimTypes.Email)?.Value ?? "", _adminEmail, StringComparison.OrdinalIgnoreCase);

        public CompanyController(AppDbContext db, IWebHostEnvironment env,
                                 ILogger<CompanyController> logger, IConfiguration config)
        {
            _db         = db;
            _env        = env;
            _logger     = logger;
            _adminEmail = config["AdminEmail"] ?? "";
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /Company/Settings
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("Settings")]
        public async Task<IActionResult> Settings()
        {
            var orgId = CurrentOrgId;
            if (orgId == null) return RedirectToAction("Index", "Home");

            var org = await _db.Orgs.FirstOrDefaultAsync(o => o.Id == orgId);
            if (org == null) return RedirectToAction("Index", "Home");

            ViewBag.IsAdmin = IsAdmin();
            return View(org);
        }

        // ─────────────────────────────────────────────────────────────────
        // POST /Company/Settings
        // ─────────────────────────────────────────────────────────────────
        [HttpPost("Settings")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Settings(
            string? companyName, string? companyEmail, string? phone,
            string? website, string? accentColor, string? headerColor, string? tagline,
            string? licenseNumber, IFormFile? logoFile, string? removeLogo,
            string? address, string? facebookUrl, string? instagramUrl, string? googleBusinessUrl)
        {
            var orgId = CurrentOrgId;
            if (orgId == null) return RedirectToAction("Index", "Home");

            var org = await _db.Orgs.FirstOrDefaultAsync(o => o.Id == orgId);
            if (org == null) return RedirectToAction("Index", "Home");

            org.CompanyName   = companyName?.Trim();
            org.CompanyEmail  = companyEmail?.Trim();
            org.Phone         = phone?.Trim();
            org.Website       = website?.Trim();
            org.Tagline       = tagline?.Trim();
            org.LicenseNumber = licenseNumber?.Trim();

            // ── Additional company info ──────────────────────────────────
            org.Address           = string.IsNullOrWhiteSpace(address)           ? null : address.Trim();
            org.FacebookUrl       = string.IsNullOrWhiteSpace(facebookUrl)       ? null : facebookUrl.Trim();
            org.InstagramUrl      = string.IsNullOrWhiteSpace(instagramUrl)      ? null : instagramUrl.Trim();
            org.GoogleBusinessUrl = string.IsNullOrWhiteSpace(googleBusinessUrl) ? null : googleBusinessUrl.Trim();

            // Validate and set brand colors
            if (!string.IsNullOrWhiteSpace(accentColor) &&
                System.Text.RegularExpressions.Regex.IsMatch(accentColor.Trim(), @"^#[0-9a-fA-F]{6}$"))
                org.AccentColor = accentColor.Trim();
            if (!string.IsNullOrWhiteSpace(headerColor) &&
                System.Text.RegularExpressions.Regex.IsMatch(headerColor.Trim(), @"^#[0-9a-fA-F]{6}$"))
                org.HeaderColor = headerColor.Trim();

            // Logos are stored on the persistent data volume so they survive redeploys
            var logosDir = Path.Combine(_env.ContentRootPath, "App_Data", "logos");
            Directory.CreateDirectory(logosDir);

            // Handle logo removal
            if (removeLogo == "true" && !string.IsNullOrWhiteSpace(org.LogoPath))
            {
                var oldPath = Path.Combine(logosDir, Path.GetFileName(org.LogoPath));
                if (System.IO.File.Exists(oldPath))
                    System.IO.File.Delete(oldPath);
                org.LogoPath = null;
            }

            // Handle logo upload
            if (logoFile != null && logoFile.Length > 0)
            {
                var allowed = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg" };
                var ext     = Path.GetExtension(logoFile.FileName).ToLowerInvariant();
                if (!allowed.Contains(ext))
                {
                    TempData["Error"] = "Logo must be a PNG, JPG, GIF, WebP, or SVG file.";
                    return View(org);
                }
                if (logoFile.Length > 2 * 1024 * 1024)
                {
                    TempData["Error"] = "Logo must be under 2 MB.";
                    return View(org);
                }

                // Delete old logo if any
                if (!string.IsNullOrWhiteSpace(org.LogoPath))
                {
                    var oldPath = Path.Combine(logosDir, Path.GetFileName(org.LogoPath));
                    if (System.IO.File.Exists(oldPath))
                        System.IO.File.Delete(oldPath);
                }

                var fileName = $"{orgId}{ext}";
                var filePath = Path.Combine(logosDir, fileName);
                using var stream = new FileStream(filePath, FileMode.Create);
                await logoFile.CopyToAsync(stream);
                org.LogoPath = fileName; // just the filename; served via /Company/Logo/{orgId}
            }

            await _db.SaveChangesAsync();
            TempData["Success"] = "Company profile saved.";
            return RedirectToAction(nameof(Settings));
        }

        // GET /Company/Logo/{orgId} — serves logo from the persistent data volume
        // Public: email clients load it without signing in.
        [AllowAnonymous]
        [HttpGet("Logo/{id:long}")]
        public IActionResult Logo(long id)
        {
            var logosDir = Path.Combine(_env.ContentRootPath, "App_Data", "logos");
            if (!Directory.Exists(logosDir)) return NotFound();
            // Find any file whose name starts with the orgId (ext may vary)
            var file = Directory.EnumerateFiles(logosDir)
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == id.ToString());
            if (file == null) return NotFound();

            var ext  = Path.GetExtension(file).ToLowerInvariant();
            var mime = ext switch
            {
                ".png"  => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif"  => "image/gif",
                ".webp" => "image/webp",
                ".svg"  => "image/svg+xml",
                _       => "application/octet-stream"
            };
            return PhysicalFile(file, mime);
        }
    }
}
