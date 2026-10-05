namespace CandaceHolder.Data.Models
{
    public class Org
    {
        public long     Id        { get; set; }
        public string   Name      { get; set; } = "";
        public long?    OwnerId   { get; set; }
        /// <summary>free | pro | agency</summary>
        public string   Plan      { get; set; } = "free";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>When the org's trial period ends. Null = no active trial (legacy org or paid plan).</summary>
        public DateTime? TrialEndsAt { get; set; }

        // ── Stripe ────────────────────────────────────────────────
        /// <summary>Stripe Customer ID (cus_...). Created on first checkout, reused for every later purchase/subscription.</summary>
        public string? StripeCustomerId     { get; set; }
        /// <summary>Active Stripe Subscription ID (sub_...) for Starter/Pro. Null if no subscription (trial, one-time-pack-only, or canceled).</summary>
        public string? StripeSubscriptionId { get; set; }

        // ── Branding ──────────────────────────────────────────────
        /// <summary>Display name used on PDF reports (may differ from org Name)</summary>
        public string? CompanyName    { get; set; }
        public string? CompanyEmail   { get; set; }
        public string? Phone          { get; set; }
        public string? Website        { get; set; }
        /// <summary>Hex color, e.g. #f97316</summary>
        public string? AccentColor    { get; set; }
        /// <summary>Header background hex color on PDF reports, e.g. #0f172a</summary>
        public string? HeaderColor    { get; set; }
        public string? Tagline        { get; set; }
        /// <summary>Contractor / roofing license number shown on PDF</summary>
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
