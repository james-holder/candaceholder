namespace CandaceHolder.Data.Models
{
    public class User
    {
        public long    Id          { get; set; }
        public string  Provider    { get; set; } = "";
        public string  ProviderId  { get; set; } = "";
        public string? Email       { get; set; }
        public string? DisplayName { get; set; }
        /// <summary>ASP.NET Core Identity password hash — only set for Provider == "password" accounts created via signup.</summary>
        public string? PasswordHash { get; set; }
        /// <summary>Single-use forgot-password token — null unless a reset is currently in flight. Cleared on successful reset or use.</summary>
        public string?   PasswordResetToken     { get; set; }
        /// <summary>UTC expiry for PasswordResetToken — reset links are valid for 1 hour (see AuthController.ForgotPassword).</summary>
        public DateTime? PasswordResetExpiresAt { get; set; }
        public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
        public bool    IsAdmin     { get; set; }
        /// <summary>
        /// Platform admin tier: "user" (default) | "admin" | "super_admin".
        /// Admin panel access requires "admin" or "super_admin". Only "super_admin"
        /// can promote/demote other admins (see AdminController.IsSuperAdmin).
        /// Whoever authenticates via the Auth:AdminEmail/AdminPassword break-glass
        /// credential is always re-bootstrapped to "super_admin" on login, so that
        /// credential can never be locked out of admin access.
        /// </summary>
        public string  Role        { get; set; } = "user";
        public long?   OrgId      { get; set; }
        /// <summary>owner | manager | rep</summary>
        public string  OrgRole    { get; set; } = "owner";

        // ── My Profile ───────────────────────────────────────────
        /// <summary>Personal contact phone number, shown on My Profile</summary>
        public string? Phone             { get; set; }
        /// <summary>Email used for alert/notification delivery (may differ from login email)</summary>
        public string? NotificationEmail { get; set; }
        /// <summary>Email signature (sanitized HTML) — fills {{signature}} in emails this user sends.</summary>
        public string? EmailSignature    { get; set; }

        public Org?                      Org          { get; set; }
        public ICollection<Lead>         Leads        { get; set; } = new List<Lead>();
        public ICollection<Enrichment>   Enrichments  { get; set; } = new List<Enrichment>();
    }
}
