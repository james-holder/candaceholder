namespace CandaceHolder.Data.Models
{
    /// <summary>
    /// Tracks the credit balance for a specific resource type within an org.
    /// One row per (org_id, credit_type). Balance is decremented on use;
    /// Stripe webhooks (or admin top-ups) increment it.
    /// </summary>
    public class OrgCredit
    {
        public long     Id             { get; set; }
        public long     OrgId          { get; set; }

        /// <summary>enrichment | sms | search</summary>
        public string   CreditType     { get; set; } = "";

        /// <summary>Remaining credits available to spend.</summary>
        public int      Balance        { get; set; } = 0;

        /// <summary>Running total consumed in the current period (for reporting).</summary>
        public int      UsedThisPeriod { get; set; } = 0;

        /// <summary>When the current billing/usage period started.</summary>
        public DateTime PeriodStart    { get; set; } = DateTime.UtcNow;

        /// <summary>When the period resets (null = manual top-up / no expiry).</summary>
        public DateTime? PeriodEnd     { get; set; }

        public DateTime UpdatedAt      { get; set; } = DateTime.UtcNow;

        public Org? Org { get; set; }
    }
}
