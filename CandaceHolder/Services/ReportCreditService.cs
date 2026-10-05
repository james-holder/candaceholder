using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using CandaceHolder.Data.Models;

namespace CandaceHolder.Services
{
    /// <summary>
    /// Grant/consume/forfeit logic for PDF hail-report credits, implementing
    /// the pricing policy locked in docs/pricing-refresh-punchlist.md. All
    /// prices/allotments live in <see cref="PricingCatalog"/> — this class
    /// reads that catalog rather than hardcoding numbers.
    ///
    ///   - Trial: 1 report, granted once at signup, never expires.
    ///   - Starter ($14.99/mo): 10 reports/mo. Does NOT roll over — each
    ///     billing cycle replaces the prior one (policy changed 2026-07-23;
    ///     previously banked up to 3x/60 days). Forfeited immediately on
    ///     cancel/downgrade too, same as always.
    ///   - Pro ($39.99/mo): unlimited (changed 2026-07-23 from 40 reports/mo).
    ///     Unlimited plans never touch the credit ledger at all — see
    ///     <see cref="GrantSubscriptionAllotmentAsync"/> and
    ///     PricingCatalog.IsUnlimitedPlan, which LeadsController uses to skip
    ///     the balance check/consumption entirely for these orgs.
    ///   - Top-up ($5.99/3) and 50-pack ($59.99): one-time purchases, never
    ///     expire, never forfeited — kept separate from the subscription
    ///     accounting below. (100-pack removed 2026-07-23 — redundant now
    ///     that Pro is unlimited. Any org with pre-existing purchase_pack100
    ///     credits keeps them; see PricingCatalog's removal note.)
    ///
    /// Built on <see cref="ReportCreditGrant"/> (a per-batch ledger, not a
    /// single running balance) because that mix of expiring/non-expiring
    /// credit can't be represented as one scalar balance. Every grant/consume
    /// also writes an <see cref="OrgCreditTransaction"/> row (CreditType =
    /// "report") so the existing admin/reporting ledger surfaces report-
    /// credit activity the same way it already does for enrichment credits.
    ///
    /// NOTE: This service is fully wired but inert in production until
    /// FeatureFlags:ReportCreditsEnabled is turned on (see LeadsController.
    /// Report) — see docs/pricing-refresh-punchlist.md Workstream A for the
    /// remaining steps (Stripe checkout, existing-org migration) before that
    /// flag should flip.
    /// </summary>
    public class ReportCreditService
    {
        private readonly AppDbContext _db;

        public ReportCreditService(AppDbContext db)
        {
            _db = db;
        }

        /// <summary>Total usable report credits for an org right now (all non-expired grants, any source).</summary>
        public async Task<int> GetBalanceAsync(long orgId)
        {
            var now = DateTime.UtcNow;
            return await _db.ReportCreditGrants
                .Where(g => g.OrgId == orgId && g.Remaining > 0 && (g.ExpiresAt == null || g.ExpiresAt > now))
                .SumAsync(g => (int?)g.Remaining) ?? 0;
        }

        /// <summary>One-time trial grant (1 report, never expires) — called once at signup.</summary>
        public Task GrantTrialAsync(long orgId, long? userId, int amount = 1) =>
            GrantAsync(orgId, amount, source: "trial", expiresAt: null, userId,
                description: "Trial allotment");

        /// <summary>
        /// Grants a subscription's monthly report allotment. Subscriptions do
        /// NOT roll over (policy changed 2026-07-23): any unused balance from
        /// the prior cycle is forfeited first, then the full monthly amount
        /// is granted fresh — a hard reset, not a bank. This also cleanly
        /// handles a mid-cycle plan change (e.g. starter -> pro), since the
        /// old plan's leftover credits are zeroed before the new grant.
        ///
        /// Unlimited plans (Pro) grant nothing — there's no ledger balance to
        /// track for them at all; callers should check
        /// PricingCatalog.IsUnlimitedPlan and skip credit gating entirely.
        /// Returns the amount actually granted (0 for an unlimited plan).
        /// </summary>
        public async Task<int> GrantSubscriptionAllotmentAsync(long orgId, string plan, long? userId)
        {
            var planInfo = PricingCatalog.PlanByKey(plan);
            if (planInfo == null)
                throw new ArgumentException($"'{plan}' is not a recurring report-credit plan.", nameof(plan));

            if (planInfo.IsUnlimited) return 0;

            var monthlyAmount = planInfo.MonthlyReportAllotment!.Value;

            await ForfeitSubscriptionCreditsAsync(orgId, userId, reason: $"Cycle reset ({plan}) — subscriptions don't roll over");

            await GrantAsync(orgId, monthlyAmount, source: "subscription",
                expiresAt: null, userId,
                description: $"Monthly allotment ({plan}, {monthlyAmount}/mo — no rollover)");

            return monthlyAmount;
        }

