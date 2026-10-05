using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using CandaceHolder.Data.Models;
using CandaceHolder.Filters;
using CandaceHolder.Services;
using System.Security.Claims;

namespace CandaceHolder.Controllers
{
    [Route("[controller]")]
    [SkipTrialGate]
    public class AuthController : Controller
    {
        private readonly AppDbContext        _db;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<AuthController> _logger;
        private readonly ReportCreditService _reportCredits;
        private readonly EmailService         _email;
        private readonly string               _adminEmail;

        public AuthController(AppDbContext db, IWebHostEnvironment env, ILogger<AuthController> logger,
            ReportCreditService reportCredits, EmailService email, IConfiguration config)
        {
            _db     = db;
            _env    = env;
            _logger = logger;
            _reportCredits = reportCredits;
            _email      = email;
            _adminEmail = config["AdminEmail"] ?? "";
        }

        // ── GET /Auth/Login ─────────────────────────────────────────
        [HttpGet("Login")]
        public IActionResult Login(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true) return Redirect(returnUrl ?? "/");

            var cfg = HttpContext.RequestServices.GetService<IConfiguration>();
            ViewData["ReturnUrl"]        = returnUrl ?? "/";
            ViewData["GoogleEnabled"]    = !string.IsNullOrWhiteSpace(cfg?["Auth:Google:ClientId"]);
            ViewData["MicrosoftEnabled"] = !string.IsNullOrWhiteSpace(cfg?["Auth:Microsoft:ClientId"]);
            ViewData["PasswordEnabled"]  = true;
            ViewData["LoginSuccess"]     = TempData["LoginSuccess"] as string;
            return View();
        }

        // ── POST /Auth/Login — password login ───────────────────────
        [HttpPost("Login")]
        public async Task<IActionResult> LoginPost(string email, string password, string? returnUrl = "/")
        {
            var cfg           = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var adminEmail    = cfg["Auth:AdminEmail"]    ?? "";
            var adminPassword = cfg["Auth:AdminPassword"] ?? "";

            // Check admin credentials (config-based superuser)
            if (!string.IsNullOrWhiteSpace(adminEmail) &&
                string.Equals(email, adminEmail, StringComparison.OrdinalIgnoreCase) &&
                password == adminPassword)
            {
                var userId = await FindOrCreateUserAsync("password", adminEmail, adminEmail, adminEmail.Split('@')[0]);
                await SignInUserAsync(userId, "password", adminEmail, adminEmail, adminEmail.Split('@')[0]);
                return LocalRedirect(returnUrl ?? "/");
            }

            // Check registered (signup) accounts with a stored password hash
            var normalizedEmail = email.Trim().ToLowerInvariant();
            var account = await _db.Users.FirstOrDefaultAsync(
                u => u.Provider == "password" && u.ProviderId == normalizedEmail);

            if (account != null && !string.IsNullOrEmpty(account.PasswordHash))
            {
                var verifyResult = new PasswordHasher<Data.Models.User>()
                    .VerifyHashedPassword(account, account.PasswordHash, password);

                if (verifyResult == PasswordVerificationResult.Success ||
                    verifyResult == PasswordVerificationResult.SuccessRehashNeeded)
                {
                    await SignInUserAsync(account.Id, "password", normalizedEmail,
                        account.Email ?? normalizedEmail, account.DisplayName ?? normalizedEmail);
                    return LocalRedirect(returnUrl ?? "/");
                }
            }

            ViewData["ReturnUrl"]        = returnUrl ?? "/";
            ViewData["GoogleEnabled"]    = !string.IsNullOrWhiteSpace(cfg["Auth:Google:ClientId"]);
            ViewData["MicrosoftEnabled"] = !string.IsNullOrWhiteSpace(cfg["Auth:Microsoft:ClientId"]);
            ViewData["PasswordEnabled"]  = true;
            ViewData["LoginError"]       = "Invalid email or password.";
            return View("Login");
        }

