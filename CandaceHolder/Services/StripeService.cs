using Stripe;
using Stripe.Checkout;

namespace CandaceHolder.Services
{
    using CandaceHolder.Data.Models;
    // MailKit pulls in BouncyCastle.Cryptography transitively, which declares the
    // global namespace "Org.BouncyCastle.*" — that makes "Org" ambiguous with our
    // own Org model class (CS0118) unless aliased explicitly like this. The alias
    // must live INSIDE the namespace block (not at file scope) — at file scope it
    // collides with the global "Org" namespace itself (CS0576).
    using Org = CandaceHolder.Data.Models.Org;

    /// <summary>
    /// Thin wrapper around Stripe.net for the products in
    /// <see cref="PricingCatalog"/>: recurring subscriptions (starter, pro)
    /// and one-time purchases (topup, pack50). Reads product/price metadata
    /// from PricingCatalog rather than hardcoding it a second time here
    /// (100-pack removed 2026-07-23 — dropping it from the catalog is all
    /// that's needed for it to disappear from here too).
    ///
    /// Doesn't touch <see cref="ReportCreditService"/> directly — this class
    /// only talks to Stripe (create customers, Checkout Sessions, Portal
    /// Sessions, resolve a Price ID back to a product key). BillingController
    /// is what wires a completed checkout/webhook to the actual credit grant.
    ///
    /// Every method assumes <see cref="IsConfigured"/> is true; callers
    /// (BillingController) check that first and return a friendly error
    /// otherwise — see appsettings.json's "Stripe" section for what needs to
    /// be filled in before this works.
    /// </summary>
    public class StripeService
    {
        private readonly IConfiguration _config;

        public StripeService(IConfiguration config)
        {
            _config = config;
        }

        /// <summary>True once a Stripe secret key has been configured. Product Price IDs are checked per-call (a key can be set before all products exist).</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(_config["Stripe:SecretKey"]);

        /// <summary>Publishable key — safe to expose client-side, needed to load Stripe.js for embedded checkout.</summary>
        public string PublishableKey => _config["Stripe:PublishableKey"] ?? "";

        /// <summary>starter | pro (recurring) — subscription mode.</summary>
        public static readonly string[] SubscriptionProducts = PricingCatalog.Plans.Select(p => p.Key).ToArray();
        /// <summary>topup | pack50 — one-time payment mode.</summary>
        public static readonly string[] OneTimeProducts = PricingCatalog.Packs.Select(p => p.Key).ToArray();

        /// <summary>Report credits granted per one-time product — mirrors ReportCreditService.GrantPurchaseAsync's amounts.</summary>
        public static readonly IReadOnlyDictionary<string, int> OneTimeReportAmount =
            PricingCatalog.Packs.ToDictionary(p => p.Key, p => p.ReportAmount);

        /// <summary>Maps a product key to its "purchase_*" ReportCreditGrant source string (see ReportCreditService.GrantPurchaseAsync).</summary>
        public static readonly IReadOnlyDictionary<string, string> OneTimeGrantSource =
            PricingCatalog.Packs.ToDictionary(p => p.Key, p => p.GrantSource);

        private string PriceId(string product)
        {
            var configKey = PricingCatalog.PlanByKey(product)?.ConfigPriceIdKey
                ?? PricingCatalog.PackByKey(product)?.ConfigPriceIdKey;
            return configKey != null ? (_config[configKey] ?? "") : "";
        }

        /// <summary>Reverse lookup: given a Stripe Price ID (e.g. from an invoice line item on renewal), which product is it? Null if it doesn't match any configured price.</summary>
        public string? ProductForPriceId(string priceId)
        {
            foreach (var product in SubscriptionProducts.Concat(OneTimeProducts))
                if (!string.IsNullOrEmpty(priceId) && priceId == PriceId(product))
                    return product;
            return null;
        }

        /// <summary>True if every product's Price ID is filled in (not just the API key). Admin/setup-check convenience.</summary>
        public bool AllPricesConfigured =>
            SubscriptionProducts.Concat(OneTimeProducts).All(p => !string.IsNullOrWhiteSpace(PriceId(p)));

        /// <summary>Gets-or-creates a Stripe Customer for this org and returns its ID. Caller is responsible for saving org.StripeCustomerId to the DB.</summary>
        public async Task<string> EnsureCustomerAsync(Org org, string email, string name)
        {
            if (!string.IsNullOrWhiteSpace(org.StripeCustomerId))
                return org.StripeCustomerId;

            var service = new CustomerService();
            var customer = await service.CreateAsync(new CustomerCreateOptions
            {
                Email = email,
                Name  = name,
                Metadata = new Dictionary<string, string> { ["org_id"] = org.Id.ToString() }
            });
            return customer.Id;
        }

        /// <summary>
        /// Creates an embedded Checkout Session for one product and returns
        /// its ClientSecret — the frontend mounts Stripe's checkout iframe
        /// directly on our own page (ui_mode=embedded_page) instead of
        /// redirecting to a checkout.stripe.com URL. Subscription products
        /// (starter/pro) use mode=subscription; one-time products use
        /// mode=payment. See docs.stripe.com/checkout/embedded/quickstart.
        /// </summary>
        /// <param name="returnUrl">
        /// Where Stripe sends the browser after the payment attempt (success
        /// or failure). Must contain the literal "{CHECKOUT_SESSION_ID}"
        /// placeholder — Stripe substitutes it with the real session ID.
        /// </param>
        public async Task<string> CreateEmbeddedCheckoutSessionAsync(
            Org org, string customerId, string product, string returnUrl)
        {
            var priceId = PriceId(product);
            if (string.IsNullOrWhiteSpace(priceId))
                throw new InvalidOperationException($"Stripe price for \"{product}\" isn't configured (see appsettings.json Stripe section).");

            var isSubscription = SubscriptionProducts.Contains(product);

            var options = new SessionCreateOptions
            {
                Customer  = customerId,
                Mode      = isSubscription ? "subscription" : "payment",
                UiMode    = "embedded_page",
                LineItems = new List<SessionLineItemOptions>
                {
                    new() { Price = priceId, Quantity = 1 }
                },
                ReturnUrl = returnUrl,
                Metadata  = new Dictionary<string, string>
                {
                    ["org_id"]  = org.Id.ToString(),
                    ["product"] = product
                }
            };

            var service = new SessionService();
            var session = await service.CreateAsync(options);
            return session.ClientSecret;
        }

        /// <summary>
        /// Looks up a Checkout Session's status ("open" | "complete" |
        /// "expired") and the product it was for — used by the return page
        /// to decide whether to show a success message or bounce the
        /// customer back to Upgrade. Does not grant credits; that's still
        /// the webhook's job (see BillingController.Webhook).
        /// </summary>
        public async Task<(string Status, string? Product)> GetCheckoutSessionStatusAsync(string sessionId)
        {
            var service = new SessionService();
            var session = await service.GetAsync(sessionId);
            var product = session.Metadata != null && session.Metadata.TryGetValue("product", out var p) ? p : null;
            return (session.Status, product);
        }

        /// <summary>Creates a Stripe Customer Portal session (self-serve cancel/update-card/change-plan) and returns its URL.</summary>
        public async Task<string> CreatePortalSessionUrlAsync(string customerId, string returnUrl)
        {
            var service = new Stripe.BillingPortal.SessionService();
            var session = await service.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
            {
                Customer  = customerId,
                ReturnUrl = returnUrl
            });
            return session.Url;
        }
    }
}
