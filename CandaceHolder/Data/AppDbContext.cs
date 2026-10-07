using Microsoft.EntityFrameworkCore;

namespace CandaceHolder.Data
{
    using CandaceHolder.Data.Models;
    // MailKit pulls in BouncyCastle.Cryptography transitively, which declares
    // the global namespace "Org.BouncyCastle.*" — that makes "Org" ambiguous
    // with our own Org model class (CS0118) unless aliased explicitly here.
    using Org = CandaceHolder.Data.Models.Org;

    public class AppDbContext : DbContext
    {
        public DbSet<User>         Users        => Set<User>();
        public DbSet<Org>          Orgs         => Set<Org>();
        public DbSet<OrgInvite>    OrgInvites   => Set<OrgInvite>();
        public DbSet<Lead>         Leads        => Set<Lead>();
        public DbSet<Enrichment>   Enrichments  => Set<Enrichment>();
        public DbSet<LeadContact>  LeadContacts => Set<LeadContact>();
        public DbSet<AppSetting>   AppSettings  => Set<AppSetting>();
        public DbSet<EmailTemplate> EmailTemplates => Set<EmailTemplate>();
        public DbSet<EmailSend>     EmailSends     => Set<EmailSend>();
        public DbSet<EmailOptOut>   EmailOptOuts   => Set<EmailOptOut>();

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder m)
        {
            // ── Org ──────────────────────────────────────────────────────
            m.Entity<Org>(e =>
            {
                e.ToTable("orgs");
                e.Property(o => o.EmailLogoHeight).HasColumnName("email_logo_height");
                e.HasKey(o => o.Id);
                e.Property(o => o.Id).HasColumnName("id");
                e.Property(o => o.Name).HasColumnName("name").IsRequired();
                e.Property(o => o.OwnerId).HasColumnName("owner_id");
                e.Property(o => o.CreatedAt).HasColumnName("created_at")
                 .HasDefaultValueSql("datetime('now')");
                e.Property(o => o.CompanyName).HasColumnName("company_name");
                e.Property(o => o.CompanyEmail).HasColumnName("company_email");
                e.Property(o => o.Phone).HasColumnName("phone");
                e.Property(o => o.Website).HasColumnName("website");
                e.Property(o => o.AccentColor).HasColumnName("accent_color");
                e.Property(o => o.HeaderColor).HasColumnName("header_color");
                e.Property(o => o.Tagline).HasColumnName("tagline");
                e.Property(o => o.LicenseNumber).HasColumnName("license_number");
                e.Property(o => o.LogoPath).HasColumnName("logo_path");
                e.Property(o => o.Address).HasColumnName("address");
                e.Property(o => o.FacebookUrl).HasColumnName("facebook_url");
                e.Property(o => o.InstagramUrl).HasColumnName("instagram_url");
                e.Property(o => o.GoogleBusinessUrl).HasColumnName("google_business_url");

                e.HasOne(o => o.Owner)
                 .WithMany()
                 .HasForeignKey(o => o.OwnerId)
                 .OnDelete(DeleteBehavior.SetNull);
            });

            // ── OrgInvite ─────────────────────────────────────────────────
            m.Entity<OrgInvite>(e =>
            {
                e.ToTable("org_invites");
                e.HasKey(i => i.Id);
                e.Property(i => i.Id).HasColumnName("id");
                e.Property(i => i.OrgId).HasColumnName("org_id");
                e.Property(i => i.Email).HasColumnName("email").IsRequired();
                e.Property(i => i.Token).HasColumnName("token").IsRequired();
                e.Property(i => i.Role).HasColumnName("role").HasDefaultValue("rep");
                e.Property(i => i.ExpiresAt).HasColumnName("expires_at");
                e.Property(i => i.AcceptedAt).HasColumnName("accepted_at");
                e.Property(i => i.CreatedAt).HasColumnName("created_at")
                 .HasDefaultValueSql("datetime('now')");

                e.HasIndex(i => i.Token).IsUnique();
                e.HasIndex(i => i.OrgId);

                e.HasOne(i => i.Org)
                 .WithMany(o => o.Invites)
                 .HasForeignKey(i => i.OrgId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── User ─────────────────────────────────────────────────────
            m.Entity<User>(e =>
            {
                e.ToTable("users");
                e.Property(u => u.EmailSignature).HasColumnName("email_signature");
                e.HasKey(u => u.Id);
                e.Property(u => u.Id).HasColumnName("id");
                e.Property(u => u.Provider).HasColumnName("provider").IsRequired();
                e.Property(u => u.ProviderId).HasColumnName("provider_id").IsRequired();
                e.Property(u => u.Email).HasColumnName("email");
                e.Property(u => u.DisplayName).HasColumnName("display_name");
                e.Property(u => u.CreatedAt).HasColumnName("created_at")
                 .HasDefaultValueSql("datetime('now')");
                e.Property(u => u.IsAdmin).HasColumnName("is_admin").HasDefaultValue(false);
                e.Property(u => u.Role).HasColumnName("role").HasDefaultValue("user");
                e.Property(u => u.OrgId).HasColumnName("org_id");
                e.Property(u => u.OrgRole).HasColumnName("org_role").HasDefaultValue("owner");
                e.Property(u => u.PasswordHash).HasColumnName("password_hash");
                e.Property(u => u.PasswordResetToken).HasColumnName("password_reset_token");
                e.Property(u => u.PasswordResetExpiresAt).HasColumnName("password_reset_expires_at");
                e.Property(u => u.Phone).HasColumnName("phone");
                e.Property(u => u.NotificationEmail).HasColumnName("notification_email");

                e.HasIndex(u => new { u.Provider, u.ProviderId }).IsUnique();
                e.HasIndex(u => u.OrgId);

                e.HasOne(u => u.Org)
                 .WithMany(o => o.Members)
                 .HasForeignKey(u => u.OrgId)
                 .OnDelete(DeleteBehavior.SetNull);
            });

            // ── Lead ─────────────────────────────────────────────────────
            m.Entity<Lead>(e =>
            {
                e.ToTable("leads");
                e.HasKey(l => l.Id);
                e.Property(l => l.Id).HasColumnName("id");
                e.Property(l => l.Address).HasColumnName("address").IsRequired();
                e.Property(l => l.Lat).HasColumnName("lat");
                e.Property(l => l.Lng).HasColumnName("lng");
                e.Property(l => l.YearBuilt).HasColumnName("year_built");
                e.Property(l => l.PropertyType).HasColumnName("property_type");
                e.Property(l => l.SourceAddress).HasColumnName("source_address");
                e.Property(l => l.SavedAt).HasColumnName("saved_at")
                 .HasDefaultValueSql("datetime('now')");
                e.Property(l => l.Notes).HasColumnName("notes");
                e.Property(l => l.OwnerName).HasColumnName("owner_name");
                e.Property(l => l.OwnerPhone).HasColumnName("owner_phone");
                e.Property(l => l.OwnerEmail).HasColumnName("owner_email");
                e.Property(l => l.UserId).HasColumnName("user_id");
                e.Property(l => l.OrgId).HasColumnName("org_id");
                e.Property(l => l.AssignedToUserId).HasColumnName("assigned_to_user_id");
                e.Property(l => l.IsEnriched).HasColumnName("is_enriched").HasDefaultValue(false);
                e.Property(l => l.DeletedAt).HasColumnName("deleted_at");
                e.Property(l => l.Status).HasColumnName("status").HasDefaultValue("new");

                e.HasIndex(l => new { l.OrgId, l.Address }).IsUnique();

                e.HasOne(l => l.User)
                 .WithMany(u => u.Leads)
                 .HasForeignKey(l => l.UserId)
                 .OnDelete(DeleteBehavior.SetNull);
            });

            // ── LeadContact ──────────────────────────────────────────────
            m.Entity<LeadContact>(e =>
            {
                e.ToTable("lead_contacts");
                e.HasKey(c => c.Id);
                e.Property(c => c.Id).HasColumnName("id");
                e.Property(c => c.LeadId).HasColumnName("lead_id");
                e.Property(c => c.Name).HasColumnName("name");
                e.Property(c => c.Phone).HasColumnName("phone");
                e.Property(c => c.Email).HasColumnName("email");
                e.Property(c => c.PhoneType).HasColumnName("phone_type");
                e.Property(c => c.IsDnc).HasColumnName("is_dnc").HasDefaultValue(false);
                e.Property(c => c.IsLitigator).HasColumnName("is_litigator").HasDefaultValue(false);
                e.Property(c => c.PhoneScore).HasColumnName("phone_score");
                e.Property(c => c.PhoneTested).HasColumnName("phone_tested");
                e.Property(c => c.PhoneReachable).HasColumnName("phone_reachable");
                e.Property(c => c.ContactType).HasColumnName("contact_type").HasDefaultValue("owner");
                e.Property(c => c.IsPrimary).HasColumnName("is_primary").HasDefaultValue(false);
                e.Property(c => c.Source).HasColumnName("source").HasDefaultValue("batchdata");
                e.Property(c => c.CreatedAt).HasColumnName("created_at")
                 .HasDefaultValueSql("datetime('now')");

                e.HasIndex(c => c.LeadId);

                e.HasOne(c => c.Lead)
                 .WithMany(l => l.Contacts)
                 .HasForeignKey(c => c.LeadId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── Email templates / sends / opt-outs ───────────────────────
            m.Entity<EmailTemplate>(e =>
            {
                e.ToTable("email_templates");
                e.HasKey(t => t.Id);
                e.Property(t => t.Id).HasColumnName("id");
                e.Property(t => t.OrgId).HasColumnName("org_id");
                e.Property(t => t.Name).HasColumnName("name").IsRequired();
                e.Property(t => t.Subject).HasColumnName("subject").IsRequired();
                e.Property(t => t.Body).HasColumnName("body").IsRequired();
                e.Property(t => t.Branded).HasColumnName("branded");
                e.Property(t => t.IsHtml).HasColumnName("is_html");
                e.Property(t => t.Kind).HasColumnName("kind");
                e.Property(t => t.CreatedAt).HasColumnName("created_at");
                e.Property(t => t.UpdatedAt).HasColumnName("updated_at");
                e.HasIndex(t => t.OrgId);
            });
            m.Entity<EmailSend>(e =>
            {
                e.ToTable("email_sends");
                e.HasKey(t => t.Id);
                e.Property(t => t.Id).HasColumnName("id");
                e.Property(t => t.OrgId).HasColumnName("org_id");
                e.Property(t => t.LeadId).HasColumnName("lead_id");
                e.Property(t => t.TemplateId).HasColumnName("template_id");
                e.Property(t => t.UserId).HasColumnName("user_id");
                e.Property(t => t.ToEmail).HasColumnName("to_email").IsRequired();
                e.Property(t => t.Subject).HasColumnName("subject").IsRequired();
                e.Property(t => t.Status).HasColumnName("status").IsRequired();
                e.Property(t => t.Error).HasColumnName("error");
                e.Property(t => t.SentAt).HasColumnName("sent_at");
                e.HasIndex(t => t.LeadId);
                e.HasIndex(t => t.OrgId);
            });
            m.Entity<EmailOptOut>(e =>
            {
                e.ToTable("email_opt_outs");
                e.HasKey(t => t.Id);
                e.Property(t => t.Id).HasColumnName("id");
                e.Property(t => t.OrgId).HasColumnName("org_id");
                e.Property(t => t.Email).HasColumnName("email").IsRequired();
                e.Property(t => t.CreatedAt).HasColumnName("created_at");
                e.HasIndex(t => new { t.OrgId, t.Email }).IsUnique();
            });

            // ── AppSetting ───────────────────────────────────────────────
            m.Entity<AppSetting>(e =>
            {
                e.ToTable("app_settings");
                e.HasKey(s => s.Key);
                e.Property(s => s.Key).HasColumnName("key");
                e.Property(s => s.Value).HasColumnName("value");
                e.Property(s => s.UpdatedAt).HasColumnName("updated_at");
            });

            // ── Enrichment ───────────────────────────────────────────────
            m.Entity<Enrichment>(e =>
            {
                e.ToTable("enrichments");
                e.HasKey(en => en.Id);
                e.Property(en => en.Id).HasColumnName("id");
                e.Property(en => en.UserId).HasColumnName("user_id");
                e.Property(en => en.LeadId).HasColumnName("lead_id");
                e.Property(en => en.Address).HasColumnName("address");
                e.Property(en => en.Status).HasColumnName("status").HasDefaultValue("pending");
                e.Property(en => en.Provider).HasColumnName("provider").HasDefaultValue("batchskiptracing");
                // No HasDefaultValue here: EF would then skip writing 0 (the CLR
                // default) and the column's DB default would turn a no-match into a
                // billed match. CreditsUsed is always set explicitly.
                e.Property(en => en.CreditsUsed).HasColumnName("credits_used");
                e.Property(en => en.CreatedAt).HasColumnName("created_at")
                 .HasDefaultValueSql("datetime('now')");

                e.HasIndex(en => en.UserId);
                e.HasIndex(en => en.CreatedAt);

                e.HasOne(en => en.User)
                 .WithMany(u => u.Enrichments)
                 .HasForeignKey(en => en.UserId)
                 .OnDelete(DeleteBehavior.SetNull);

                e.HasOne(en => en.Lead)
                 .WithMany(l => l.Enrichments)
                 .HasForeignKey(en => en.LeadId)
                 .OnDelete(DeleteBehavior.SetNull);
            });
        }
    }
}