        // ── GET /Auth/ForgotPassword ──────────────────────────────────
        [HttpGet("ForgotPassword")]
        public IActionResult ForgotPassword()
        {
            if (User.Identity?.IsAuthenticated == true) return Redirect("/");
            return View();
        }

        // ── POST /Auth/ForgotPassword ─────────────────────────────────
        // Always shows the same "check your email" message whether or not
        // the account exists — otherwise this endpoint becomes a way to
        // check which emails have an account (user enumeration).
        [HttpPost("ForgotPassword")]
        public async Task<IActionResult> ForgotPasswordPost(string email)
        {
            const string genericMessage = "If an account exists for that email, we've sent a link to reset your password.";

            if (string.IsNullOrWhiteSpace(email))
            {
                ViewData["FormError"] = "Enter your email address.";
                return View("ForgotPassword");
            }

            var normalizedEmail = email.Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(
                u => u.Provider == "password" && u.ProviderId == normalizedEmail);

            if (user != null)
            {
                // 32 random bytes, URL-safe — long enough that guessing isn't
                // practical, short-lived (1 hour) so an intercepted-but-unused
                // link doesn't stay valid indefinitely.
                var tokenBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                var token = Convert.ToBase64String(tokenBytes)
                    .Replace('+', '-').Replace('/', '_').TrimEnd('=');

                user.PasswordResetToken     = token;
                user.PasswordResetExpiresAt = DateTime.UtcNow.AddHours(1);
                await _db.SaveChangesAsync();

                var resetUrl = $"{Request.Scheme}://{Request.Host}/Auth/ResetPassword?token={Uri.EscapeDataString(token)}";
                try
                {
                    await _email.SendAsync(user.Email ?? email, "Reset your StormLead Pro password",
                        $"<p>Someone (hopefully you) requested a password reset for your StormLead Pro account.</p>" +
                        $"<p><a href=\"{resetUrl}\">Click here to set a new password</a> — this link expires in 1 hour.</p>" +
                        $"<p>If you didn't request this, you can safely ignore this email.</p>");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send password-reset email for user={UserId}", user.Id);
                }
            }
            else
            {
                _logger.LogInformation("Password reset requested for unknown email={Email}", normalizedEmail);
            }

            ViewData["FormSuccess"] = genericMessage;
            return View("ForgotPassword");
        }

        // ── GET /Auth/ResetPassword?token=... ─────────────────────────
        [HttpGet("ResetPassword")]
        public async Task<IActionResult> ResetPassword(string? token)
        {
            if (string.IsNullOrWhiteSpace(token) || !await IsValidResetTokenAsync(token))
            {
                ViewData["TokenInvalid"] = true;
                return View();
            }

            ViewData["Token"] = token;
            return View();
        }

        // ── POST /Auth/ResetPassword ───────────────────────────────────
        [HttpPost("ResetPassword")]
        public async Task<IActionResult> ResetPasswordPost(string token, string password, string confirmPassword)
        {
            var user = string.IsNullOrWhiteSpace(token) ? null : await _db.Users.FirstOrDefaultAsync(
                u => u.PasswordResetToken == token &&
                     u.PasswordResetExpiresAt != null && u.PasswordResetExpiresAt > DateTime.UtcNow);

            if (user == null)
            {
                ViewData["TokenInvalid"] = true;
                return View("ResetPassword");
            }

            ViewData["Token"] = token;

            if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            {
                ViewData["FormError"] = "Password must be at least 8 characters.";
                return View("ResetPassword");
            }
            if (password != confirmPassword)
            {
                ViewData["FormError"] = "Passwords don't match.";
                return View("ResetPassword");
            }

            user.PasswordHash           = new PasswordHasher<Data.Models.User>().HashPassword(user, password);
            user.PasswordResetToken     = null;
            user.PasswordResetExpiresAt = null;
            await _db.SaveChangesAsync();

            _logger.LogInformation("Password reset completed for user={UserId}", user.Id);

            TempData["LoginSuccess"] = "Password updated — sign in with your new password.";
            return RedirectToAction(nameof(Login));
        }

