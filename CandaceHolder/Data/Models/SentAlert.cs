namespace CandaceHolder.Data.Models
{
    public class SentAlert
    {
        public long     Id                { get; set; }
        public long?    UserId            { get; set; }
        public long?    OrgId             { get; set; }
        public long     WatchedAreaId     { get; set; }
        public DateTime EventDate         { get; set; }   // date of the hail event
        public double   HailSizeInches    { get; set; }
        public DateTime SentAt            { get; set; } = DateTime.UtcNow;

        // Nav
        public User?        User        { get; set; }
        public WatchedArea? WatchedArea { get; set; }
    }
}
