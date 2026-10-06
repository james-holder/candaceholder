using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using CandaceHolder.Data.Models;
using CandaceHolder.Services;

var builder = WebApplication.CreateBuilder(args);
var config  = builder.Configuration;

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
builder.Services.AddControllersWithViews();
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
        opt.Cookie.Name       = ".CandaceHolder.Session";
        opt.Cookie.HttpOnly   = true;
        opt.Cookie.SameSite   = SameSiteMode.Lax;
    })
    .AddCookie("External", opt =>
    {
        opt.Cookie.Name    = ".CandaceHolder.External";
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
    c.DefaultRequestHeaders.Add("User-Agent", "CandaceHolder/1.0");
});
builder.Services.AddHttpClient("regrid", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient("batchdata", c =>
{
    // A full 100-address batch can take a while on BatchData's side.
    c.Timeout = TimeSpan.FromSeconds(120);
    c.DefaultRequestHeaders.Add("User-Agent", "CandaceHolder/1.0");
});
builder.Services.AddHttpClient("whitepages", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.Add("User-Agent", "CandaceHolder/1.0");
});

// ── Services ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<RealDataService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<EmailService>();

// ── Pipeline ──────────────────────────────────────────────────────────────
var app = builder.Build();

// Initialise the database at startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // EnsureCreated builds every table from AppDbContext's model on a fresh
    // database, but never alters an existing one — so columns added after the
    // first deploy are patched in below.
    db.Database.EnsureCreated();

    var conn = db.Database.GetDbConnection();
    conn.Open();
    void AddColumnIfMissing(string table, string column, string definition)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return;
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        cmd.ExecuteNonQuery();
    }

    // 2026-10-06: BatchData skip tracing — phone type + Do Not Call / litigator flags
    AddColumnIfMissing("lead_contacts", "phone_type",   "TEXT");
    AddColumnIfMissing("lead_contacts", "is_dnc",       "INTEGER NOT NULL DEFAULT 0");
    AddColumnIfMissing("lead_contacts", "is_litigator", "INTEGER NOT NULL DEFAULT 0");

    // 2026-10-06: settings editable from Admin (Email settings)
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS app_settings (
                key        TEXT NOT NULL PRIMARY KEY,
                value      TEXT,
                updated_at TEXT NOT NULL
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
                Name        = "Candace Holder",
                CompanyName = "Candace Holder",
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
