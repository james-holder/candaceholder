namespace CandaceHolder.Data.Models
{
    public class LeadContact
    {
        public long      Id          { get; set; }
        public long      LeadId      { get; set; }
        public Lead?     Lead        { get; set; }
        public string?   Name        { get; set; }
        public string?   Phone       { get; set; }
        public string?   Email       { get; set; }
        /// <summary>owner | resident</summary>
        public string    ContactType { get; set; } = "owner";
        public bool      IsPrimary   { get; set; }
        public string    Source      { get; set; } = "whitepages";
        public DateTime  CreatedAt   { get; set; } = DateTime.UtcNow;
    }
}
