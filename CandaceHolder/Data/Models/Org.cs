namespace CandaceHolder.Data.Models
{
    public class Org
    {
        public long     Id        { get; set; }
        public string   Name      { get; set; } = "";
        public long?    OwnerId   { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // ── Branding ──────────────────────────────────────────────
        /// <summary>Display name for the org (may differ from org Name)</summary>
        public string? CompanyName    { get; set; }
        public string? CompanyEmail   { get; set; }
        public string? Phone          { get; set; }
        public string? Website        { get; set; }
        /// <summary>Hex color, e.g. #f97316</summary>
        public string? AccentColor    { get; set; }
        /// <summary>Logo height (px) in the header of branded emails; null = 56</summary>
        public int?    EmailLogoHeight { get; set; }
        /// <summary>Header background hex color, e.g. #0f172a</summary>
        public string? HeaderColor    { get; set; }
        public string? Tagline        { get; set; }
        /// <summary>Business license number, if any</summary>
        public string? LicenseNumber  { get; set; }
        /// <summary>Relative path to logo image, e.g. /uploads/logos/42.png</summary>
        public string? LogoPath       { get; set; }

        // ── Additional company info ──────────────────────────────
        /// <summary>Business mailing/physical address shown on Company Profile</summary>
        public string? Address           { get; set; }
        public string? FacebookUrl       { get; set; }
        public string? InstagramUrl      { get; set; }
        /// <summary>Link to Google Business Profile (great for reviews / local SEO)</summary>
        public string? GoogleBusinessUrl { get; set; }

        public User?                    Owner   { get; set; }
        public ICollection<User>        Members { get; set; } = new List<User>();
        public ICollection<OrgInvite>   Invites { get; set; } = new List<OrgInvite>();
    }
}
