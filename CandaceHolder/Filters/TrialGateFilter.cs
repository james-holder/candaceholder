using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using System.Security.Claims;

namespace CandaceHolder.Filters
{
    /// <summary>
    /// Global action filter that enforces the post-trial hard paywall.
    /// Once an org's <c>TrialEndsAt</c> has passed (and it hasn't been
    /// cleared by an upgrade), every action is redirected to
    /// /Billing/Upgrade unless the controller/action carries
    /// <see cref="SkipTrialGateAttribute"/>.
    ///
    /// Orgs with <c>TrialEndsAt == null</c> (legacy orgs created before this
    /// feature, or orgs that have completed a paid upgrade) are never gated.
    /// </summary>
    public class TrialGateFilter : IAsyncActionFilter
    {
        private readonly AppDbContext _db;
        private readonly string       _adminEmail;

        public TrialGateFilter(AppDbContext db, IConfiguration config)
        {
            _db         = db;
            _adminEmail = config["AdminEmail"] ?? "";
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var skip = false;

            if (context.ActionDescriptor is ControllerActionDescriptor)
            {
                skip = context.ActionDescriptor.EndpointMetadata
                    .Any(m => m is SkipTrialGateAttribute);
            }

            // Platform admins are never gated by the trial paywall — anywhere.
            var email = context.HttpContext.User.FindFirst(ClaimTypes.Email)?.Value ?? "";
            if (!string.IsNullOrEmpty(_adminEmail) &&
                string.Equals(email, _adminEmail, StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            if (context.HttpContext.User.Identity?.IsAuthenticated == true)
            {
                var orgIdClaim = context.HttpContext.User.FindFirst("user_org_id")?.Value;
                if (long.TryParse(orgIdClaim, out var orgId))
                {
                    var trialEndsAt = await _db.Orgs.AsNoTracking()
                        .Where(o => o.Id == orgId)
                        .Select(o => o.TrialEndsAt)
                        .FirstOrDefaultAsync();

                    if (trialEndsAt.HasValue)
                    {
                        if (trialEndsAt.Value < DateTime.UtcNow)
                        {
                            if (!skip)
                            {
                                var isAjax = context.HttpContext.Request.Headers["X-Requested-With"] == "XMLHttpRequest"
                                    || context.HttpContext.Request.Headers["Accept"].ToString().Contains("application/json")
                                    || !context.HttpContext.Request.Headers["Accept"].ToString().Contains("text/html");
                                if (isAjax)
                                    context.Result = new JsonResult(new { error = "trial_expired", message = "Your trial has ended. Please upgrade to continue." })
                                        { StatusCode = 402 };
                                else
                                    context.Result = new RedirectResult("/Billing/Upgrade");
                                return;
                            }
                        }
                        else if (context.Controller is Controller controller)
                        {
                            // Surface "X days left" so views can render a trial banner.
                            var daysLeft = (int)Math.Ceiling((trialEndsAt.Value - DateTime.UtcNow).TotalDays);
                            controller.ViewData["TrialDaysLeft"] = Math.Max(0, daysLeft);
                        }
                    }
                }
            }

            await next();
        }
    }
}
