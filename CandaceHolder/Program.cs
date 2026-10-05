using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Infrastructure;
using CandaceHolder.Data;
using CandaceHolder.Data.Models;
using CandaceHolder.Filters;
using CandaceHolder.Services;

QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);
var config  = builder.Configuration;

// Stripe.net reads this static key by default for every Stripe.* service
// class instantiated with no explicit client. Blank until Stripe:SecretKey
// is filled in — BillingController checks for that before making any Stripe
// call, so a blank key here just means billing endpoints return a friendly
// "not set up yet" error instead of an auth failure.
Stripe.StripeConfiguration.ApiKey = config["Stripe:SecretKey"];

// NOTE: embedded Checkout's ui_mode=embedded_page requires Stripe API version
// 2026-03-25.dahlia or later. Stripe.net has no public way to override the
// API version per-call or globally (RequestOptions.StripeVersion and
// StripeConfiguration.ApiVersion's setter are both internal-only in this
// SDK) — so the fix is using an SDK version that already defaults to Dahlia.
// Stripe.net 51.0.1 pins 2026-03-25.dahlia out of the box; see .csproj.

// ── Data Protection (persist keys so auth cookies survive redeploys) ──────
// NOTE: this and the DB path below use ContentRootPath (the project folder),
// NOT AppContext.BaseDirectory (the bin/Debug/net8.0 build output folder).
// They used to point at BaseDirectory, which meant the real SQLite DB and
// auth keys lived inside a folder Visual Studio treats as disposable build
// output — a bin/obj clean silently wiped real user data. Folder is named
// "App_Data" (not "data") because Windows filesystems are case-insensitive:
// "data" collides with the existing Data/ source folder (AppDbContext.cs,
// Models/) at this same project-root level, which merges runtime binary
// files into source control territory. App_Data is also the ASP.NET
// convention IIS won't serve as static content.
var keysDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "dp-keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir));

// ── MVC ───────────────────────────────────────────────────────────────────
builder.Services.AddControllersWithViews(o =>
{
    // Hard paywall once an org's trial has expired (see Filters/TrialGateFilter.cs)
    o.Filters.Add<TrialGateFilter>();
});
builder.Services.AddMemoryCache();

// ── Database (EF Core + SQLite) ───────────────────────────────────────────
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "leads.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// ── Authentication ────────────────────────────────────────────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, opt =>
    {
        opt.LoginPath         = "/Auth/Login";
        opt.LogoutPath        = "/Auth/Logout";
        opt.AccessDeniedPath  = "/Auth/Login";
        opt.ExpireTimeSpan    = TimeSpan.FromDays(30);
        opt.SlidingExpiration = true;
        opt.Cookie.Name       = ".StormLead.Session";
        opt.Cookie.HttpOnly   = true;
        opt.Cookie.SameSite   = SameSiteMode.Lax;
    })
    .AddCookie("External", opt =>
    {
        opt.Cookie.Name    = ".StormLead.External";
        opt.ExpireTimeSpan = TimeSpan.FromMinutes(10);
    });

if (!string.IsNullOrWhiteSpace(config["Auth:Google:ClientId"]))
{
    builder.Services.AddAuthentication()
        .AddGoogle("Google", opt =>
        {
            opt.SignInScheme = "External";
            opt.ClientId     = config["Auth:Google:ClientId"]!;
            opt.ClientSecret = config["Auth:Google:ClientSecret"]!;
        });
}

if (!string.IsNullOrWhiteSpace(config["Auth:Microsoft:ClientId"]))
{
    builder.Services.AddAuthentication()
        .AddMicrosoftAccount("Microsoft", opt =>
        {
            opt.SignInScheme = "External";
            opt.ClientId     = config["Auth:Microsoft:ClientId"]!;
            opt.ClientSecret = config["Auth:Microsoft:ClientSecret"]!;
        });
}

// ── HttpClient factory (RealDataService) ─────────────────────────────────
builder.Services.AddHttpClient("overpass", c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
});
builder.Services.AddHttpClient("noaa", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
});
builder.Services.AddHttpClient("regrid", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient("mesonet", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
});
builder.Services.AddHttpClient("bst", c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
});
builder.Services.AddHttpClient("whitepages", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
});
builder.Services.AddHttpClient("tomorrow", c =>
{
    c.BaseAddress = new Uri("https://api.tomorrow.io/");
    c.Timeout     = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
});
builder.Services.AddHttpClient("mesh", c =>
{
    // MRMS MESH grib2.gz downloads (NOAA AWS / IEM MTArchive) can run several
    // MB per CONUS-wide daily file; give this more headroom than the other
    // (small JSON response) clients.
    c.Timeout = TimeSpan.FromSeconds(120);
    c.DefaultRequestHeaders.Add("User-Agent", "StormLeadPro/1.0");
});

