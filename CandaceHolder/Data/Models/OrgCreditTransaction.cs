namespace CandaceHolder.Data.Models
{
    /// <summary>
    /// Immutable ledger entry for every credit change within an org.
    /// Negative amount = debit (usage). Positive amount = credit (top-up / adjustment).
    /// BalanceAfter is a snapshot so the ledger can be reconstructed without joins.
    /// </summary>
    public class OrgCreditTransaction
    {
        public long     Id            { get; set; }
        public long     OrgId         { get; set; }

        /// <summary>The user who triggered the action (null for system/Stripe events).</summary>
        public long?    UserId        { get; set; }

        /// <summary>enrichment | sms | search</summary>
        public string   CreditType    { get; set; } = "";

        /// <summary>Negative for usage, positive for top-ups / refunds.</summary>
        public int      Amount        { get; set; }

        /// <summary>Snapshot of org_credits.balance immediately after this transaction.</summary>
        public int      BalanceAfter  { get; set; }

        /// <summary>Human-readable reason, e.g. "Lead enriched: 123 Main St" or "Stripe top-up".</summary>
        public string   Description   { get; set; } = "";

        /// <summary>Optional ID of the related entity (lead id, Stripe payment_intent, etc.).</summary>
        public string?  ReferenceId   { get; set; }

        /// <summary>What kind of entity ReferenceId points to: lead | stripe_payment | admin</summary>
        public string?  ReferenceType { get; set; }

        public DateTime CreatedAt     { get; set; } = DateTime.UtcNow;

        public Org?  Org  { get; set; }
        public User? User { get; set; }
    }
}
