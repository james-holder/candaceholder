namespace CandaceHolder.Data.Models
{
    public class WatchedArea
    {
        public long     Id                { get; set; }
        public long?    UserId            { get; set; }
        public long?    OrgId             { get; set; }
        public string   Label             { get; set; } = "";   // e.g. "Dallas, TX"
        public double   CenterLat         { get; set; }
        public double   CenterLng         { get; set; }
        public double   RadiusMiles       { get; set; } = 10.0;
        public double   MinHailSizeInches { get; set; } = 1.0;  // default: quarter-sized
        public bool     AlertsEnabled     { get; set; } = true;
        public DateTime CreatedAt         { get; set; } = DateTime.UtcNow;

        // Nav
        public User?              User       { get; set; }
        public List<SentAlert>    SentAlerts { get; set; } = new();
    }
}