// ── Services ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<RealDataService>();
builder.Services.AddSingleton<MeshSwathService>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddSingleton<HailReportService>();
builder.Services.AddScoped<ReportCreditService>();
builder.Services.AddScoped<StripeService>();
builder.Services.AddHostedService<StormAlertService>();

// ── Pipeline ──────────────────────────────────────────────────────────────
var app = builder.Build();

// Initialise / migrate the database at startup
using (var scope = app.Services.CreateScope())
{
    var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var conn = db.Database.GetDbConnection();
    conn.Open();

    db.Database.EnsureCreated();

    // ── Helper: add a column only if it doesn't already exist ────────────
    void AddColumnIfMissing(string table, string column, string definition)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                return; // column already present
        reader.Close();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        cmd.ExecuteNonQuery();
    }

    // ── Helper: create a table only if it doesn't already exist ──────────
    bool TableExists(string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@t";
        var p = cmd.CreateParameter(); p.ParameterName = "@t"; p.Value = table;
        cmd.Parameters.Add(p);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    // ── Schema patches ────────────────────────────────────────────────────
    AddColumnIfMissing("users", "is_admin",            "INTEGER NOT NULL DEFAULT 0");
    AddColumnIfMissing("users", "role",                "TEXT NOT NULL DEFAULT 'user'");
    AddColumnIfMissing("users", "org_id",              "INTEGER REFERENCES orgs(id) ON DELETE SET NULL");
    AddColumnIfMissing("users", "org_role",            "TEXT NOT NULL DEFAULT 'owner'");
    AddColumnIfMissing("users", "password_hash",       "TEXT");
    AddColumnIfMissing("users", "password_reset_token",      "TEXT");
    AddColumnIfMissing("users", "password_reset_expires_at", "TEXT");
    AddColumnIfMissing("users", "phone",               "TEXT");
    AddColumnIfMissing("users", "notification_email",  "TEXT");
    AddColumnIfMissing("leads", "year_built",          "INTEGER");
    AddColumnIfMissing("leads", "owner_name",          "TEXT");
    AddColumnIfMissing("leads", "owner_phone",         "TEXT");
    AddColumnIfMissing("leads", "owner_email",         "TEXT");
    AddColumnIfMissing("leads", "property_type",       "TEXT");
    AddColumnIfMissing("leads", "source_address",      "TEXT");
    AddColumnIfMissing("leads", "notes",               "TEXT");
    AddColumnIfMissing("leads", "is_enriched",         "INTEGER NOT NULL DEFAULT 0");
    AddColumnIfMissing("leads", "deleted_at",          "TEXT");
    AddColumnIfMissing("leads", "status",              "TEXT NOT NULL DEFAULT 'new'");
    AddColumnIfMissing("leads", "org_id",              "INTEGER REFERENCES orgs(id) ON DELETE SET NULL");
    AddColumnIfMissing("leads", "assigned_to_user_id", "INTEGER REFERENCES users(id) ON DELETE SET NULL");

    // ── Migrate leads.address unique index → (org_id, address) ──────────
    // The old global unique index prevents two orgs from saving the same address.
    // We drop it and replace with a composite (org_id, address) unique index.
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_leads_Address'";
        var hasOldIndex = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        if (hasOldIndex)
        {
            cmd.CommandText = "DROP INDEX IF EXISTS IX_leads_Address";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_leads_org_address ON leads(org_id, address)";
            cmd.ExecuteNonQuery();
        }
        // Also ensure the new composite index exists even if old one was already gone
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_leads_org_address'";
        var hasNewIndex = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        if (!hasNewIndex)
        {
            cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_leads_org_address ON leads(org_id, address)";
            cmd.ExecuteNonQuery();
        }
    }
    AddColumnIfMissing("watched_areas", "org_id",      "INTEGER REFERENCES orgs(id) ON DELETE SET NULL");
    AddColumnIfMissing("sent_alerts",   "org_id",      "INTEGER REFERENCES orgs(id) ON DELETE SET NULL");

    // ── Org branding columns ──────────────────────────────────────────────
    AddColumnIfMissing("orgs", "company_name",   "TEXT");
    AddColumnIfMissing("orgs", "company_email",  "TEXT");
    AddColumnIfMissing("orgs", "phone",          "TEXT");
    AddColumnIfMissing("orgs", "website",        "TEXT");
    AddColumnIfMissing("orgs", "accent_color",   "TEXT");
    AddColumnIfMissing("orgs", "header_color",   "TEXT");
    AddColumnIfMissing("orgs", "tagline",        "TEXT");
    AddColumnIfMissing("orgs", "license_number", "TEXT");
    AddColumnIfMissing("orgs", "logo_path",      "TEXT");
    AddColumnIfMissing("orgs", "trial_ends_at",  "TEXT");
    AddColumnIfMissing("orgs", "stripe_customer_id",     "TEXT");
    AddColumnIfMissing("orgs", "stripe_subscription_id", "TEXT");

    // ── Org additional info (address & social links) ───────────────────────
    AddColumnIfMissing("orgs", "address",             "TEXT");
    AddColumnIfMissing("orgs", "facebook_url",        "TEXT");
    AddColumnIfMissing("orgs", "instagram_url",       "TEXT");
    AddColumnIfMissing("orgs", "google_business_url", "TEXT");

    if (!TableExists("orgs"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE orgs (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                name       TEXT NOT NULL,
                owner_id   INTEGER REFERENCES users(id) ON DELETE SET NULL,
                plan       TEXT NOT NULL DEFAULT 'free',
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_orgs_owner_id ON orgs(owner_id)";
        cmd.ExecuteNonQuery();
    }

    if (!TableExists("org_invites"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE org_invites (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                org_id      INTEGER NOT NULL REFERENCES orgs(id) ON DELETE CASCADE,
                email       TEXT NOT NULL,
                token       TEXT NOT NULL UNIQUE,
                role        TEXT NOT NULL DEFAULT 'rep',
                expires_at  TEXT NOT NULL,
                accepted_at TEXT,
                created_at  TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE UNIQUE INDEX ix_org_invites_token  ON org_invites(token)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX        ix_org_invites_org_id ON org_invites(org_id)";
        cmd.ExecuteNonQuery();
    }

    if (!TableExists("enrichments"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE enrichments (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id       INTEGER REFERENCES users(id) ON DELETE SET NULL,
                lead_id       INTEGER REFERENCES leads(id) ON DELETE SET NULL,
                address       TEXT,
                status        TEXT NOT NULL DEFAULT 'pending',
                provider      TEXT NOT NULL DEFAULT 'batchskiptracing',
                credits_used  INTEGER NOT NULL DEFAULT 1,
                created_at    TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = "CREATE INDEX ix_enrichments_user_id    ON enrichments(user_id)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_enrichments_created_at ON enrichments(created_at)";
        cmd.ExecuteNonQuery();
    }

    if (!TableExists("lead_contacts"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE lead_contacts (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                lead_id      INTEGER NOT NULL REFERENCES leads(id) ON DELETE CASCADE,
                name         TEXT,
                phone        TEXT,
                email        TEXT,
                contact_type TEXT NOT NULL DEFAULT 'owner',
                is_primary   INTEGER NOT NULL DEFAULT 0,
                source       TEXT NOT NULL DEFAULT 'whitepages',
                created_at   TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_lead_contacts_lead_id ON lead_contacts(lead_id)";
        cmd.ExecuteNonQuery();
    }

    if (!TableExists("watched_areas"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE watched_areas (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id               INTEGER REFERENCES users(id) ON DELETE CASCADE,
                label                 TEXT NOT NULL,
                center_lat            REAL NOT NULL,
                center_lng            REAL NOT NULL,
                radius_miles          REAL NOT NULL DEFAULT 10.0,
                min_hail_size_inches  REAL NOT NULL DEFAULT 1.0,
                alerts_enabled        INTEGER NOT NULL DEFAULT 1,
                created_at            TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_watched_areas_user_id ON watched_areas(user_id)";
        cmd.ExecuteNonQuery();
    }

    if (!TableExists("sent_alerts"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE sent_alerts (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id           INTEGER REFERENCES users(id) ON DELETE CASCADE,
                watched_area_id   INTEGER NOT NULL REFERENCES watched_areas(id) ON DELETE CASCADE,
                event_date        TEXT NOT NULL,
                hail_size_inches  REAL NOT NULL,
                sent_at           TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE UNIQUE INDEX ix_sent_alerts_area_date ON sent_alerts(watched_area_id, event_date)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_sent_alerts_user_id ON sent_alerts(user_id)";
        cmd.ExecuteNonQuery();
    }

    if (!TableExists("org_credits"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE org_credits (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                org_id           INTEGER NOT NULL REFERENCES orgs(id) ON DELETE CASCADE,
                credit_type      TEXT NOT NULL,
                balance          INTEGER NOT NULL DEFAULT 0,
                used_this_period INTEGER NOT NULL DEFAULT 0,
                period_start     TEXT NOT NULL DEFAULT (datetime('now')),
                period_end       TEXT,
                updated_at       TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE UNIQUE INDEX ix_org_credits_org_type ON org_credits(org_id, credit_type)";
        cmd.ExecuteNonQuery();
    }

    if (!TableExists("org_credit_transactions"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE org_credit_transactions (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                org_id         INTEGER NOT NULL REFERENCES orgs(id) ON DELETE CASCADE,
                user_id        INTEGER REFERENCES users(id) ON DELETE SET NULL,
                credit_type    TEXT NOT NULL,
                amount         INTEGER NOT NULL,
                balance_after  INTEGER NOT NULL,
                description    TEXT NOT NULL DEFAULT '',
                reference_id   TEXT,
                reference_type TEXT,
                created_at     TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_org_credit_tx_org_id     ON org_credit_transactions(org_id)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_org_credit_tx_user_id    ON org_credit_transactions(user_id)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_org_credit_tx_created_at ON org_credit_transactions(created_at)";
        cmd.ExecuteNonQuery();
    }

    if (!TableExists("report_credit_grants"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE report_credit_grants (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                org_id              INTEGER NOT NULL REFERENCES orgs(id) ON DELETE CASCADE,
                source              TEXT NOT NULL,
                amount              INTEGER NOT NULL,
                remaining           INTEGER NOT NULL,
                expires_at          TEXT,
                created_by_user_id  INTEGER REFERENCES users(id) ON DELETE SET NULL,
                description         TEXT NOT NULL DEFAULT '',
                granted_at          TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_report_credit_grants_org_id     ON report_credit_grants(org_id)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX ix_report_credit_grants_expires_at ON report_credit_grants(expires_at)";
        cmd.ExecuteNonQuery();
    }

    // Stripe can (and does) redeliver the same webhook event more than once
    // (retries on timeout, manual resends from the dashboard, etc.). Since a
    // webhook directly grants report credits, processing the same event
    // twice would double-grant. This table is just a "have we seen this
    // Stripe event ID before" check — see BillingController.Webhook.
    if (!TableExists("stripe_webhook_events"))
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE stripe_webhook_events (
                event_id     TEXT PRIMARY KEY,
                event_type   TEXT NOT NULL,
                processed_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """;
        cmd.ExecuteNonQuery();
    }

    // ── Dev-only seed login ───────────────────────────────────────────────
    // Gives you a ready-to-use account at /Auth/Login on localhost without
    // going through /Auth/Register. Only runs in Development, and only the
    // first time (skipped once the account exists). The seed email matches
    // AdminEmail in appsettings.json, so this account also unlocks /Admin.
    if (app.Environment.IsDevelopment())
    {
        const string seedEmail    = "jaholder78@gmail.com";
        const string seedPassword = "LocalDev123!";

        var seedExists = await db.Users.AnyAsync(
            u => u.Provider == "password" && u.ProviderId == seedEmail);

        if (!seedExists)
        {
            var seedOrg = new CandaceHolder.Data.Models.Org
            {
                Name        = "StormLead Demo Co.",
                CompanyName = "StormLead Demo Co.",
                Plan        = "pro",
                TrialEndsAt = null, // null = never gated by TrialGateFilter
                CreatedAt   = DateTime.UtcNow
            };
            db.Orgs.Add(seedOrg);
            await db.SaveChangesAsync(); // get seedOrg.Id

            var seedUser = new User
            {
                Provider    = "password",
                ProviderId  = seedEmail,
                Email       = seedEmail,
                DisplayName = "James",
                OrgId       = seedOrg.Id,
                OrgRole     = "owner",
                IsAdmin     = true,
                CreatedAt   = DateTime.UtcNow
            };
            seedUser.PasswordHash = new PasswordHasher<User>().HashPassword(seedUser, seedPassword);
            db.Users.Add(seedUser);
            await db.SaveChangesAsync(); // get seedUser.Id

            seedOrg.OwnerId = seedUser.Id;

            db.OrgCredits.Add(new OrgCredit
            {
                OrgId       = seedOrg.Id,
                CreditType  = "enrichment",
                Balance     = 100,
                PeriodStart = DateTime.UtcNow,
                UpdatedAt   = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            Console.WriteLine("──────────────────────────────────────────────────");
            Console.WriteLine(" Seeded local dev login (Development only):");
            Console.WriteLine($"   Email:    {seedEmail}");
            Console.WriteLine($"   Password: {seedPassword}");
            Console.WriteLine("   Sign in:  /Auth/Login");
            Console.WriteLine("──────────────────────────────────────────────────");
        }
    }

    conn.Close();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

if (app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name:    "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
