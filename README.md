# Candace Holder

A private web app for finding addresses in an area and skip tracing their owners.

1. **Find addresses** — search an address with a radius, look up a single address, or draw a box on the map.
2. **Save** the addresses you want.
3. **Skip trace** saved leads to get the owner's name, phone numbers, and emails.
4. **Work the list** — status pipeline, notes, CSV export.

---

## Tech stack

| Layer | Technology |
|---|---|
| Framework | ASP.NET Core 8 MVC |
| Database | SQLite via Entity Framework Core (`App_Data/leads.db`, created on first run) |
| Frontend | Tailwind CSS (CDN), vanilla JS, Font Awesome, Leaflet |
| Auth | Cookie auth — password accounts, optional Google / Microsoft sign-in |
| Hosting | Fly.io (`fly.toml`), SQLite on a Fly volume mounted at `/app/App_Data` |

## Data sources

| Purpose | Source | Config key |
|---|---|---|
| Addresses in an area | OpenStreetMap Overpass (free, no key) | — |
| Address fallback + geocoding + autocomplete | Google Maps | `GoogleMaps:ApiKey` |
| Street basemap (optional) | MapTiler | `MapTiler:ApiKey` |
| Owner name + year built | Regrid | `Regrid:Token` |
| Phone + email | Whitepages Pro (preferred) or BatchSkipTracing | `WhitepagesPro:ApiKey`, `BatchSkipTracing:ApiKey` |
| Password resets, team invites | Any SMTP server | `Email:*` |

Any provider left blank is skipped. Skip-trace lookups are billed by the provider.

## Configuration

Keep real keys out of `appsettings.json` (it's committed):

- **Local:** put them in `CandaceHolder/appsettings.Development.json` (git-ignored).
- **Production:** set Fly secrets. Use `__` for nesting, e.g.
  `fly secrets set GoogleMaps__ApiKey=... Regrid__Token=... -a candaceholder`

Sign-up is **closed** by default (`Auth:AllowRegistration = false`). Only `AdminEmail` can
self-register; everyone else joins through a Team invite.

## Running locally

```bash
cd CandaceHolder
dotnet run
```

In Development a login is seeded on first run (see the console output).

## Deploying

```bash
fly deploy
```

The GitHub Actions workflow in `.github/workflows/deploy.yml` deploys every push to `main`
once a `FLY_API_TOKEN` repo secret is set.
