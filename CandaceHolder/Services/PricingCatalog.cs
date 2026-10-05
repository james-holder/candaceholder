namespace CandaceHolder.Services
{
    /// <summary>
    /// Single source of truth for StormLead Pro's plans and one-time report
    /// packs — price, report allotment (or unlimited), and the Stripe/config
    /// keys that tie a plan to its Stripe Price ID. Added 2026-07-23 so a
    /// price or allotment change is made in exactly one place instead of
    /// hand-syncing Views, controllers, JS, and appsettings.json separately
    /// (see docs/pricing-refresh-punchlist.md for the incident that prompted
    /// this — the Help page was found showing swapped/stale pack prices after
    /// a prior revision missed that one spot).
    ///
    /// Controllers/services should read <see cref="Plans"/>/<see cref="Packs"/>
    /// (or the named <see cref="Starter"/>/<see cref="Pro"/>/<see cref="Topup"/>/
    /// <see cref="Pack50"/> statics) instead of hardcoding dollar amounts or
    /// report counts. Views can inject/reference this class directly (it's a
    /// plain static class, no DI needed) for the same reason.
    /// </summary>
    public static class PricingCatalog
    {
        /// <summary>A recurring subscription tier.</summary>
        public sealed class Plan
        {
            public required string Key             { get; init; } // "starter" | "pro" — matches Org.Plan and ReportCreditGrant source conventions
            public required string DisplayName      { get; init; }
            public required decimal MonthlyPrice    { get; init; }
            /// <summary>Reports granted per billing cycle. Null = unlimited (no credit gating at all — see PricingCatalog.IsUnlimitedPlan).</summary>
            public required int? MonthlyReportAllotment { get; init; }
            public required string ConfigPriceIdKey { get; init; } // e.g. "Stripe:StarterPriceId"

            public bool IsUnlimited => MonthlyReportAllotment is null;

            /// <summary>Null when unlimited — there's no meaningful per-report figure to show.</summary>
            public decimal? PricePerReport =>
                MonthlyReportAllotment is int n and > 0 ? Math.Round(MonthlyPrice / n, 2) : null;

            public string PriceLabel => $"${MonthlyPrice:0.00}/month";

            public string AllotmentLabel => IsUnlimited
                ? "Unlimited PDF hail reports"
                : $"{MonthlyReportAllotment} PDF hail reports every month";

            public string PerReportNote => IsUnlimited
                ? "No monthly report limit"
                : $"{MonthlyReportAllotment} PDF reports/mo — about ${PricePerReport:0.00} each";
        }

        /// <summary>A one-time (non-recurring) report pack.</summary>
        public sealed class Pack
        {
            public required string Key             { get; init; } // "topup" | "pack50" — matches ReportCreditGrant "purchase_*" source suffix
            public required string DisplayName      { get; init; }
            public required decimal Price           { get; init; }
            public required int ReportAmount        { get; init; }
            public required string ConfigPriceIdKey { get; init; }

            /// <summary>The ReportCreditGrant/OrgCreditTransaction source string for this pack — always "purchase_" + Key.</summary>
            public string GrantSource => "purchase_" + Key;

            public decimal PricePerReport => Math.Round(Price / ReportAmount, 2);

            public string PriceLabel => $"${Price:0.00}";
            public string PerReportLabel => $"(~${PricePerReport:0.00} each)";
        }

        // ── Recurring plans ──────────────────────────────────────────────
        // Pro made unlimited 2026-07-23 (was 40 reports/mo, revised 2026-07-16
        // from 75) — see docs/pricing-refresh-punchlist.md. Represented as a
        // null allotment (not a large sentinel number) so nothing downstream
        // — rollover caps, per-report math — has to special-case a fake huge
        // integer; callers check IsUnlimited/IsUnlimitedPlan instead.
        public static readonly Plan Starter = new()
        {
            Key = "starter", DisplayName = "Starter", MonthlyPrice = 14.99m,
            MonthlyReportAllotment = 10, ConfigPriceIdKey = "Stripe:StarterPriceId"
        };

        public static readonly Plan Pro = new()
        {
            Key = "pro", DisplayName = "Pro", MonthlyPrice = 39.99m,
            MonthlyReportAllotment = null, ConfigPriceIdKey = "Stripe:ProPriceId"
        };

        public static readonly IReadOnlyList<Plan> Plans = new[] { Starter, Pro };

        // ── One-time packs ───────────────────────────────────────────────
        // 100-pack removed 2026-07-23 (redundant now that Pro is unlimited —
        // anyone who'd want 100 reports/mo is better served by Pro). Existing
        // orgs that already bought a 100-pack keep their purchase_pack100
        // credits (never expire, never migrated) — see ReportCreditService.
        public static readonly Pack Topup = new()
        {
            Key = "topup", DisplayName = "3-report top-up", Price = 5.99m,
            ReportAmount = 3, ConfigPriceIdKey = "Stripe:TopupPriceId"
        };

        public static readonly Pack Pack50 = new()
        {
            Key = "pack50", DisplayName = "50-report pack", Price = 59.99m,
            ReportAmount = 50, ConfigPriceIdKey = "Stripe:Pack50PriceId"
        };

        public static readonly IReadOnlyList<Pack> Packs = new[] { Topup, Pack50 };

        public static Plan? PlanByKey(string? key) => key is null ? null : Plans.FirstOrDefault(p => p.Key == key);
        public static Pack? PackByKey(string? key) => key is null ? null : Packs.FirstOrDefault(p => p.Key == key);

        /// <summary>True if this org Plan value is a recurring plan with no report cap (currently: Pro). Used to bypass credit-balance gating entirely for these orgs.</summary>
        public static bool IsUnlimitedPlan(string? planKey) => PlanByKey(planKey)?.IsUnlimited == true;
    }
}
