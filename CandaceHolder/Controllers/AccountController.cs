using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using CandaceHolder.Data.Models;
using System.Security.Claims;

namespace CandaceHolder.Controllers
{
    [Authorize]
    [Route("Account")]
    public class AccountController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ILogger<AccountController> _logger;
        private readonly string       _adminEmail;

        public AccountController(AppDbContext db, ILogger<AccountController> logger, IConfiguration config)
        {
            _db         = db;
            _logger     = logger;
            _adminEmail = config["AdminEmail"] ?? "";
        }

        private long? CurrentUserId =>
            long.TryParse(User.FindFirst("user_db_id")?.Value, out var id) ? id : null;

        private bool IsAdmin() =>
            User.FindFirst("admin_role")?.Value is "admin" or "super_admin" ||
            string.Equals(User.FindFirst(ClaimTypes.Email)?.Value ?? "", _adminEmail, StringComparison.OrdinalIgnoreCase);

        // ── GET /Account/Profile ────────────────────────────────────────
        [HttpGet("Profile")]
        public async Task<IActionResult> Profile()
        {
            var userId = CurrentUserId;
            if (userId == null) return RedirectToAction("Index", "Home");

            var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return RedirectToAction("Index", "Home");

            ViewBag.IsAdmin = IsAdmin();
            return View(user);
        }

        // ── POST /Account/Profile ───────────────────────────────────────
        [HttpPost("Profile")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Profile(string? displayName, string? phone, string? notificationEmail)
        {
            var userId = CurrentUserId;
            if (userId == null) return RedirectToAction("Index", "Home");

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return RedirectToAction("Index", "Home");

            if (string.IsNullOrWhiteSpace(displayName))
            {
                TempData["Error"] = "Name cannot be empty.";
                return RedirectToAction(nameof(Profile));
            }

            user.DisplayName      = displayName.Trim();
            user.Phone            = string.IsNullOrWhiteSpace(phone)             ? null : phone.Trim();
            user.NotificationEmail = string.IsNullOrWhiteSpace(notificationEmail) ? null : notificationEmail.Trim();

            await _db.SaveChangesAsync();

            // Re-issue the auth cookie so the nav (and "Welcome back, X") reflects
            // the new display name immediately, without requiring re-login.
            await RefreshAuthCookieAsync(user);

            _logger.LogInformation("User {UserId} updated their profile", user.Id);
            TempData["Success"] = "Profile saved.";
            return RedirectToAction(nameof(Profile));
        }

        // ── Helper: re-issue auth cookie with updated claims ────────────
        private async Task RefreshAuthCookieAsync(User user)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.ProviderId),
                new(ClaimTypes.Name,           user.DisplayName ?? user.Email ?? ""),
                new(ClaimTypes.Email,          user.Email ?? ""),
                new("provider",                user.Provider),
                new("user_db_id",              user.Id.ToString()),
                new("user_org_id",             user.OrgId?.ToString() ?? ""),
                new("user_org_role",           user.OrgRole ?? "rep")
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var props    = new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc   = DateTimeOffset.UtcNow.AddDays(30)
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity), props);
        }
    }
}
