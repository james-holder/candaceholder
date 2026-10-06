using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using CandaceHolder.Data;
using CandaceHolder.Data.Models;

namespace CandaceHolder.Services
{
    /// <summary>
    /// Settings editable from the Admin UI, layered over IConfiguration:
    /// a value saved here wins; otherwise the appsettings / Fly-secret value
    /// is used. Secret keys (passwords) are encrypted with ASP.NET Data
    /// Protection, whose keys live in App_Data/dp-keys on the Fly volume.
    /// </summary>
    public class SettingsService
    {
        private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "Email:Password"
        };

        private readonly IServiceScopeFactory _scopes;
        private readonly IConfiguration       _config;
        private readonly IDataProtector       _protector;
        private readonly object               _lock = new();
        private Dictionary<string, string>?   _saved;   // null = not loaded yet

        public SettingsService(IServiceScopeFactory scopes, IConfiguration config, IDataProtectionProvider dp)
        {
            _scopes    = scopes;
            _config    = config;
            _protector = dp.CreateProtector("CandaceHolder.AppSettings");
        }

        /// <summary>Saved value if there is one, else the configuration value.</summary>
        public string? Get(string key) =>
            Saved().TryGetValue(key, out var v) ? v : _config[key];

        /// <summary>True when the value comes from the Admin UI rather than configuration.</summary>
        public bool IsSaved(string key) => Saved().ContainsKey(key);

        /// <summary>
        /// Saves values. A null or blank value deletes the saved row, so the key
        /// falls back to configuration.
        /// </summary>
        public async Task SetAsync(IDictionary<string, string?> values)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            foreach (var (key, raw) in values)
            {
                var row = await db.AppSettings.FindAsync(key);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    if (row != null) db.AppSettings.Remove(row);
                    continue;
                }
                var stored = SecretKeys.Contains(key) ? _protector.Protect(raw.Trim()) : raw.Trim();
                if (row == null) db.AppSettings.Add(new AppSetting { Key = key, Value = stored, UpdatedAt = DateTime.UtcNow });
                else { row.Value = stored; row.UpdatedAt = DateTime.UtcNow; }
            }

            await db.SaveChangesAsync();
            lock (_lock) _saved = null;   // reload on next read
        }

        private Dictionary<string, string> Saved()
        {
            lock (_lock)
            {
                if (_saved != null) return _saved;

                using var scope = _scopes.CreateScope();
                var db   = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var rows = db.AppSettings.AsNoTracking().ToList();

                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in rows)
                {
                    if (string.IsNullOrEmpty(r.Value)) continue;
                    if (SecretKeys.Contains(r.Key))
                    {
                        try { dict[r.Key] = _protector.Unprotect(r.Value); }
                        catch { /* key ring changed — treat as unset */ }
                    }
                    else dict[r.Key] = r.Value;
                }
                return _saved = dict;
            }
        }
    }
}
