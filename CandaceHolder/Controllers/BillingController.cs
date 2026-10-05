using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using CandaceHolder.Data.Models;
using CandaceHolder.Filters;
using CandaceHolder.Services;
using Stripe;
using Stripe.Checkout;

namespace CandaceHolder.Controllers
{
    [Authorize]
    [Route("Billing")]
    [SkipTrialGate]
    public class BillingController : Controller
    {
        private readonly AppDbContext         _db;
        private readonly StripeService        _stripe;
        private readonly ReportCreditService  _reportCredits;
        private readonly ILogger<BillingController> _logger;
        private readonly string               _webhookSecret;

        public BillingController(AppDbContext db, StripeService stripe, ReportCreditService reportCredits,
            ILogger<BillingController> logger, IConfiguration config)
        {
            _db            = db;
            _stripe        = stripe;
            _reportCredits = reportCredits;
            _logger        = logger;
            _webhookSecret = config["Stripe:WebhookSecret"] ?? "";
        }

        private long? CurrentOrgId =>
            long.TryParse(User.FindFirst("user_org_id")?.Value, out var id) ? id : null;

        private long? CurrentUserId =>
            long.TryParse(User.FindFirst("user_db_id")?.Value, out var id) ? id : null;

        // ── GET /Billing/Upgrade ───────────────────────────────────────
        // Landing spot for the post-trial hard paywall (see TrialGateFilter).
        [HttpGet("Upgrade")]
        public async Task<IActionResult> Upgrade()
        {
            var orgId = CurrentOrgId;
            var org = orgId.HasValue
                ? await _db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId.Value)
                : null;

            ViewBag.TrialEndsAt   = org?.TrialEndsAt;
            ViewBag.TrialExpired  = org?.TrialEndsAt.HasValue == true && org!.TrialEndsAt!.Value < DateTime.UtcNow;
            ViewBag.CompanyName   = org?.CompanyName ?? org?.Name;
            ViewBag.StripeReady   = _stripe.IsConfigured && _stripe.AllPricesConfigured;
            ViewBag.StripePublishableKey = _stripe.PublishableKey;
            return View();
        }