        /// <summary>One-time purchase grant (top-up, 50-pack) — never expires, never forfeited.</summary>
        public Task GrantPurchaseAsync(long orgId, int amount, string source, long? userId, string description)
        {
            if (source is not ("purchase_topup" or "purchase_pack50"))
                throw new ArgumentException($"'{source}' is not a recognized purchase source.", nameof(source));

            return GrantAsync(orgId, amount, source, expiresAt: null, userId, description);
        }

        /// <summary>Manual admin grant — e.g. seeding an existing org's balance before enabling the entitlement gate. Never expires.</summary>
        public Task GrantAdminAsync(long orgId, int amount, long? adminUserId, string description) =>
            GrantAsync(orgId, amount, source: "admin_grant", expiresAt: null, adminUserId, description);

        private async Task GrantAsync(long orgId, int amount, string source, DateTime? expiresAt, long? userId, string description)
        {
            if (amount <= 0) return;

            _db.ReportCreditGrants.Add(new ReportCreditGrant
            {
                OrgId           = orgId,
                Source          = source,
                Amount          = amount,
                Remaining       = amount,
                ExpiresAt       = expiresAt,
                CreatedByUserId = userId,
                Description     = description,
                GrantedAt       = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            var balanceAfter = await GetBalanceAsync(orgId);
            _db.OrgCreditTransactions.Add(new OrgCreditTransaction
            {
                OrgId         = orgId,
                UserId        = userId,
                CreditType    = "report",
                Amount        = amount,
                BalanceAfter  = balanceAfter,
                Description   = description,
                ReferenceType = source,
                CreatedAt     = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// Spends report credits. Draws down "subscription"-sourced credits
        /// first (they're forfeited at the next cycle reset regardless of
        /// use, so spending them before permanent purchased/admin credits
        /// avoids wasting them), then trial, then admin grants, then
        /// purchased packs last (these never expire, so there's no urgency
        /// to spend them). All-or-nothing: returns false without consuming
        /// anything if the org doesn't have enough.
        /// </summary>
        public async Task<bool> ConsumeAsync(long orgId, int amount, long? userId, string? referenceId = null, string? referenceType = null, string description = "PDF hail report generated")
        {
            if (amount <= 0) return true;

            var now = DateTime.UtcNow;
            var grants = await _db.ReportCreditGrants
                .Where(g => g.OrgId == orgId && g.Remaining > 0 && (g.ExpiresAt == null || g.ExpiresAt > now))
                .ToListAsync();

            var ordered = grants
                .OrderBy(g => g.Source switch
                {
                    "subscription" => 0,
                    "trial"        => 1,
                    "admin_grant"  => 2,
                    _              => 3 // purchase_* — never expire, spend last
                })
                .ThenBy(g => g.ExpiresAt ?? DateTime.MaxValue)
                .ToList();

            var available = ordered.Sum(g => g.Remaining);
            if (available < amount) return false;

            var remainingToConsume = amount;
            foreach (var grant in ordered)
            {
                if (remainingToConsume <= 0) break;
                var take = Math.Min(grant.Remaining, remainingToConsume);
                grant.Remaining -= take;
                remainingToConsume -= take;
            }

            await _db.SaveChangesAsync();

            var balanceAfter = await GetBalanceAsync(orgId);
            _db.OrgCreditTransactions.Add(new OrgCreditTransaction
            {
                OrgId         = orgId,
                UserId        = userId,
                CreditType    = "report",
                Amount        = -amount,
                BalanceAfter  = balanceAfter,
                Description   = description,
                ReferenceId   = referenceId,
                ReferenceType = referenceType,
                CreatedAt     = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            return true;
        }

        /// <summary>
        /// Zeroes out any remaining "subscription"-sourced credits (never
        /// touches purchase_*/admin_grant/trial credits) — called both when
        /// an org cancels/downgrades AND at the start of every renewal grant
        /// now that subscriptions don't roll over (see
        /// GrantSubscriptionAllotmentAsync). Returns the amount forfeited.
        /// </summary>
        public async Task<int> ForfeitSubscriptionCreditsAsync(long orgId, long? userId = null, string reason = "Subscription canceled/downgraded")
        {
            var now = DateTime.UtcNow;
            var grants = await _db.ReportCreditGrants
                .Where(g => g.OrgId == orgId && g.Source == "subscription" &&
                            g.Remaining > 0 && (g.ExpiresAt == null || g.ExpiresAt > now))
                .ToListAsync();

            var forfeited = grants.Sum(g => g.Remaining);
            if (forfeited == 0) return 0;

            foreach (var grant in grants) grant.Remaining = 0;
            await _db.SaveChangesAsync();

            var balanceAfter = await GetBalanceAsync(orgId);
            _db.OrgCreditTransactions.Add(new OrgCreditTransaction
            {
                OrgId         = orgId,
                UserId        = userId,
                CreditType    = "report",
                Amount        = -forfeited,
                BalanceAfter  = balanceAfter,
                Description   = reason,
                ReferenceType = "forfeit",
                CreatedAt     = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            return forfeited;
        }
    }
}
