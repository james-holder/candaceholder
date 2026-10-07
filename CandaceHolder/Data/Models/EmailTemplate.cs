namespace CandaceHolder.Data.Models
{
    /// <summary>
    /// A reusable email for traced leads. Subject and body may contain
    /// {{variables}} — see Services/TemplateRenderer.cs for the list.
    /// </summary>
    public class EmailTemplate
    {
        public long     Id        { get; set; }
        public long     OrgId     { get; set; }
        public string   Name      { get; set; } = "";
        public string   Subject   { get; set; } = "";
        /// <summary>Plain text; line breaks are kept when sent.</summary>
        public string   Body      { get; set; } = "";
        /// <summary>Branded layout (logo, colors from Company Profile) vs. a plain personal-looking email.</summary>
        public bool     Branded   { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>One email sent to a lead's owner — shown on the lead's Activity timeline.</summary>
    public class EmailSend
    {
        public long      Id         { get; set; }
        public long      OrgId      { get; set; }
        public long      LeadId     { get; set; }
        public long?     TemplateId { get; set; }
        public long?     UserId     { get; set; }
        public string    ToEmail    { get; set; } = "";
        public string    Subject    { get; set; } = "";
        /// <summary>sent | failed</summary>
        public string    Status     { get; set; } = "sent";
        public string?   Error      { get; set; }
        public DateTime  SentAt     { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// An address that unsubscribed. Every send skips these — required by
    /// CAN-SPAM, so opt-outs are permanent until removed by hand.
    /// </summary>
    public class EmailOptOut
    {
        public long     Id        { get; set; }
        public long     OrgId     { get; set; }
        /// <summary>Lower-cased email address.</summary>
        public string   Email     { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