        // ── POST /Billing/ContinueFree ──────────────────────────────────
        // "Free" fallback plan for anyone who doesn't want to pay right now.
        // This clears the paywall gate without any Stripe involvement.
        [HttpPost("ContinueFree")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ContinueFree()
        {
            var orgId = CurrentOrgId;
            if (orgId.HasValue)
            {
                var org = await _db.Orgs.FindAsync(orgId.Value);
                if (org != null)
                {
                    org.Plan        = "free";
                    org.TrialEndsAt = null;
                    await _db.SaveChangesAsync();
                }
            }
            return Redirect("/");
        }

        // ── POST /Billing/Checkout ──────────────────────────────────────
        // product = starter | pro | topup | pack50 (see PricingCatalog). Creates (or
        // reuses) a Stripe Customer for the org and returns a Checkout
        // Session ClientSecret as JSON — the Upgrade page's JS mounts
        // Stripe's embedded checkout iframe directly on our own page with
        // it (no redirect to checkout.stripe.com). Actual credit granting
        // happens in Webhook once Stripe confirms payment — nothing is
        // granted here.
        [HttpPost("Checkout")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Checkout(string product)
        {
            var validProducts = StripeService.SubscriptionProducts.Concat(StripeService.OneTimeProducts);
            if (!validProducts.Contains(product))
                return BadRequest(new { error = "Unknown product." });

            if (!_stripe.IsConfigured)
                return BadRequest(new { error = "Billing isn't set up yet — check back soon or contact support." });

            var orgId = CurrentOrgId;
            var org   = orgId.HasValue ? await _db.Orgs.FindAsync(orgId.Value) : null;
            if (org == null)
                return BadRequest(new { error = "No organization on your account — contact support." });

            var email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "";
            var name  = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value  ?? org.Name;

            try
            {
                var customerId = await _stripe.EnsureCustomerAsync(org, email, name);
                if (org.StripeCustomerId != customerId)
                {
                    org.StripeCustomerId = customerId;
                    await _db.SaveChangesAsync();
                }

                var baseUrl = $"{Request.Scheme}://{Request.Host}";
                // {CHECKOUT_SESSION_ID} is a literal placeholder Stripe substitutes
                // itself — {{ }} in a C# interpolated string just escapes to { }.
                var returnUrl = $"{baseUrl}/Billing/Success?session_id={{CHECKOUT_SESSION_ID}}";

                var clientSecret = await _stripe.CreateEmbeddedCheckoutSessionAsync(org, customerId, product, returnUrl);
                return Json(new { clientSecret });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stripe checkout session creation failed for org={OrgId} product={Product}", org.Id, product);
                return BadRequest(new { error = "Couldn't start checkout — " + ex.Message });
            }
        }

        // ── GET /Billing/SessionStatus ────────────────────────────────────
        // Polled by the Success return page (which only gets a session_id in
        // its query string) to find out whether the payment actually
        // completed and which product it was for. Doesn't grant credits —
        // that's still the webhook's job — this is purely for display.
        [HttpGet("SessionStatus")]
        public async Task<IActionResult> SessionStatus(string session_id)
        {
            if (string.IsNullOrWhiteSpace(session_id) || !_stripe.IsConfigured)
                return BadRequest(new { error = "Missing or invalid session." });

            try
            {
                var (status, product) = await _stripe.GetCheckoutSessionStatusAsync(session_id);
                return Json(new { status, product });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stripe session status lookup failed for session={SessionId}", session_id);
                return BadRequest(new { error = "Couldn't look up checkout status." });
            }
        }

        // ── GET /Billing/Success ─────────────────────────────────────────
        // Landing page after a Stripe Checkout attempt. Credits are granted
        // by the webhook, not here. This page only gets a session_id in its
        // query string (per Stripe's embedded-checkout return_url pattern),
        // so its JS calls SessionStatus above to find out whether the
        // payment actually completed and what was purchased.
        [HttpGet("Success")]
        public IActionResult Success()
        {
            return View();
        }

        // ── GET /Billing/Portal ──────────────────────────────────────────
        // Redirects to the Stripe Customer Portal (self-serve cancel / update
        // card / view invoices) for orgs that already have a Stripe Customer.
        [HttpGet("Portal")]
        public async Task<IActionResult> Portal()
        {
            var orgId = CurrentOrgId;
            var org   = orgId.HasValue ? await _db.Orgs.FindAsync(orgId.Value) : null;
            if (org == null || string.IsNullOrWhiteSpace(org.StripeCustomerId) || !_stripe.IsConfigured)
                return RedirectToAction(nameof(Upgrade));

            try
            {
                var returnUrl = $"{Request.Scheme}://{Request.Host}/";
                var portalUrl = await _stripe.CreatePortalSessionUrlAsync(org.StripeCustomerId, returnUrl);
                return Redirect(portalUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stripe portal session creation failed for org={OrgId}", org.Id);
                return RedirectToAction(nameof(Upgrade));
            }
        }

        // ── POST /Billing/Webhook ─────────────────────────────────────────
        // Stripe calls this unauthenticated, server-to-server, with a raw
        // JSON body signed via the Stripe-Signature header — hence
        // [AllowAnonymous] (overriding the class-level [Authorize]) and no
        // antiforgery token. See appsettings.json's "Stripe" section for how
        // to point a webhook endpoint at this URL and get WebhookSecret.
        [HttpPost("Webhook")]
        [AllowAnonymous]
        public async Task<IActionResult> Webhook()
        {
            if (string.IsNullOrWhiteSpace(_webhookSecret))
            {
                _logger.LogWarning("Stripe webhook received but Stripe:WebhookSecret isn't configured — ignoring.");
                return BadRequest();
            }

            var json = await new StreamReader(Request.Body).ReadToEndAsync();

            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(json, Request.Headers["Stripe-Signature"], _webhookSecret);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stripe webhook signature verification failed.");
                return BadRequest();
            }

            // Dedup: Stripe redelivers events (timeouts, manual resends). A
            // seen event is a no-op success, not an error, so Stripe doesn't
            // keep retrying it.
            var alreadyProcessed = await _db.StripeWebhookEvents.AnyAsync(e => e.EventId == stripeEvent.Id);
            if (alreadyProcessed) return Ok();

            try
            {
                await HandleEventAsync(stripeEvent);
            }
            catch (Exception ex)
            {
                // Don't record as processed — returning 500 makes Stripe
                // retry with backoff, which is what we want for a transient
                // failure (DB hiccup, etc.) rather than silently dropping a
                // paid credit grant.
                _logger.LogError(ex, "Stripe webhook processing failed for event {EventId} ({EventType})", stripeEvent.Id, stripeEvent.Type);
                return StatusCode(500);
            }

            _db.StripeWebhookEvents.Add(new StripeWebhookEvent
            {
                EventId     = stripeEvent.Id,
                EventType   = stripeEvent.Type,
                ProcessedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            return Ok();
        }

        private async Task HandleEventAsync(Event stripeEvent)
        {
            switch (stripeEvent.Type)
            {
                case "checkout.session.completed":
                    await HandleCheckoutCompletedAsync(stripeEvent);
                    break;
                case "invoice.paid":
                    await HandleInvoicePaidAsync(stripeEvent);
                    break;
                case "customer.subscription.deleted":
                    await HandleSubscriptionDeletedAsync(stripeEvent);
                    break;
                default:
                    // Not an event type we act on — ack and move on.
                    break;
            }
        }

        // First payment for either a subscription or a one-time pack.
        private async Task HandleCheckoutCompletedAsync(Event stripeEvent)
        {
            if (stripeEvent.Data.Object is not Session session) return;

            var product = session.Metadata != null && session.Metadata.TryGetValue("product", out var p) ? p : null;
            var orgIdStr = session.Metadata != null && session.Metadata.TryGetValue("org_id", out var o) ? o : null;
            if (product == null || !long.TryParse(orgIdStr, out var orgId))
            {
                _logger.LogWarning("checkout.session.completed missing product/org_id metadata (session={SessionId})", session.Id);
                return;
            }

            var org = await _db.Orgs.FindAsync(orgId);
            if (org == null)
            {
                _logger.LogWarning("checkout.session.completed for unknown org={OrgId} (session={SessionId})", orgId, session.Id);
                return;
            }

            if (StripeService.SubscriptionProducts.Contains(product))
            {
                org.Plan = product;
                org.StripeSubscriptionId = session.SubscriptionId;
                await _db.SaveChangesAsync();
                await _reportCredits.GrantSubscriptionAllotmentAsync(org.Id, product, userId: null);
            }
            else if (StripeService.OneTimeGrantSource.TryGetValue(product, out var source) &&
                     StripeService.OneTimeReportAmount.TryGetValue(product, out var amount))
            {
                await _reportCredits.GrantPurchaseAsync(org.Id, amount, source, userId: null,
                    description: $"Stripe purchase: {product} (session {session.Id})");
            }
        }

        // Subscription renewal — grant the next cycle's allotment.
        private async Task HandleInvoicePaidAsync(Event stripeEvent)
        {
            if (stripeEvent.Data.Object is not Invoice invoice) return;
            if (string.IsNullOrWhiteSpace(invoice.CustomerId)) return;

            // Stripe replaced the old top-level "price" field on invoice line
            // items with Pricing.PriceDetails — PriceId is the plain string
            // Price ID; PriceDetails.Price is the expandable Stripe.Price
            // object (not requested here, so it's null) — see
            // docs.stripe.com/api/invoice-line-item.
            var priceId = invoice.Lines?.Data?.FirstOrDefault()?.Pricing?.PriceDetails?.PriceId;
            var product = priceId != null ? _stripe.ProductForPriceId(priceId) : null;
            if (product == null || !StripeService.SubscriptionProducts.Contains(product)) return;

            var org = await _db.Orgs.FirstOrDefaultAsync(o => o.StripeCustomerId == invoice.CustomerId);
            if (org == null)
            {
                _logger.LogWarning("invoice.paid for unknown Stripe customer={CustomerId}", invoice.CustomerId);
                return;
            }

            // First invoice of a new subscription is already granted by
            // checkout.session.completed — only grant here for renewals.
            if (invoice.BillingReason == "subscription_create") return;

            await _reportCredits.GrantSubscriptionAllotmentAsync(org.Id, product, userId: null);
        }

        // Subscription canceled (self-serve via Portal, or non-payment) —
        // forfeit any banked rollover, same as the /Admin forfeit button.
        private async Task HandleSubscriptionDeletedAsync(Event stripeEvent)
        {
            if (stripeEvent.Data.Object is not Subscription subscription) return;

            var org = await _db.Orgs.FirstOrDefaultAsync(o => o.StripeSubscriptionId == subscription.Id);
            if (org == null) return;

            org.StripeSubscriptionId = null;
            org.Plan = "free";
            await _db.SaveChangesAsync();

            await _reportCredits.ForfeitSubscriptionCreditsAsync(org.Id, userId: null,
                reason: $"Stripe subscription canceled ({subscription.Id})");
        }
    }
}
