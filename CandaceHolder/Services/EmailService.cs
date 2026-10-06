using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace CandaceHolder.Services
{
    /// <summary>
    /// Sends email via any SMTP server. Settings come from Admin → Email settings
    /// (SettingsService), falling back to "Email": { SmtpHost, SmtpPort, Username,
    /// Password, FromAddress, FromName } in appsettings / Fly secrets.
    /// </summary>
    public class EmailService
    {
        private readonly SettingsService        _settings;
        private readonly ILogger<EmailService>  _logger;

        public EmailService(SettingsService settings, ILogger<EmailService> logger)
        {
            _settings = settings;
            _logger   = logger;
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_settings.Get("Email:SmtpHost")) &&
            !string.IsNullOrWhiteSpace(_settings.Get("Email:FromAddress"));

        /// <summary>Sends one email. Returns false (and logs why) on any failure.</summary>
        public async Task<bool> SendAsync(string toAddress, string subject, string htmlBody)
        {
            if (!IsConfigured)
            {
                _logger.LogWarning("Email not configured — skipping send to {To}", toAddress);
                return false;
            }
            try
            {
                var message = NewMessage(subject, htmlBody);
                message.To.Add(MailboxAddress.Parse(toAddress));
                await SendCoreAsync(message);
                _logger.LogInformation("Email sent to {To}: {Subject}", toAddress, subject);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {To}", toAddress);
                return false;
            }
        }

        /// <summary>
        /// Sends a test email and returns the SMTP error text on failure, so the
        /// settings page can show exactly what went wrong.
        /// </summary>
        public async Task<string?> SendTestAsync(string toAddress)
        {
            if (!IsConfigured) return "Enter at least an SMTP server and a From address first.";
            try
            {
                var message = NewMessage("Candace Holder — test email",
                    "<p>This is a test email from <b>Candace Holder</b>. Email sending is set up correctly.</p>");
                message.To.Add(MailboxAddress.Parse(toAddress));
                await SendCoreAsync(message);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Test email to {To} failed", toAddress);
                return ex.Message;
            }
        }

        /// <summary>
        /// Sends one message to a list of recipients via Bcc, so recipients can't
        /// see each other's addresses. The visible "To" is FromAddress itself.
        /// Used for admin broadcast emails — see AdminController.EmailAllUsers.
        /// </summary>
        public async Task<bool> SendBccBlastAsync(IEnumerable<string> bccAddresses, string subject, string htmlBody)
        {
            if (!IsConfigured)
            {
                _logger.LogWarning("Email not configured — skipping BCC blast");
                return false;
            }

            var recipients = bccAddresses
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (recipients.Count == 0)
            {
                _logger.LogWarning("BCC blast requested with no recipients — skipping");
                return false;
            }

            try
            {
                var message = NewMessage(subject, htmlBody);
                message.To.Add(message.From[0]);
                foreach (var addr in recipients)
                {
                    try { message.Bcc.Add(MailboxAddress.Parse(addr)); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Skipping malformed BCC address {Addr}", addr); }
                }
                await SendCoreAsync(message);
                _logger.LogInformation("BCC blast sent to {Count} recipient(s): {Subject}", recipients.Count, subject);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send BCC blast to {Count} recipient(s)", recipients.Count);
                return false;
            }
        }

        private MimeMessage NewMessage(string subject, string htmlBody)
        {
            var fromAddress = _settings.Get("Email:FromAddress")!;
            var fromName    = _settings.Get("Email:FromName") ?? "Candace Holder";

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromAddress));
            message.Subject = subject;
            message.Body    = new TextPart("html") { Text = htmlBody };
            return message;
        }

        // Throws on failure — callers decide whether to swallow or report it.
        private async Task SendCoreAsync(MimeMessage message)
        {
            var host     = _settings.Get("Email:SmtpHost")!;
            var port     = int.TryParse(_settings.Get("Email:SmtpPort"), out var p) ? p : 587;
            var username = _settings.Get("Email:Username") ?? "";
            var password = _settings.Get("Email:Password") ?? "";

            using var client = new SmtpClient { Timeout = 20000 };
            var security = port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(host, port, security);
            if (!string.IsNullOrWhiteSpace(username))
                await client.AuthenticateAsync(username, password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
        }
    }
}
