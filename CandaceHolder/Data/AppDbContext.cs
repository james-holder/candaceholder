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
        public DbSet<WatchedArea>  WatchedAreas => Set<WatchedArea>();
        public DbSet<SentAlert>    SentAlerts   => Set<SentAlert>();
        public DbSet<OrgCredit>            OrgCredits            => Set<OrgCredit>();
        public DbSet<OrgCreditTransaction> OrgCreditTransactions => Set<OrgCreditTransaction>();
        public DbSet<ReportCreditGrant>    ReportCreditGrants    => Set<ReportCreditGrant>();
        public DbSet<StripeWebhookEvent>   StripeWebhookEvents   => Set<StripeWebhookEvent>();

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder m)
        {
            // ── Org ──────────────────────────────────────────────────────
            m.Entity<Org>(e =>
            {
                e.ToTable("orgs");
                e.HasKey(o => o.Id);
                e.Property(o => o.Id).HasColumnName("id");
                e.Property(o => o.Name).HasColumnName("name").IsRequired();
                e.Property(o => o.OwnerId).HasColumnName("owner_id");
                e.Property(o => o.Plan).HasColumnName("plan").HasDefaultValue("free");
                e.Property(o => o.CreatedAt).HasColumnName("created_at")
                 .HasDefaultValueSql("datetime('now')");
                e.Property(o => o.TrialEndsAt).HasColumnName("trial_ends_at");
                e.Property(o => o.StripeCustomerId).HasColumnName("stripe_customer_id");
                e.Property(o => o.StripeSubscriptionId).HasColumnName("stripe_subscription_id");
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
                e.Property(l => l.RiskLevel).HasColumnName("risk_level");
                e.Property(l => l.LastStormDate).HasColumnName("last_storm_date");
                e.Property(l => l.HailSize).HasColumnName("hail_size");
                e.Property(l => l.EstimatedDamage).HasColumnName("estimated_damage");
                e.Property(l => l.RoofAge).HasColumnName("roof_age");
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
                e.HasIndex(l => l.RiskLevel);

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
                e.Property(c => c.ContactType).HasColumnName("contact_type").HasDefaultValue("owner");
                e.Property(c => c.IsPrimary).HasColumnName("is_primary").HasDefaultValue(false);
                e.Property(c => c.Source).HasColumnName("source").HasDefaultValue("whitepages");
                e.Property(c => c.CreatedAt).HasColumnName("created_at")
                 .HasDefaultValueSql("datetime('now')");

                e.HasIndex(c => c.LeadId);

                e.HasOne(c => c.Lead)
                 .WithMany(l => l.Contacts)
                 .HasForeignKey(c => c.LeadId)
                 .OnDelete(DeleteBehavior.Cascade);
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
                e.Property(en => en.CreditsUsed).HasColumnName("credits_used").HasDefaultValue(1);
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

            // ── WatchedArea ──────────────────────────────────────────────
            m.Entity<WatchedArea>(e =>
            {
                e.ToTable("watched_areas");
                e.HasKey(w => w.Id);
                e.Property(w => w.Id).HasColumnName("id");
                e.Property(w => w.UserId).HasColumnName("user_id");
                e.Property(w => w.OrgId).HasColumnName("org_id");
                e.Property(w => w.Label).HasColumnName("label").IsRequired();
                e.Property(w => w.CenterLat).HasColumnName("center_lat");
                e.Property(w => w.CenterLng).HasColumnName("center_lng");
                e.Property(w => w.RadiusMiles).HasColumnName("radius_miles").HasDefaultValue(10.0);
                e.Property(w => w.MinHailSizeInches).HasColumnName("min_hail_size_inches").HasDefaultValue(1.0);
                e.Property(w => w.AlertsEnabled).HasColumnName("alerts_enabled").HasDefaultValue(true);
                e.Property(w => w.CreatedAt).HasColumnName("created_at")
                 .HasDefaultValueSql("datetime('now')");

                e.HasIndex(w => w.UserId);

                e.HasOne(w => w.User)
                 .WithMany(u => u.WatchedAreas)
                 .HasForeignKey(w => w.UserId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── OrgCredit ─────────────────────────────────────────────────
            m.Entity<OrgCredit>(e =>
            {
                e.ToTable("org_credits");
                e.HasKey(c => c.Id);
                e.Property(c => c.Id).HasColumnName("id");
                e.Property(c => c.OrgId).HasColumnName("org_id");
                e.Property(c => c.CreditType).HasColumnName("credit_type").IsRequired();
                e.Property(c => c.Balance).HasColumnName("balance").HasDefaultValue(0);
                e.Property(c => c.UsedThisPeriod).HasColumnName("used_this_period").HasDefaultValue(0);
                e.Property(c => c.PeriodStart).HasColumnName("period_start")
                 .HasDefaultValueSql("datetime('now')");
                e.Property(c => c.PeriodEnd).HasColumnName("period_end");
                e.Property(c => c.UpdatedAt).HasColumnName("updated_at")
                 .HasDefaultValueSql("datetime('now')");

                e.HasIndex(c => new { c.OrgId, c.CreditType }).IsUnique();

                e.HasOne(c => c.Org)
                 .WithMany()
                 .HasForeignKey(c => c.OrgId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── OrgCreditTransaction ──────────────────────────────────────
            m.Entity<OrgCreditTransaction>(e =>
            {
                e.ToTable("org_credit_transactions");
                e.HasKey(t => t.Id);
                e.Property(t => t.Id).HasColumnName("id");
                e.Property(t => t.OrgId).HasColumnName("org_id");
                e.Property(t => t.UserId).HasColumnName("user_id");
                e.Property(t => t.CreditType).HasColumnName("credit_type").IsRequired();
                e.Property(t => t.Amount).HasColumnName("amount");
                e.Property(t => t.BalanceAfter).HasColumnName("balance_after");
                e.Property(t => t.Description).HasColumnName("description");
                e.Property(t => t.ReferenceId).HasColumnName("reference_id");
                e.Property(t => t.ReferenceType).HasColumnName("reference_type");
                e.Property(t => t.CreatedAt).HasColumnName("created_at")
                 .HasDefaultValueSql("datetime('now')");

                e.HasIndex(t => t.OrgId);
                e.HasIndex(t => t.UserId);
                e.HasIndex(t => t.CreatedAt);

                e.HasOne(t => t.Org)
                 .WithMany()
                 .HasForeignKey(t => t.OrgId)
                 .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(t => t.User)
                 .WithMany()
                 .HasForeignKey(t => t.UserId)
                 .OnDelete(DeleteBehavior.SetNull);
            });

            // ── ReportCreditGrant ────────────────────────────────────────
            m.Entity<ReportCreditGrant>(e =>
            {
                e.ToTable("report_credit_grants");
                e.HasKey(g => g.Id);
                e.Property(g => g.Id).HasColumnName("id");
                e.Property(g => g.OrgId).HasColumnName("org_id");
                e.Property(g => g.Source).HasColumnName("source").IsRequired();
                e.Property(g => g.Amount).HasColumnName("amount");
                e.Property(g => g.Remaining).HasColumnName("remaining");
                e.Property(g => g.ExpiresAt).HasColumnName("expires_at");
                e.Property(g => g.CreatedByUserId).HasColumnName("created_by_user_id");
                e.Property(g => g.Description).HasColumnName("description").HasDefaultValue("");
                e.Property(g => g.GrantedAt).HasColumnName("granted_at")
                 .HasDefaultValueSql("datetime('now')");

                e.HasIndex(g => g.OrgId);
                e.HasIndex(g => g.ExpiresAt);

                e.HasOne(g => g.Org)
                 .WithMany()
                 .HasForeignKey(g => g.OrgId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── StripeWebhookEvent ───────────────────────────────────────
            m.Entity<StripeWebhookEvent>(e =>
            {
                e.ToTable("stripe_webhook_events");
                e.HasKey(x => x.EventId);
                e.Property(x => x.EventId).HasColumnName("event_id");
                e.Property(x => x.EventType).HasColumnName("event_type").IsRequired();
                e.Property(x => x.ProcessedAt).HasColumnName("processed_at")
                 .HasDefaultValueSql("datetime('now')");
            });

            // ── SentAlert ────────────────────────────────────────────────
            m.Entity<SentAlert>(e =>
            {
                e.ToTable("sent_alerts");
                e.HasKey(s => s.Id);
                e.Property(s => s.Id).HasColumnName("id");
                e.Property(s => s.UserId).HasColumnName("user_id");
                e.Property(s => s.OrgId).HasColumnName("org_id");
                e.Property(s => s.WatchedAreaId).HasColumnName("watched_area_id");
                e.Property(s => s.EventDate).HasColumnName("event_date");
                e.Property(s => s.HailSizeInches).HasColumnName("hail_size_inches");
                e.Property(s => s.SentAt).HasColumnName("sent_at")
                 .HasDefaultValueSql("datetime('now')");

                e.HasIndex(s => new { s.WatchedAreaId, s.EventDate }).IsUnique();
                e.HasIndex(s => s.UserId);

                e.HasOne(s => s.User)
                 .WithMany(u => u.SentAlerts)
                 .HasForeignKey(s => s.UserId)
                 .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(s => s.WatchedArea)
                 .WithMany(w => w.SentAlerts)
                 .HasForeignKey(s => s.WatchedAreaId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

        }
    }
}
