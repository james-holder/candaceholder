namespace CandaceHolder.Data.Models
{
    /// <summary>
    /// Dedup record for a processed Stripe webhook event. Stripe redelivers
    /// events (timeouts, manual resends from the dashboard), and since a
    /// webhook directly grants report credits, processing the same event
    /// twice would double-grant. BillingController.Webhook checks this table
    /// before processing and only inserts a row after successful processing
    /// — a failed/retried delivery is deliberately left unrecorded so Stripe's
    /// retry actually reprocesses it.
    /// </summary>
    public class StripeWebhookEvent
    {
        /// <summary>Stripe's event ID (evt_...) — primary key, not auto-increment.</summary>
        public string EventId { get; set; } = "";
        public string EventType { get; set; } = "";
        public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
    }
}
