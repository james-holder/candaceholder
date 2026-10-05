namespace CandaceHolder.Data.Models
{
    public class Lead
    {
        public long    Id              { get; set; }
        public string  Address         { get; set; } = "";
        public double? Lat             { get; set; }
        public double? Lng             { get; set; }
        public int?    YearBuilt       { get; set; }  // from Regrid parcel data
        public string? PropertyType    { get; set; }
        public string? SourceAddress   { get; set; }
        public DateTime SavedAt        { get; set; } = DateTime.UtcNow;
        public string? Notes           { get; set; }
        public string? OwnerName       { get; set; }
        public string? OwnerPhone      { get; set; }
        public string? OwnerEmail      { get; set; }
        public long?     UserId             { get; set; }
        public long?     OrgId              { get; set; }
        public long?     AssignedToUserId   { get; set; }
        public bool      IsEnriched         { get; set; } = false;
        public DateTime? DeletedAt       { get; set; }
        /// <summary>Pipeline status: new | contacted | appointment_set | closed_won | closed_lost</summary>
        public string    Status          { get; set; } = "new";

        public User?                     User        { get; set; }
        public ICollection<Enrichment>   Enrichments { get; set; } = new List<Enrichment>();
        public ICollection<LeadContact>  Contacts    { get; set; } = new List<LeadContact>();
    }
}
