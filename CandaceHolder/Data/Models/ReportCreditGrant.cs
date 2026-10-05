namespace CandaceHolder.Data.Models
{
    /// <summary>
    /// One "batch" of PDF hail-report credits granted to an org — a trial
    /// allotment, a monthly subscription allotment (no rollover — reset each
    /// cycle, see ReportCreditService.GrantSubscriptionAllotmentAsync), or a
    /// one-time purchase (top-up / 50-pack).
    ///
    /// Unlike <see cref="OrgCredit"/> (a single running balance per org+type),
    /// report credits need per-batch tracking: subscription credits are wiped
    /// and replaced each billing cycle instead of banking (policy changed
    /// 2026-07-23), while purchased credits (top-up, 50-pack — and any
    /// pre-existing purchase_pack100 from before that pack was removed) never
    /// expire and are never forfeited. That mix can't be expressed as one
    /// scalar balance, so each grant is its own row with its own remaining
    /// balance and (nullable) expiration.
    ///
    /// See <see cref="Services.PricingCatalog"/> for current prices/
    /// allotments, docs/pricing-refresh-punchlist.md for the policy history,
    /// and <see cref="Services.ReportCreditService"/> for the grant/consume/
    /// forfeit logic built on top of this table.
    /// </summary>
    public class ReportCreditGrant
    {
        public long Id    { get; set; }
        public long OrgId { get; set; }

        /// <summary>trial | subscription | purchase_topup | purchase_pack50 | admin_grant (purchase_pack100 may still appear on old grants — the pack was removed 2026-07-23, existing credits are untouched)</summary>
        public string Source { get; set; } = "";

        /// <summary>Original size of this grant.</summary>
        public int Amount { get; set; }

        /// <summary>Unspent credits left in this specific grant. Decremented on use (see ReportCreditService.ConsumeAsync for draw-down order).</summary>
        public int Remaining { get; set; }

        /// <summary>When this batch expires and becomes unusable (even if Remaining > 0). Currently null for every source — subscriptions are reset explicitly instead of expiring passively.</summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>User who triggered the grant (null for system/admin-initiated grants).</summary>
        public long? CreatedByUserId { get; set; }

        /// <summary>Human-readable note, e.g. "Monthly allotment (starter)" or "Purchased: 50-report pack".</summary>
        public string Description { get; set; } = "";

        public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

        public Org? Org { get; set; }
    }
}