        private async Task<bool> IsValidResetTokenAsync(string token) =>
            await _db.Users.AnyAsync(u =>
                u.PasswordResetToken == token &&
                u.PasswordResetExpiresAt != null && u.PasswordResetExpiresAt > DateTime.UtcNow);

        // ── GET /Auth/Register ───────────────────────────────────────
        [HttpGet("Register")]
        public IActionResult Register(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true) return Redirect(returnUrl ?? "/");

            ViewData["ReturnUrl"] = returnUrl ?? "/";
            return View();
        }

        // ── POST /Auth/Register — create org + owner account ─────────
        [HttpPost("Register")]
        public async Task<IActionResult> RegisterPost(
            string companyName, string name, string email, string password, string confirmPassword,
            string? plan = "trial", string? returnUrl = "/")
        {
            // Account type is a required choice on the signup form (trial /
            // starter / pro — see Views/Auth/Register.cshtml). Since Stripe
            // checkout doesn't exist yet, every choice still starts the same
            // 14-day/1-report trial below; starter/pro just records intent
            // and emails the admin to follow up on billing manually.
            var validPlans = new[] { "trial", "starter", "pro" };
            if (plan == null || !validPlans.Contains(plan)) plan = "trial";

            ViewData["ReturnUrl"]   = returnUrl ?? "/";
            ViewData["CompanyName"] = companyName;
            ViewData["Name"]        = name;
            ViewData["Email"]       = email;
            ViewData["Plan"]        = plan;

            if (string.IsNullOrWhiteSpace(companyName) || string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                ViewData["RegisterError"] = "Please fill in all fields.";
                return View("Register");
            }

            if (password.Length < 8)
            {
                ViewData["RegisterError"] = "Password must be at least 8 characters.";
                return View("Register");
            }

            if (password != confirmPassword)
            {
                ViewData["RegisterError"] = "Passwords don't match.";
                return View("Register");
            }

            var normalizedEmail = email.Trim().ToLowerInvariant();
            var existing = await _db.Users.FirstOrDefaultAsync(
                u => u.Provider == "password" && u.ProviderId == normalizedEmail);
            if (existing != null)
            {
                ViewData["RegisterError"] = "An account with that email already exists. Try signing in instead.";
                return View("Register");
            }

            // Create the org first — new signups get a 14-day free trial with
            // 1 PDF hail report regardless of which account type they picked
            // (see docs/pricing-refresh-punchlist.md — no Stripe checkout
            // exists yet, so starter/pro can't actually be charged at signup;
            // Plan just records their choice for a manual billing follow-up).
            // Enrichment is feature-flagged off platform-wide, so the capped
            // enrichment grant below is inert today but kept so the ledger
            // is seeded if enrichment is ever re-enabled.
            var trialEndsAt = DateTime.UtcNow.AddDays(14);
            var org = new Data.Models.Org
            {
                Name        = companyName.Trim(),
                CompanyName = companyName.Trim(),
                Plan        = plan,
                TrialEndsAt = trialEndsAt,
                CreatedAt   = DateTime.UtcNow
            };
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync(); // get org.Id

            _db.OrgCredits.Add(new Data.Models.OrgCredit
            {
                OrgId       = org.Id,
                CreditType  = "enrichment",
                Balance     = 25,
                PeriodStart = DateTime.UtcNow,
                PeriodEnd   = trialEndsAt,
                UpdatedAt   = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            // Create the owner account with a hashed password
            var newUser = new Data.Models.User
            {
                Provider    = "password",
                ProviderId  = normalizedEmail,
                Email       = email.Trim(),
                DisplayName = name.Trim(),
                OrgId       = org.Id,
                OrgRole     = "owner",
                CreatedAt   = DateTime.UtcNow
            };
            newUser.PasswordHash = new PasswordHasher<Data.Models.User>().HashPassword(newUser, password);
            _db.Users.Add(newUser);
            await _db.SaveChangesAsync();

            org.OwnerId = newUser.Id;
            await _db.SaveChangesAsync();

            await _reportCredits.GrantTrialAsync(org.Id, newUser.Id);

            if (plan is "starter" or "pro" && !string.IsNullOrWhiteSpace(_adminEmail))
            {
                // Best-effort — no Stripe checkout yet, so this is the only
                // signal that a new signup wants to pay. Never block signup
                // on it (EmailService already no-ops quietly if SMTP isn't
                // configured; the try/catch is just extra insurance).
                try
                {
                    await _email.SendAsync(_adminEmail,
                        $"New {plan} signup — {org.Name}",
                        $"<p><b>{System.Net.WebUtility.HtmlEncode(name.Trim())}</b> ({System.Net.WebUtility.HtmlEncode(email.Trim())}) " +
                        $"signed up for <b>{plan}</b> at <b>{System.Net.WebUtility.HtmlEncode(org.Name)}</b>.</p>" +
                        $"<p>They're on the standard 14-day trial for now — follow up to set up {plan} billing, " +
                        $"then grant credits and set the plan via /Admin.</p>");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send signup-intent email for org={OrgId} plan={Plan}", org.Id, plan);
                }
            }

            await SignInUserAsync(newUser.Id, "password", normalizedEmail, newUser.Email!, newUser.DisplayName!);
            _logger.LogInformation("New signup: org={OrgId} ({OrgName}) user={UserId} email={Email}",
                org.Id, org.Name, newUser.Id, normalizedEmail);

            return LocalRedirect(returnUrl ?? "/");
        }

        // ── GET /Auth/SignIn/{provider} ─────────────────────────────
        [HttpGet("SignIn/{provider}")]
        public IActionResult SignIn(string provider, string? returnUrl = "/")
        {
            var props = new AuthenticationProperties
            {
                RedirectUri = Url.Action("Callback", "Auth", new { returnUrl }),
                Items       = { ["provider"] = provider }
            };
            return Challenge(props, provider);
        }

        // ── GET /Auth/Callback ──────────────────────────────────────
        [HttpGet("Callback")]
        public async Task<IActionResult> Callback(string? returnUrl = "/")
        {
            var result = await HttpContext.AuthenticateAsync("External");
            if (!result.Succeeded)
            {
                _logger.LogWarning("External auth failed: {Error}", result.Failure?.Message);
                return Redirect("/Auth/Login");
            }

            await HttpContext.SignOutAsync("External");

            var provider   = result.Properties?.Items["provider"] ?? "unknown";
            var providerId = result.Principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
            var email      = result.Principal.FindFirst(ClaimTypes.Email)?.Value ?? "";
            var name       = result.Principal.FindFirst(ClaimTypes.Name)?.Value  ?? email;

            var userId = await FindOrCreateUserAsync(provider, providerId, email, name);
            await SignInUserAsync(userId, provider, providerId, email, name);

            return LocalRedirect(returnUrl ?? "/");
        }

        // ── GET /Auth/Logout ────────────────────────────────────────
        [HttpGet("Logout")]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Redirect("/Auth/Login");
        }

        // ── GET /Auth/DevLogin ──────────────────────────────────────
        // ⚠️  DEV BACKDOOR — returns 404 in Production
        [HttpGet("DevLogin")]
        public async Task<IActionResult> DevLogin(string? returnUrl = "/")
        {
            if (!_env.IsDevelopment()) return NotFound();

            var userId = await FindOrCreateUserAsync("dev", "dev-user-1", "dev@localhost", "Dev User");
            await SignInUserAsync(userId, "dev", "dev-user-1", "dev@localhost", "Dev User");
            _logger.LogWarning("DEV BACKDOOR used — signed in as Dev User (id={Id})", userId);
            return LocalRedirect(returnUrl ?? "/");
        }

        // ── Helpers ─────────────────────────────────────────────────

        private async Task<long> FindOrCreateUserAsync(
            string provider, string providerId, string email, string name)
        {
            var user = await _db.Users.FirstOrDefaultAsync(
                u => u.Provider == provider && u.ProviderId == providerId);

            if (user != null)
            {
                // Backfill org for any existing user who doesn't have one yet
                if (user.OrgId == null)
                {
                    var org = new Data.Models.Org
                    {
                        Name      = string.IsNullOrWhiteSpace(user.DisplayName) ? "My Company" : $"{user.DisplayName}'s Company",
                        OwnerId   = user.Id,
                        Plan      = "free",
                        CreatedAt = DateTime.UtcNow
                    };
                    _db.Orgs.Add(org);
                    await _db.SaveChangesAsync();

                    user.OrgId   = org.Id;
                    user.OrgRole = "owner";
                    await _db.SaveChangesAsync();

                    await _reportCredits.GrantTrialAsync(org.Id, user.Id);

                    // Migrate orphaned data to this org
                    await _db.Leads.Where(l => l.UserId == user.Id && l.OrgId == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(l => l.OrgId, org.Id));
                    await _db.WatchedAreas.Where(w => w.UserId == user.Id && w.OrgId == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(w => w.OrgId, org.Id));
                    await _db.SentAlerts.Where(a => a.UserId == user.Id && a.OrgId == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(a => a.OrgId, org.Id));
                }
                return user.Id;
            }

            // Brand-new user — create user + org in one shot
            var newOrg = new Data.Models.Org
            {
                Name      = string.IsNullOrWhiteSpace(name) ? "My Company" : $"{name}'s Company",
                Plan      = "free",
                CreatedAt = DateTime.UtcNow
            };
            _db.Orgs.Add(newOrg);
            await _db.SaveChangesAsync(); // get org.Id first

            var newUser = new Data.Models.User
            {
                Provider    = provider,
                ProviderId  = providerId,
                Email       = email,
                DisplayName = name,
                OrgId       = newOrg.Id,
                OrgRole     = "owner",
                CreatedAt   = DateTime.UtcNow
            };
            _db.Users.Add(newUser);
            await _db.SaveChangesAsync();

            // Set the owner back-reference
            newOrg.OwnerId = newUser.Id;
            await _db.SaveChangesAsync();

            await _reportCredits.GrantTrialAsync(newOrg.Id, newUser.Id);

            return newUser.Id;
        }

        private async Task SignInUserAsync(
            long userId, string provider, string providerId, string email, string name)
        {
            var user = await _db.Users.FindAsync(userId);

            // Whoever's email matches the configured platform admin (appsettings
            // "AdminEmail") is always treated as super_admin, no matter which
            // login path got them here — break-glass password, their own signup/
            // reset password, or OAuth. Without this, resetting your password via
            // /Auth/ResetPassword and logging in with it instead of the Fly-secret
            // break-glass credential would silently leave Role at "user".
            // Checked against Email, ProviderId, and the raw login email — trimmed
            // and case-insensitive on all three — since stored casing/whitespace
            // on the free-form Email column isn't guaranteed consistent across
            // every account-creation path in this codebase (signup, dev-seed,
            // admin-bypass, OAuth).
            var adminEmailNorm = (_adminEmail ?? "").Trim();
            var isConfiguredAdmin = user != null && !string.IsNullOrWhiteSpace(adminEmailNorm) && (
                string.Equals((user.Email ?? "").Trim(),      adminEmailNorm, StringComparison.OrdinalIgnoreCase) ||
                string.Equals((user.ProviderId ?? "").Trim(), adminEmailNorm, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(email.Trim(),                   adminEmailNorm, StringComparison.OrdinalIgnoreCase));

            if (isConfiguredAdmin && user!.Role != "super_admin")
            {
                user.Role = "super_admin";
                await _db.SaveChangesAsync();
            }

            var orgId      = user?.OrgId?.ToString()  ?? "";
            var orgRole    = user?.OrgRole             ?? "owner";
            var adminRole  = user?.Role                ?? "user";

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, providerId),
                new(ClaimTypes.Name,           name),
                new(ClaimTypes.Email,          email),
                new("provider",                provider),
                new("user_db_id",              userId.ToString()),
                new("user_org_id",             orgId),
                new("user_org_role",           orgRole),
                new("admin_role",              adminRole),
            };

            var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var props     = new AuthenticationProperties
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
