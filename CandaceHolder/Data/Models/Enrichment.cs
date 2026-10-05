namespace CandaceHolder.Data.Models
{
    public class Enrichment
    {
        public long     Id           { get; set; }
        public long?    UserId       { get; set; }
        public long?    LeadId       { get; set; }
        public string?  Address      { get; set; }
        public string   Status       { get; set; } = "pending";
        public string   Provider     { get; set; } = "batchskiptracing";
        public int      CreditsUsed  { get; set; } = 1;
        public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;

        public User? User { get; set; }
        public Lead? Lead { get; set; }
    }
}
