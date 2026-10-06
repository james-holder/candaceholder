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
        /// <summary>Mobile | Land Line | VoIP … as reported by the skip-trace provider</summary>
        public string?   PhoneType   { get; set; }
        /// <summary>Phone is on a Do Not Call list — don't cold call or text it.</summary>
        public bool      IsDnc       { get; set; }
        /// <summary>Person is a known TCPA litigator (sues over calls/texts) — don't contact.</summary>
        public bool      IsLitigator { get; set; }
        /// <summary>owner | resident</summary>
        public string    ContactType { get; set; } = "owner";
        public bool      IsPrimary   { get; set; }
        public string    Source      { get; set; } = "batchdata";
        public DateTime  CreatedAt   { get; set; } = DateTime.UtcNow;
    }
}
