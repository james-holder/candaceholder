# StormLead Pro

A web-based lead generation tool for roofing contractors. The core idea: after a hail storm, every house in the affected area is a potential customer. StormLead Pro lets a contractor search any address, see a risk-scored map of nearby properties using real NOAA storm data, save the best leads, and eventually pull owner contact info — all in one place.

---

## What It Does

A contractor types in any address (their own neighborhood, a zip code they want to work, wherever). The app geocodes that address, queries OpenStreetMap for real nearby residential addresses, and cross-references them against two years of real NOAA hail event history. Every property gets a risk score:

- **High** — hail ≥ 1.5" detected within 1 mile
- **Medium** — hail ≥ 0.75" detected within 1.5 miles
- **Low** — no significant hail nearby

Properties are displayed on an interactive Google Map with color-coded pins and a sortable table below. The contractor can select promising leads and save them to the database with one click. From the Saved Leads page they can manage their pipeline, add notes, look up owner contact info, and export to CSV for outreach.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Framework | ASP.NET Core 8 MVC |
| Database | SQLite via Entity Framework Core |
| Frontend | Tailwind CSS (CDN), vanilla JS, Font Awesome |
| Map | Google Maps JavaScript API |
| Auth | Cookie-based with Google and Microsoft OAuth |

---

## External Data Sources

### Free / No Key Required
- **OpenStreetMap Overpass API** — pulls real residential addresses within the search radius. Falls back to simulated addresses when OSM coverage is sparse (rural areas).
- **NOAA SWDI (Severe Weather Data Inventory)** — queries the `nx3hail` dataset for real hail events over the past two years. Split into two requests to work within NOAA's 1-year-per-query limit.

### Configured in `appsettings.json`
- **Google Maps** — geocoding and map rendering. API key is currently hardcoded in `RoofHealthController.cs` and `Views/Home/Index.cshtml`. Should be moved to config before going to production.
- **Regrid Parcel API** — property owner name lookup. Free Starter plan allows 25 lookups/day. Add token under `"Regrid": { "Token": "..." }`.
- **BatchSkipTracing** — pay-per-record contact enrichment (~$0.12/lookup). The plumbing is in place (database table, API endpoint, UI button) but the actual API call is stubbed pending a paid account. Add key under `"BatchSkipTracing": { "ApiKey": "..." }`.

---

## Project Structure

```
Controllers/
  HomeController.cs         – Serves the main search page
  RoofHealthController.cs   – Core data engine: geocoding, OSM, NOAA, risk scoring, CSV export
  LeadsController.cs        – CRUD for saved leads, enrichment endpoint, stats API
  DashboardController.cs    – Per-user stats and recent activity
  AdminController.cs        – Platform-wide metrics and user table (admin only)
  AuthController.cs         – Google/Microsoft OAuth flow, cookie sign-in, dev backdoor

Data/
  AppDbContext.cs            – EF Core context, table/column mappings
  Models/
    User.cs                  – Registered users (provider, email, display name)
    Lead.cs                  – Saved properties with all storm/risk metadata
    Enrichment.cs            – Contact lookup records per lead

Services/
  RealDataService.cs         – Wraps Overpass, NOAA SWDI, and Regrid API calls

Views/
  Home/Index.cshtml          – Search UI, Google Map, property table
  Leads/Saved.cshtml         – Lead management / pipeline view
  Dashboard/Index.cshtml     – User dashboard (stats, recent leads, recent enrichments)
  Admin/Index.cshtml         – Admin dashboard (all users, platform totals)
  Auth/Login.cshtml          – OAuth sign-in page
```

---

## Database

SQLite file lives at `bin/Debug/net8.0/data/leads.db` at runtime. Three tables:

- **users** — one row per OAuth login. Provider + provider_id is the unique key.
- **leads** — saved properties. Address is unique; re-saving an existing address updates the record rather than creating a duplicate.
- **enrichments** — one row per contact lookup attempt, linked to a user and lead.

The app uses `EnsureCreated()` at startup (not EF migrations). New tables added after the initial DB was created are patched in via `ExecuteSqlRaw` with `CREATE TABLE IF NOT EXISTS` — see `Program.cs`.

---

## Authentication

OAuth via Google and Microsoft. Credentials go in `appsettings.json` under `Auth:Google` and `Auth:Microsoft`. The app works without them configured (login page just shows no provider buttons).

A dev backdoor at `/Auth/DevLogin` signs in as a local dev user without OAuth. It is hard-blocked in Production (`IsDevelopment()` check).

Admin access is currently hard-coded to `jaholder78@gmail.com` in both `DashboardController` and `AdminController`.

---

## Configuration Reference

```jsonc
{
  "Regrid": {
    "Token": ""           // Free at app.regrid.com — 25 owner lookups/day
  },
  "BatchSkipTracing": {
    "ApiKey": ""          // Pay-per-record contact enrichment, ~$0.12/lookup
  },
  "Auth": {
    "Google": {
      "ClientId": "",
      "ClientSecret": ""  // console.cloud.google.com → Credentials
    },
    "Microsoft": {
      "ClientId": "",
      "ClientSecret": ""  // portal.azure.com → App registrations
    }
  }
}
```

---

## Intended Workflow

1. Contractor searches a neighborhood address
2. App pulls real addresses from OSM and scores them against NOAA hail data
3. High-risk properties appear at the top of the map and table
4. Contractor selects the best prospects and saves them as leads
5. From Saved Leads, contractor adds notes and triggers contact lookup (BatchSkipTracing) to get owner name/phone/email
6. Contractor exports to CSV or works leads directly from the app

---

## Known Gaps / Roadmap

- **BatchSkipTracing integration** — endpoint and DB row are created, actual API call is a stub. Needs a paid key and the webhook/polling flow to update the status when results come back.
- **Google Maps API key** — currently hardcoded in source. Should move to `appsettings.json` and be injected into views.
- **EF Migrations** — the app currently uses `EnsureCreated` + manual DDL patches. A proper migration pipeline would make schema evolution safer.
- **Admin access** — hardcoded email check. Should eventually be a database role or claim.
- **Rate limiting** — no per-user rate limiting on the `/RoofHealth/Neighborhood` endpoint. Could get expensive on Google Maps API under load.
- **Contact Lookup UI** — the lightning bolt button on saved leads hits the enrichment endpoint but the result is a stub message. Needs the full BatchSkipTracing flow + polling to surface owner contact details.
