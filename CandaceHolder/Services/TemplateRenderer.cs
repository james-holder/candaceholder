using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CandaceHolder.Services
{
    /// <summary>
    /// Fills {{variables}} in email templates. Each variable can have a fallback
    /// used when the value is missing: {{first_name|there}} → "there".
    /// Unknown variables are left as-is so typos are visible in the preview.
    /// </summary>
    public static class TemplateRenderer
    {
        /// <summary>Variable name → description, shown as insert buttons in the editor.</summary>
        public static readonly IReadOnlyList<(string Name, string Description)> Variables = new[]
        {
            ("first_name",       "Owner's first name"),
            ("last_name",        "Owner's last name"),
            ("full_name",        "Owner's full name"),
            ("email",            "Owner's email address"),
            ("property_address", "Full property address"),
            ("street",           "Street address only"),
            ("city",             "Property city"),
            ("sender_name",      "Your name (the person sending)"),
            ("company_name",     "Company name from Company Profile"),
        };

        public record Context(string? FullName, string? Email, string? PropertyAddress,
                              string? SenderName, string? CompanyName);

        private static readonly Regex VarPattern =
            new(@"\{\{\s*([a-z_]+)\s*(?:\|([^}]*))?\}\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Owner names from county records are often companies or trusts —
        // don't greet "Dear Smith" as "Hi Smith Family" by mistake.
        private static readonly Regex EntityName =
            new(@"\b(LLC|L\.L\.C|INC|CORP|CO|COMPANY|TRUST|TRUSTEE|ESTATE|PARTNERS|LP|LTD|BANK|HOLDINGS|PROPERTIES|ASSOCIATION)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Render(string template, Context ctx)
        {
            var values = Values(ctx);
            return VarPattern.Replace(template ?? "", m =>
            {
                var name = m.Groups[1].Value.ToLowerInvariant();
                if (!values.TryGetValue(name, out var value)) return m.Value;   // unknown — leave visible
                if (!string.IsNullOrWhiteSpace(value)) return value!;
                return m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
            });
        }

        // Light formatting for template bodies:
        //   **bold**                       → bold
        //   [text](https://…)              → link
        //   a [text](https://…) on its own line → button
        //   bare https://… addresses       → link
        private static readonly Regex LinkPattern =
            new(@"\[([^\]\n]+)\]\((https?://[^\s)]+)\)|(https?://[^\s<]+[^\s<.,;:!?)])", RegexOptions.Compiled);
        private static readonly Regex ButtonLine =
            new(@"^\s*\[([^\]\n]+)\]\((https?://[^\s)]+)\)\s*$", RegexOptions.Compiled);
        private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);

        /// <summary>Plain-text body → HTML (escaped, line breaks kept, formatting above applied).</summary>
        public static string ToHtml(string plainText, string accent = "#0d9488")
        {
            var lines = (plainText ?? "").Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                var button = ButtonLine.Match(lines[i]);
                if (button.Success)
                {
                    sb.Append(Button(button.Groups[1].Value, button.Groups[2].Value, accent));
                    continue;   // the button is its own block — no extra line break
                }
                sb.Append(InlineHtml(lines[i], accent));
                if (i < lines.Length - 1) sb.Append("<br>");
            }
            return sb.ToString();
        }

        /// <summary>Plain-text part of the email: links written out, ** removed.</summary>
        public static string ToText(string body)
        {
            var text = LinkPattern.Replace(body ?? "", m => m.Groups[3].Success ? m.Value : $"{m.Groups[1].Value}: {m.Groups[2].Value}");
            return Bold.Replace(text, "$1");
        }

        private static string InlineHtml(string line, string accent)
        {
            var sb = new StringBuilder();
            int pos = 0;
            foreach (Match m in LinkPattern.Matches(line))
            {
                sb.Append(WebUtility.HtmlEncode(line[pos..m.Index]));
                var (text, url) = m.Groups[3].Success ? (m.Value, m.Value) : (m.Groups[1].Value, m.Groups[2].Value);
                sb.Append("<a href=\"").Append(WebUtility.HtmlEncode(url)).Append("\" style=\"color:").Append(accent)
                  .Append(";font-weight:bold\">").Append(WebUtility.HtmlEncode(text)).Append("</a>");
                pos = m.Index + m.Length;
            }
            sb.Append(WebUtility.HtmlEncode(line[pos..]));
            return Bold.Replace(sb.ToString(), "<strong>$1</strong>");
        }

        private static string Button(string text, string url, string accent) =>
            "<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:4px 0\"><tr>" +
            "<td style=\"background:" + accent + ";border-radius:10px\">" +
            "<a href=\"" + WebUtility.HtmlEncode(url) + "\" style=\"display:inline-block;padding:12px 24px;" +
            "font-family:Arial,Helvetica,sans-serif;font-size:15px;font-weight:bold;color:#ffffff;text-decoration:none\">" +
            WebUtility.HtmlEncode(text) + "</a></td></tr></table>";

        private static Dictionary<string, string?> Values(Context ctx)
        {
            var full  = (ctx.FullName ?? "").Trim();
            var isEntity = EntityName.IsMatch(full);
            var parts = isEntity ? Array.Empty<string>() : full.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var (street, city, _, _) = RealDataService.SplitAddress(ctx.PropertyAddress ?? "");

            return new Dictionary<string, string?>
            {
                ["first_name"]       = parts.Length > 0 ? parts[0] : null,
                ["last_name"]        = parts.Length > 1 ? parts[^1] : null,
                ["full_name"]        = full.Length > 0 ? full : null,
                ["email"]            = ctx.Email,
                ["property_address"] = ctx.PropertyAddress?.Replace(", USA", "").Replace(", United States", ""),
                ["street"]           = string.IsNullOrWhiteSpace(street) ? null : street,
                ["city"]             = string.IsNullOrWhiteSpace(city) ? null : city,
                ["sender_name"]      = ctx.SenderName,
                ["company_name"]     = ctx.CompanyName,
            };
        }
    }
}
