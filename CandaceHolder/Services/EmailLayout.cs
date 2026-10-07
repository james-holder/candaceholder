using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using OrgModel = CandaceHolder.Data.Models.Org;

namespace CandaceHolder.Services
{
    /// <summary>
    /// Wraps a rendered template body in the email that actually goes out:
    /// either the branded layout (logo, colors and contact details from
    /// Company Profile) or a plain personal-looking note. Both end with the
    /// footer every marketing email needs: company, mailing address and an
    /// unsubscribe link (CAN-SPAM).
    ///
    /// Email clients ignore most CSS, so the branded layout is a table with
    /// inline styles only.
    /// </summary>
    public static class EmailLayout
    {
        // Used when Company Profile has no colors set — the app's own pink / teal.
        public const string DefaultHeaderColor = "#e11d74";
        public const string DefaultAccentColor = "#0d9488";

        // Header logo sizes offered on the Email Templates page (height in px).
        public static readonly IReadOnlyList<(string Label, int Height)> HeaderLogoSizes = new[]
        {
            ("Small", 40), ("Medium", 56), ("Large", 80), ("Extra large", 110),
        };
        public const int DefaultHeaderLogoHeight = 56;

        public static int HeaderLogoHeight(OrgModel? org) =>
            org?.EmailLogoHeight is int h && HeaderLogoSizes.Any(s => s.Height == h) ? h : DefaultHeaderLogoHeight;

        private const string Font = "font-family:Arial,Helvetica,sans-serif;";

        /// <param name="bodyHtml">Rendered message (TemplateRenderer.RenderBody).</param>
        /// <param name="bodyText">Plain-text version of the same message.</param>
        /// <param name="logoUrl">Absolute URL of the logo, or null for none.</param>
        public static (string Html, string Text) Build(
            string bodyHtml, string bodyText, OrgModel? org, string? logoUrl, string unsubscribeUrl, bool branded)
        {
            var company = CompanyName(org) ?? "";
            var address = org?.Address?.Trim() ?? "";
            var accent  = AccentFor(org);

            var text = bodyText +
                       (branded ? SignatureText(org) : "") +
                       "\n\n--\n" + company + (address.Length > 0 ? "\n" + address : "") +
                       "\nDon't want these emails? Unsubscribe: " + unsubscribeUrl;

            var footer =
                Enc(company) +
                (address.Length > 0 ? "<br>" + Enc(address).Replace("\n", "<br>") : "") +
                "<br>Don't want these emails? <a href=\"" + Enc(unsubscribeUrl) + "\" style=\"color:#6b7280\">Unsubscribe</a>";

            if (!branded)
            {
                var plain =
                    "<div style=\"" + Font + "font-size:15px;line-height:1.5;color:#1f1235\">" +
                    bodyHtml + "</div>" +
                    "<hr style=\"border:0;border-top:1px solid #e5e7eb;margin:24px 0 12px\">" +
                    "<div style=\"" + Font + "font-size:12px;color:#6b7280;line-height:1.5\">" + footer + "</div>";
                return (plain, text);
            }

            var header     = ValidColor(org?.HeaderColor) ?? DefaultHeaderColor;
            var headerText = IsLight(header) ? "#1f1235" : "#ffffff";

            // Wide logos are capped by width so they don't overflow the 600px email
            var logoH = HeaderLogoHeight(org);
            var logoW = Math.Min(520, logoH * 5);
            var brand = logoUrl != null
                ? "<img src=\"" + Enc(logoUrl) + "\" alt=\"" + Enc(company) + "\" height=\"" + logoH + "\" " +
                  "style=\"display:block;margin:0 auto;max-height:" + logoH + "px;max-width:" + logoW + "px;height:" + logoH + "px;width:auto;border:0\">"
                : "<div style=\"" + Font + "font-size:22px;font-weight:bold;color:" + headerText + "\">" + Enc(company) + "</div>";
            var tagline = string.IsNullOrWhiteSpace(org?.Tagline) ? "" :
                "<div style=\"" + Font + "font-size:13px;color:" + headerText + ";opacity:.85;margin-top:6px\">" + Enc(org!.Tagline!.Trim()) + "</div>";

            var html = new StringBuilder()
                .Append("<div style=\"background:#f6f3fa;padding:24px 12px\">")
                .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:600px;margin:0 auto;border-collapse:separate\">")
                // Header band: logo (or company name) + tagline
                .Append("<tr><td style=\"background:").Append(header).Append(";padding:22px 28px;border-radius:14px 14px 0 0;text-align:center\">")
                .Append(brand).Append(tagline).Append("</td></tr>")
                // Accent stripe
                .Append("<tr><td style=\"background:").Append(accent).Append(";height:4px;line-height:4px;font-size:0\">&nbsp;</td></tr>")
                // Message + signature
                .Append("<tr><td style=\"background:#ffffff;padding:28px;border-radius:0 0 14px 14px;").Append(Font)
                .Append("font-size:15px;line-height:1.6;color:#1f1235\">")
                .Append(bodyHtml)
                .Append(SignatureHtml(org, accent))
                .Append("</td></tr>")
                // Required footer
                .Append("<tr><td style=\"padding:16px 28px;text-align:center;").Append(Font)
                .Append("font-size:12px;color:#6b7280;line-height:1.5\">").Append(footer).Append("</td></tr>")
                .Append("</table></div>")
                .ToString();
            return (html, text);
        }

