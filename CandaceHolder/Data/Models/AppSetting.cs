namespace CandaceHolder.Data.Models
{
    /// <summary>
    /// A setting saved from the Admin UI (e.g. "Email:SmtpHost"). Overrides the
    /// same key from appsettings / Fly secrets. Secret values are stored
    /// encrypted — see SettingsService.
    /// </summary>
    public class AppSetting
    {
        public string   Key       { get; set; } = "";
        public string?  Value     { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
