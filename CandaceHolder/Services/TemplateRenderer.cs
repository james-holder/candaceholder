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

        /// <param name="htmlEncode">True when filling HTML: lead values are encoded so
        /// they can't add markup (fallback text is template source, already HTML).</param>
        public static string Render(string template, Context ctx, bool htmlEncode = false)
        {
            var values = Values(ctx);
            return VarPattern.Replace(template ?? "", m =>
            {
                var name = m.Groups[1].Value.ToLowerInvariant();
                if (!values.TryGetValue(name, out var value)) return m.Value;   // unknown — leave visible
                if (!string.IsNullOrWhiteSpace(value)) return htmlEncode ? WebUtility.HtmlEncode(value!) : value!;
                return m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
            });
        }

        /// <summary>
        /// A template body → the email's HTML and plain-text parts, variables
        /// filled in. Bodies from the formatting toolbar are HTML (sanitized);
        /// older templates are plain text with the ** / [text](url) shortcuts.
        /// Formatting is applied before variables so lead data can't add markup.
        /// </summary>
        // {{signature}} — the sending user's signature (see RenderBody).
        public static readonly Regex SignatureToken =
            new(@"\{\{\s*signature\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <param name="signatureHtml">The sender's saved signature; when empty,
        /// {{signature}} falls back to their name.</param>
        public static (string Html, string Text) RenderBody(string body, bool isHtml, Context ctx, string accent, string? logoUrl,
                                                             string baseUrl, string? signatureHtml = null)
        {
            // Swapped in before variables are filled, so a signature can use them too.
            var sig = string.IsNullOrWhiteSpace(signatureHtml) ? null : EmailHtml.Sanitize(signatureHtml);

            if (!isHtml)
            {
                body = SignatureToken.Replace(body ?? "", sig == null ? "{{sender_name}}" : EmailHtml.ToPlainText(sig));
                return (Render(ToHtml(body, accent, logoUrl), ctx, htmlEncode: true), Render(ToText(body), ctx));
            }

            var clean = SignatureToken.Replace(EmailHtml.Sanitize(body), sig ?? "{{sender_name}}");
            return (Render(EmailHtml.Finish(clean, accent, logoUrl, baseUrl), ctx, htmlEncode: true),
                    Render(ToText(EmailHtml.ToPlainText(clean)), ctx));
        }

        // Light formatting for template bodies:
        //   **bold**                       → bold
        //   [text](https://…)              → link
        //   a [text](https://…) on its own line → button
        //   bare https://… addresses       → link
        //   {{logo}} / {{logo|small}} / {{logo|large}} → the Company Profile logo
        private static readonly Regex LinkPattern =
            new(@"\[([^\]\n]+)\]\((https?://[^\s)]+)\)|(https?://[^\s<]+[^\s<.,;:!?)])", RegexOptions.Compiled);
        private static readonly Regex ButtonLine =
            new(@"^\s*\[([^\]\n]+)\]\((https?://[^\s)]+)\)\s*$", RegexOptions.Compiled);
        private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
        // Not a normal variable: Render leaves it in place and ToHtml swaps in the image.
        public static readonly Regex LogoToken =
            new(@"\{\{\s*logo\s*(?:\|\s*(small|medium|large)\s*)?\}\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LogoLine =
            new(@"^\s*\{\{\s*logo\s*(?:\|\s*(small|medium|large)\s*)?\}\}\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Plain-text body → HTML (escaped, line breaks kept, formatting above applied).</summary>
        /// <param name="logoUrl">Absolute logo URL for {{logo}}; null removes the token.</param>
        public static string ToHtml(string plainText, string accent = "#0d9488", string? logoUrl = null)
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
                var logo = LogoLine.Match(lines[i]);
                if (logo.Success)
                {
                    if (logoUrl != null) sb.Append(Logo(logoUrl, logo.Groups[1].Value, block: true));
                    continue;
                }
                sb.Append(LogoToken.Replace(InlineHtml(lines[i], accent),
                    m => logoUrl == null ? "" : Logo(logoUrl, m.Groups[1].Value, block: false)));
                if (i < lines.Length - 1) sb.Append("<br>");
            }
            return sb.ToString();
        }

        /// <summary>Plain-text part of the email: links written out, ** removed.</summary>
        public static string ToText(string body)
        {
            var text = LogoToken.Replace(body ?? "", "");
            text = LinkPattern.Replace(text, m => m.Groups[3].Success ? m.Value : $"{m.Groups[1].Value}: {m.Groups[2].Value}");
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

        private static string Logo(string url, string size, bool block)
        {
            var h = size.ToLowerInvariant() switch { "small" => 36, "large" => 100, _ => 60 };
            return "<img src=\"" + WebUtility.HtmlEncode(url) + "\" alt=\"\" height=\"" + h + "\" style=\"height:" + h +
                   "px;width:auto;max-width:100%;border:0;" + (block ? "display:block;margin:8px 0" : "vertical-align:middle") + "\">";
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