        /// <summary>Logo usable in email, or null. Gmail and Outlook don't show SVG.</summary>
        public static bool HasEmailLogo(OrgModel? org) =>
            !string.IsNullOrWhiteSpace(org?.LogoPath) &&
            !org!.LogoPath!.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

        public static string AccentFor(OrgModel? org) => ValidColor(org?.AccentColor) ?? DefaultAccentColor;

        public static string? CompanyName(OrgModel? org) =>
            string.IsNullOrWhiteSpace(org?.CompanyName) ? org?.Name : org.CompanyName;

        // ── Signature: phone · website, then social links ───────────────
        private static string SignatureHtml(OrgModel? org, string accent)
        {
            if (org == null) return "";
            var link = "color:" + accent + ";text-decoration:none;font-weight:bold";

            var contact = new List<string>();
            if (!string.IsNullOrWhiteSpace(org.Phone))
                contact.Add("<a href=\"tel:" + Enc(Regex.Replace(org.Phone, @"[^\d+]", "")) + "\" style=\"" + link + "\">" + Enc(org.Phone.Trim()) + "</a>");
            if (Url(org.Website) is string site)
                contact.Add("<a href=\"" + Enc(site) + "\" style=\"" + link + "\">" + Enc(DisplayUrl(site)) + "</a>");
            if (!string.IsNullOrWhiteSpace(org.CompanyEmail))
                contact.Add("<a href=\"mailto:" + Enc(org.CompanyEmail.Trim()) + "\" style=\"" + link + "\">" + Enc(org.CompanyEmail.Trim()) + "</a>");

            var social = new List<string>();
            if (Url(org.FacebookUrl)       is string fb) social.Add("<a href=\"" + Enc(fb) + "\" style=\"" + link + "\">Facebook</a>");
            if (Url(org.InstagramUrl)      is string ig) social.Add("<a href=\"" + Enc(ig) + "\" style=\"" + link + "\">Instagram</a>");
            if (Url(org.GoogleBusinessUrl) is string gb) social.Add("<a href=\"" + Enc(gb) + "\" style=\"" + link + "\">Google reviews</a>");

            if (contact.Count == 0 && social.Count == 0) return "";
            var sep = " <span style=\"color:#9ca3af\">&middot;</span> ";
            return "<div style=\"margin-top:24px;padding-top:16px;border-top:1px solid #f1e8f3;font-size:13px;line-height:1.7;color:#6b7280\">" +
                   (contact.Count > 0 ? string.Join(sep, contact) : "") +
                   (contact.Count > 0 && social.Count > 0 ? "<br>" : "") +
                   (social.Count > 0 ? string.Join(sep, social) : "") +
                   "</div>";
        }

        private static string SignatureText(OrgModel? org)
        {
            if (org == null) return "";
            var lines = new[] { org.Phone?.Trim(), Url(org.Website), org.CompanyEmail?.Trim() }
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            return lines.Count == 0 ? "" : "\n\n" + string.Join("\n", lines);
        }

        // ── Helpers ─────────────────────────────────────────────────────
        private static string Enc(string s) => WebUtility.HtmlEncode(s);

        private static string? ValidColor(string? hex) =>
            hex != null && Regex.IsMatch(hex.Trim(), "^#[0-9a-fA-F]{6}$") ? hex.Trim() : null;

        // Perceived brightness — light header colors get dark text.
        private static bool IsLight(string hex)
        {
            int r = Convert.ToInt32(hex.Substring(1, 2), 16),
                g = Convert.ToInt32(hex.Substring(3, 2), 16),
                b = Convert.ToInt32(hex.Substring(5, 2), 16);
            return (r * 299 + g * 587 + b * 114) / 1000 > 170;
        }

        // Only http(s) links; "candaceholder.com" becomes "https://candaceholder.com".
        private static string? Url(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s;
            return Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") ? s : null;
        }

        private static string DisplayUrl(string url) =>
            Regex.Replace(url, @"^https?://(www\.)?", "", RegexOptions.IgnoreCase).TrimEnd('/');
    }
}
