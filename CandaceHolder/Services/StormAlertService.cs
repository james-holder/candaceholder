using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using CandaceHolder.Data.Models;

namespace CandaceHolder.Services
{
    /// <summary>
    /// Background service that polls Tomorrow.io every 30 minutes for each watched area.
    /// When a new hail event is detected above the user's threshold, sends an email alert
    /// and records it in sent_alerts to prevent duplicate notifications.
    /// </summary>
    public class StormAlertService : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);

        private readonly IServiceScopeFactory          _scopeFactory;
        private readonly IConfiguration                _config;
        private readonly ILogger<StormAlertService>    _logger;

        public StormAlertService(
            IServiceScopeFactory       scopeFactory,
            IConfiguration             config,
            ILogger<StormAlertService> logger)
        {
            _scopeFactory = scopeFactory;
            _config       = config;
            _logger       = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("StormAlertService started — polling every {Interval} min",
                PollInterval.TotalMinutes);

            // Stagger the first run by 2 minutes so it doesn't fire immediately on startup
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAllWatchedAreasAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "StormAlertService poll cycle failed");
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }

        private async Task CheckAllWatchedAreasAsync(CancellationToken ct)
        {
            var tomorrowKey = _config["TomorrowIo:ApiKey"] ?? "";
            if (string.IsNullOrWhiteSpace(tomorrowKey))
            {
                _logger.LogWarning("StormAlertService: TomorrowIo:ApiKey not configured — skipping");
                return;
            }

            using var scope    = _scopeFactory.CreateScope();
            var db             = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var realData       = scope.ServiceProvider.GetRequiredService<RealDataService>();
            var emailService   = scope.ServiceProvider.GetRequiredService<EmailService>();

            // Load all enabled watched areas that have a user with an email address
            var areas = await db.WatchedAreas
                .Include(w => w.User)
                .Where(w => w.AlertsEnabled && w.User != null && w.User.Email != null)
                .ToListAsync(ct);

            _logger.LogInformation("StormAlertService: checking {Count} watched areas", areas.Count);

            foreach (var area in areas)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await CheckAreaAsync(area, db, realData, emailService, tomorrowKey, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "StormAlertService: error checking area {Id} ({Label})",
                        area.Id, area.Label);
                }
            }
        }

        private async Task CheckAreaAsync(
            WatchedArea area, AppDbContext db,
            RealDataService realData, EmailService emailService,
            string tomorrowKey, CancellationToken ct)
        {
            // Fetch recent hail events from Tomorrow.io for this area
            var events = await realData.GetTomorrowIoHailAsync(
                area.CenterLat, area.CenterLng, tomorrowKey);

            // Filter to events within the area radius and above the user's size threshold
            var qualifying = events
                .Where(e => e.SizeInches >= area.MinHailSizeInches)
                .Where(e => RealDataService.HaversineDistanceMiles(
                    area.CenterLat, area.CenterLng, e.Lat, e.Lng) <= area.RadiusMiles)
                .OrderByDescending(e => e.Date)
                .ToList();

            if (!qualifying.Any()) return;

            // Check which event dates we've already alerted for this area
            var alreadySent = await db.SentAlerts
                .Where(s => s.WatchedAreaId == area.Id)
                .Select(s => s.EventDate.Date)
                .ToListAsync(ct);

            var newEvents = qualifying
                .Where(e => !alreadySent.Contains(e.Date.Date))
                .GroupBy(e => e.Date.Date)
                .Select(g => g.OrderByDescending(e => e.SizeInches).First())
                .OrderByDescending(e => e.Date)
                .ToList();

            if (!newEvents.Any()) return;

            // Use the largest/most recent new event as the headline for the email
            var headline = newEvents.First();

            _logger.LogInformation(
                "StormAlertService: new hail event {Size}\" on {Date} near {Label} — alerting {Email}",
                headline.SizeInches, headline.Date.ToString("yyyy-MM-dd"),
                area.Label, area.User!.Email);

            // Build the deep link to the neighborhood search
            var searchUrl = BuildSearchUrl(area);
            var hailLabel = HailSizeLabel(headline.SizeInches);
            var subject   = $"⛈ Hail Alert: {hailLabel} near {area.Label}";
            var html      = BuildEmailHtml(area, headline, hailLabel, searchUrl, newEvents.Count);

            var sent = await emailService.SendAsync(area.User.Email!, subject, html);

            if (sent)
            {
                // Record all new event dates so we don't re-alert
                foreach (var ev in newEvents)
                {
                    db.SentAlerts.Add(new SentAlert
                    {
                        UserId         = area.UserId,
                        OrgId          = area.OrgId,
                        WatchedAreaId  = area.Id,
                        EventDate      = ev.Date.Date,
                        HailSizeInches = ev.SizeInches,
                        SentAt         = DateTime.UtcNow
                    });
                }
                await db.SaveChangesAsync(ct);
            }
        }

        private static string BuildSearchUrl(WatchedArea area)
        {
            var label = Uri.EscapeDataString(area.Label);
            return $"/RoofHealth/Neighborhood?address={label}&radius={area.RadiusMiles}";
        }

        private static string HailSizeLabel(double inches) => inches switch
        {
            >= 4.00 => $"{inches:F2}\" (Softball)",
            >= 2.75 => $"{inches:F2}\" (Baseball)",
            >= 2.50 => $"{inches:F2}\" (Tennis Ball)",
            >= 2.00 => $"{inches:F2}\" (Hen Egg)",
            >= 1.75 => $"{inches:F2}\" (Golf Ball)",
            >= 1.50 => $"{inches:F2}\" (Ping Pong)",
            >= 1.25 => $"{inches:F2}\" (Half Dollar)",
            >= 1.00 => $"{inches:F2}\" (Quarter)",
            >= 0.88 => $"{inches:F2}\" (Nickel)",
            >= 0.75 => $"{inches:F2}\" (Penny)",
            _       => $"{inches:F2}\""
        };

        private static string BuildEmailHtml(
            WatchedArea area, RealDataService.HailEvent headline,
            string hailLabel, string searchUrl, int totalNewEvents)
        {
            var additionalNote = totalNewEvents > 1
                ? $"<p style='color:#94a3b8;font-size:13px;margin:0 0 16px'>Plus {totalNewEvents - 1} additional storm event(s) in this area.</p>"
                : "";

            return $"""
                <!DOCTYPE html>
                <html>
                <head><meta charset='utf-8'></head>
                <body style='margin:0;padding:0;background:#0f172a;font-family:system-ui,sans-serif'>
                  <div style='max-width:540px;margin:32px auto;background:#1e293b;border-radius:12px;overflow:hidden;border:1px solid #334155'>

                    <!-- Header -->
                    <div style='background:#f97316;padding:20px 28px'>
                      <h1 style='margin:0;color:#fff;font-size:20px;font-weight:700'>⛈ Storm Alert</h1>
                      <p style='margin:4px 0 0;color:#fff3e0;font-size:13px'>StormLead Pro</p>
                    </div>

                    <!-- Body -->
                    <div style='padding:28px'>
                      <h2 style='margin:0 0 8px;color:#f1f5f9;font-size:18px'>New hail event near {System.Net.WebUtility.HtmlEncode(area.Label)}</h2>
                      <p style='margin:0 0 20px;color:#94a3b8;font-size:14px'>{headline.Date:MMMM d, yyyy}</p>

                      <!-- Hail size badge -->
                      <div style='background:#0f172a;border:1px solid #f97316;border-radius:8px;padding:16px 20px;margin-bottom:20px;display:inline-block'>
                        <span style='color:#f97316;font-size:28px;font-weight:800'>{hailLabel}</span>
                      </div>

                      {additionalNote}

                      <p style='color:#cbd5e1;font-size:14px;margin:0 0 24px'>
                        Leads in your <strong style='color:#f1f5f9'>{System.Net.WebUtility.HtmlEncode(area.Label)}</strong> watch area
                        ({area.RadiusMiles:F0}-mile radius) may have sustained roof damage.
                        Search the affected neighborhood now to find homeowners to contact.
                      </p>

                      <!-- CTA button -->
                      <a href='https://stormlead.app{searchUrl}'
                         style='display:inline-block;background:#f97316;color:#fff;font-weight:700;
                                font-size:14px;padding:12px 24px;border-radius:8px;text-decoration:none'>
                        Find Leads in This Area →
                      </a>
                    </div>

                    <!-- Footer -->
                    <div style='padding:16px 28px;border-top:1px solid #334155'>
                      <p style='margin:0;color:#64748b;font-size:12px'>
                        You're receiving this because you set up a watch area in StormLead Pro.
                        Manage your alerts at <a href='https://stormlead.app/Alerts' style='color:#94a3b8'>stormlead.app/Alerts</a>.
                      </p>
                    </div>
                  </div>
                </body>
                </html>
                """;
        }
    }
}
