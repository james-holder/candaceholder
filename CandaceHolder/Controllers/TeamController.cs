using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
    public class TeamController : Controller
    {
        private readonly AppDbContext              _db;
        private readonly EmailService              _email;
        private readonly ILogger<TeamController>   _logger;

        public TeamController(AppDbContext db, EmailService email, ILogger<TeamController> logger)
        {
            _db     = db;
            _email  = email;
            _logger = logger;
        }

        private long? CurrentUserId =>
            long.TryParse(User.FindFirst("user_db_id")?.Value, out var id) ? id : null;

        private long? CurrentOrgId =>
            long.TryParse(User.FindFirst("user_org_id")?.Value, out var id) ? id : null;

        private string CurrentOrgRole =>
            User.FindFirst("user_org_role")?.Value ?? "rep";

        private bool IsOwnerOrManager => CurrentOrgRole is "owner" or "manager";

        // Falls back to a DB lookup when the auth cookie predates the org system
        private async Task<long?> ResolveOrgIdAsync()
        {
            var orgId = CurrentOrgId;
            if (orgId != null) return orgId;

            var userId = CurrentUserId;
            if (userId == null) return null;

            var user = await _db.Users.FindAsync(userId);
            return user?.OrgId;
        }

        // ── GET /Team ─────────────────────────────────────────────────
        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            // Resolve org — fall back to DB if cookie is stale (signed in before org system)
            var orgId = await ResolveOrgIdAsync();
            if (orgId == null) return RedirectToAction("Index", "Dashboard");

            var org = await _db.Orgs.FindAsync(orgId);
            if (org == null) return RedirectToAction("Index", "Dashboard");

            ViewBag.OrgName    = org.Name;
            ViewBag.OrgRole    = CurrentOrgRole;
            ViewBag.IsOwner    = CurrentOrgRole == "owner";
            ViewBag.CanManage  = IsOwnerOrManager;
            return View();
        }

        // ── GET /Team/Members — JSON list of current org members ──────
        [HttpGet("Members")]
        public async Task<IActionResult> Members()
        {
            var orgId = CurrentOrgId;
            var members = await _db.Users
                .Where(u => u.OrgId == orgId)
                .OrderBy(u => u.OrgRole == "owner" ? 0 : u.OrgRole == "manager" ? 1 : 2)
                .ThenBy(u => u.DisplayName)
                .Select(u => new {
                    u.Id,
                    name    = u.DisplayName ?? u.Email ?? "(unknown)",
                    email   = u.Email ?? "",
                    role    = u.OrgRole ?? "rep",
                    isMe    = u.Id == CurrentUserId,
                    joinedAt = u.CreatedAt.ToString("yyyy-MM-dd")
                })
                .ToListAsync();

            return Json(members);
        }

        // ── GET /Team/Invites — JSON list of pending invites ──────────
        [HttpGet("Invites")]
        public async Task<IActionResult> Invites()
        {
            if (!IsOwnerOrManager)
                return Forbid();

            var orgId = CurrentOrgId;
            var invites = await _db.OrgInvites
                .Where(i => i.OrgId == orgId && i.AcceptedAt == null && i.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(i => i.CreatedAt)
                .Select(i => new {
                    i.Id, i.Email, i.Role,
                    expiresAt = i.ExpiresAt.ToString("yyyy-MM-dd")
                })
                .ToListAsync();

            return Json(invites);
        }

        // ── POST /Team/Invite ─────────────────────────────────────────
        [HttpPost("Invite")]
        public async Task<IActionResult> Invite([FromBody] InviteRequest req)
        {
            if (!IsOwnerOrManager)
                return StatusCode(403, new { error = "Only owners and managers can invite members." });

            if (string.IsNullOrWhiteSpace(req.Email))
                return BadRequest(new { error = "Email is required." });

            var role = req.Role is "owner" or "manager" or "rep" ? req.Role : "rep";
            // Only owners can invite managers or owners
            if (role != "rep" && CurrentOrgRole != "owner")
                return StatusCode(403, new { error = "Only owners can invite managers." });

            var orgId = CurrentOrgId;
            if (orgId == null)
                return BadRequest(new { error = "No org found." });

            var org = await _db.Orgs.FindAsync(orgId);
            if (org == null) return BadRequest(new { error = "Org not found." });

            // Check if they're already a member
            var alreadyMember = await _db.Users.AnyAsync(
                u => u.OrgId == orgId &&
                     u.Email != null &&
                     u.Email.ToLower() == req.Email.ToLower());
            if (alreadyMember)
                return BadRequest(new { error = "That person is already a member of your team." });

            // Cancel any existing pending invite for this email + org
            var existingInvite = await _db.OrgInvites.FirstOrDefaultAsync(
                i => i.OrgId == orgId &&
                     i.Email.ToLower() == req.Email.ToLower() &&
                     i.AcceptedAt == null);
            if (existingInvite != null)
                _db.OrgInvites.Remove(existingInvite);

            var token  = Guid.NewGuid().ToString("N");
            var invite = new OrgInvite
            {
                OrgId     = orgId.Value,
                Email     = req.Email.Trim().ToLower(),
                Token     = token,
                Role      = role,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                CreatedAt = DateTime.UtcNow
            };
            _db.OrgInvites.Add(invite);
            await _db.SaveChangesAsync();

            // Send invite email
            var inviterName = User.FindFirst(ClaimTypes.Name)?.Value ?? "Your team";
            var baseUrl     = $"{Request.Scheme}://{Request.Host}";
            var acceptUrl   = $"{baseUrl}/Team/Accept/{token}";
            var roleName    = role == "manager" ? "Manager" : "Team Member";

            var html = $"""
                <!DOCTYPE html>
                <html>
                <head><meta charset='utf-8'></head>
                <body style='margin:0;padding:0;background:#0f172a;font-family:system-ui,sans-serif'>
                  <div style='max-width:520px;margin:32px auto;background:#1e293b;border-radius:12px;overflow:hidden;border:1px solid #334155'>
                    <div style='background:#f97316;padding:20px 28px'>
                      <h1 style='margin:0;color:#fff;font-size:20px;font-weight:700'>⚡ Team Invitation</h1>
                      <p style='margin:4px 0 0;color:#fff3e0;font-size:13px'>Candace Holder</p>
                    </div>
                    <div style='padding:28px'>
                      <p style='color:#cbd5e1;font-size:15px;margin:0 0 20px'>
                        <strong style='color:#f1f5f9'>{System.Net.WebUtility.HtmlEncode(inviterName)}</strong>
                        has invited you to join
                        <strong style='color:#f1f5f9'>{System.Net.WebUtility.HtmlEncode(org.Name)}</strong>
                        on Candace Holder as a <strong style='color:#f97316'>{roleName}</strong>.
                      </p>
                      <a href='{acceptUrl}'
                         style='display:inline-block;background:#f97316;color:#fff;font-weight:700;
                                font-size:14px;padding:14px 28px;border-radius:8px;text-decoration:none'>
                        Accept Invitation →
                      </a>
                      <p style='color:#64748b;font-size:12px;margin-top:24px'>
                        This invitation expires in 7 days. If you didn't expect this, you can ignore it.
                      </p>
                    </div>
                  </div>
                </body>
                </html>
                """;

            if (_email.IsConfigured)
            {
                var sent = await _email.SendAsync(
                    req.Email,
                    $"You've been invited to join {org.Name} on Candace Holder",
                    html);
                if (!sent)
                    _logger.LogWarning("TeamController: invite email failed to deliver to {Email}", req.Email);
            }
            else
            {
                _logger.LogWarning("TeamController: SMTP not configured — invite created but email not sent. Token: {Token}", token);
            }

            return Json(new {
                ok        = true,
                message   = _email.IsConfigured
                    ? $"Invitation sent to {req.Email}."
                    : $"Invitation created (SMTP not configured). Share this link: {acceptUrl}",
                acceptUrl = _email.IsConfigured ? (string?)null : acceptUrl
            });
        }

        // ── GET /Team/Accept/{token} — show accept-invite page ────────
        [HttpGet("Accept/{token}")]
        [AllowAnonymous]
        public async Task<IActionResult> Accept(string token)
        {
            var invite = await _db.OrgInvites
                .Include(i => i.Org)
                .FirstOrDefaultAsync(i => i.Token == token);

            if (invite == null || invite.AcceptedAt != null || invite.ExpiresAt < DateTime.UtcNow)
            {
                ViewBag.Error = invite == null
                    ? "This invitation link is invalid."
                    : invite.AcceptedAt != null
                        ? "This invitation has already been accepted."
                        : "This invitation has expired. Ask your team owner to send a new one.";
                return View("AcceptInvite");
            }

            // If user is logged in, accept immediately
            if (User.Identity?.IsAuthenticated == true)
            {
                var userId = CurrentUserId;
                var user   = await _db.Users.FindAsync(userId);
                if (user != null)
                {
                    // Check they're not already in a different org
                    if (user.OrgId != null && user.OrgId != invite.OrgId)
                    {
                        ViewBag.Error = "You're already a member of a different organization. Contact support to switch orgs.";
                        return View("AcceptInvite");
                    }

                    user.OrgId   = invite.OrgId;
                    user.OrgRole = invite.Role;

                    invite.AcceptedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync();

                    // Re-issue the auth cookie with updated org claims
                    await RefreshAuthCookieAsync(user);

                    ViewBag.Success  = true;
                    ViewBag.OrgName  = invite.Org?.Name ?? "your team";
                    ViewBag.OrgRole  = invite.Role;
                    return View("AcceptInvite");
                }
            }

            // Not logged in — new people create a password here; anyone who
            // already has an account signs in first.
            var inviteEmailNorm = invite.Email.Trim().ToLowerInvariant();
            ViewBag.HasAccount  = await _db.Users.AnyAsync(u => u.Email != null && u.Email.ToLower() == inviteEmailNorm);
            ViewBag.InviteEmail = invite.Email;
            ViewBag.OrgName     = invite.Org?.Name ?? "a team";
            ViewBag.Token       = token;
            ViewBag.NeedsLogin  = true;
            return View("AcceptInvite");
        }

        // ── POST /Team/Accept/{token} — logged-in accept after redirect ─
        [HttpPost("Accept/{token}")]
        public async Task<IActionResult> AcceptPost(string token)
        {
            var invite = await _db.OrgInvites
                .Include(i => i.Org)
                .FirstOrDefaultAsync(i => i.Token == token);

            if (invite == null || invite.AcceptedAt != null || invite.ExpiresAt < DateTime.UtcNow)
            {
                ViewBag.Error = "This invitation link is no longer valid.";
                return View("AcceptInvite");
            }

            var userId = CurrentUserId;
            var user   = await _db.Users.FindAsync(userId);
            if (user == null)
            {
                ViewBag.Error = "Could not find your account. Please sign in and try again.";
                return View("AcceptInvite");
            }

            if (user.OrgId != null && user.OrgId != invite.OrgId)
            {
                ViewBag.Error = "You're already a member of a different organization.";
                return View("AcceptInvite");
            }

            user.OrgId   = invite.OrgId;
            user.OrgRole = invite.Role;

            invite.AcceptedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await RefreshAuthCookieAsync(user);

            ViewBag.Success = true;
            ViewBag.OrgName = invite.Org?.Name ?? "your team";
            ViewBag.OrgRole = invite.Role;
            return View("AcceptInvite");
        }

        // ── POST /Team/Accept/{token}/Register — new user sets a password ─
        // Sign-up is closed site-wide, so a valid invite link is what lets a
        // new person create an account. The email is fixed to the invited
        // address; the account joins the inviting org with the invited role.
        [HttpPost("Accept/{token}/Register")]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AcceptRegister(string token, string name, string password, string confirmPassword)
        {
            var invite = await _db.OrgInvites
                .Include(i => i.Org)
                .FirstOrDefaultAsync(i => i.Token == token);

            if (invite == null || invite.AcceptedAt != null || invite.ExpiresAt < DateTime.UtcNow)
            {
                ViewBag.Error = "This invitation link is no longer valid. Ask your team owner to send a new one.";
                return View("AcceptInvite");
            }

            // Re-show the form with an error, keeping what they typed.
            IActionResult Retry(string message)
            {
                ViewBag.InviteEmail = invite.Email;
                ViewBag.OrgName     = invite.Org?.Name ?? "a team";
                ViewBag.Token       = token;
                ViewBag.NeedsLogin  = true;
                ViewBag.Name        = name;
                ViewBag.FormError   = message;
                return View("AcceptInvite");
            }

            if (string.IsNullOrWhiteSpace(name))       return Retry("Please enter your name.");
            if ((password ?? "").Length < 8)           return Retry("Password must be at least 8 characters.");
            if (password != confirmPassword)            return Retry("Passwords don't match.");

            var normalizedEmail = invite.Email.Trim().ToLowerInvariant();
            if (await _db.Users.AnyAsync(u => u.Provider == "password" && u.ProviderId == normalizedEmail))
                return Retry("An account with this email already exists — use \"Sign in instead\" below.");

            var user = new User
            {
                Provider    = "password",
                ProviderId  = normalizedEmail,
                Email       = invite.Email.Trim(),
                DisplayName = name.Trim(),
                OrgId       = invite.OrgId,
                OrgRole     = invite.Role,
                CreatedAt   = DateTime.UtcNow
            };
            user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>().HashPassword(user, password!);
            _db.Users.Add(user);

            invite.AcceptedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await RefreshAuthCookieAsync(user);
            _logger.LogInformation("Invite accepted with new account: user={UserId} org={OrgId} role={Role}",
                user.Id, invite.OrgId, invite.Role);

            ViewBag.Success = true;
            ViewBag.OrgName = invite.Org?.Name ?? "your team";
            ViewBag.OrgRole = invite.Role;
            return View("AcceptInvite");
        }

        // ── DELETE /Team/Members/{id} — remove member from org ────────
        [HttpDelete("Members/{id:long}")]
        public async Task<IActionResult> RemoveMember(long id)
        {
            if (CurrentOrgRole != "owner")
                return StatusCode(403, new { error = "Only owners can remove members." });

            if (id == CurrentUserId)
                return BadRequest(new { error = "You cannot remove yourself from the org." });

            var orgId  = CurrentOrgId;
            var member = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && u.OrgId == orgId);
            if (member == null)
                return NotFound(new { error = "Member not found." });

            // Don't remove another owner
            if (member.OrgRole == "owner")
                return BadRequest(new { error = "Cannot remove another owner. Transfer ownership first." });

            member.OrgId   = null;
            member.OrgRole = "owner"; // reset to default for their next org
            await _db.SaveChangesAsync();

            return Json(new { ok = true });
        }

        // ── PATCH /Team/Members/{id}/Role ─────────────────────────────
        [HttpPatch("Members/{id:long}/Role")]
        public async Task<IActionResult> UpdateRole(long id, [FromBody] UpdateRoleRequest req)
        {
            if (CurrentOrgRole != "owner")
                return StatusCode(403, new { error = "Only owners can change roles." });

            if (id == CurrentUserId)
                return BadRequest(new { error = "You cannot change your own role." });

            var valid = new[] { "manager", "rep" };
            if (!valid.Contains(req.Role))
                return BadRequest(new { error = "Invalid role." });

            var orgId  = CurrentOrgId;
            var member = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && u.OrgId == orgId);
            if (member == null)
                return NotFound(new { error = "Member not found." });

            member.OrgRole = req.Role;
            await _db.SaveChangesAsync();

            return Json(new { ok = true, role = req.Role });
        }

        // ── DELETE /Team/Invites/{id} — revoke a pending invite ───────
        [HttpDelete("Invites/{id:long}")]
        public async Task<IActionResult> RevokeInvite(long id)
        {
            if (!IsOwnerOrManager)
                return StatusCode(403, new { error = "Only owners and managers can revoke invites." });

            var orgId  = CurrentOrgId;
            var invite = await _db.OrgInvites.FirstOrDefaultAsync(
                i => i.Id == id && i.OrgId == orgId);
            if (invite == null) return NotFound(new { error = "Invite not found." });

            _db.OrgInvites.Remove(invite);
            await _db.SaveChangesAsync();

            return Json(new { ok = true });
        }

        // ── Helper: re-issue auth cookie with updated org claims ───────
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

        // ── DTOs ─────────────────────────────────────────────────────
        public record InviteRequest(
            [property: JsonPropertyName("email")] string Email,
            [property: JsonPropertyName("role")]  string Role = "rep");

        public class UpdateRoleRequest
        {
            [JsonPropertyName("role")] public string Role { get; set; } = "rep";
        }
    }
}
