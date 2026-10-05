namespace CandaceHolder.Data.Models
{
    public class OrgInvite
    {
        public long      Id         { get; set; }
        public long      OrgId      { get; set; }
        public string    Email      { get; set; } = "";
        public string    Token      { get; set; } = "";
        /// <summary>rep | manager</summary>
        public string    Role       { get; set; } = "rep";
        public DateTime  ExpiresAt  { get; set; }
        public DateTime? AcceptedAt { get; set; }
        public DateTime  CreatedAt  { get; set; } = DateTime.UtcNow;

        public Org? Org { get; set; }
    }
}
