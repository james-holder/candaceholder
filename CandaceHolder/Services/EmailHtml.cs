using System.Net;
using System.Text.RegularExpressions;
using Ganss.Xss;

namespace CandaceHolder.Services
{
    /// <summary>
    /// Template bodies written in the formatting toolbar are HTML. This keeps
    /// that HTML email-safe: only simple formatting tags and inline styles
    /// survive (no scripts, images, classes or pasted page layouts), then the
    /// template shortcuts — {{logo}}, a [text](url) line as a button — are
    /// turned into email markup.
    /// </summary>
    public static class EmailHtml
    {
        private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

        private static HtmlSanitizer CreateSanitizer()
        {
            var s = new HtmlSanitizer();
            s.AllowedTags.Clear();
            foreach (var t in new[] { "div", "p", "br", "b", "strong", "i", "em", "u", "s", "strike",
                                      "span", "font", "a", "ul", "ol", "li", "blockquote", "hr" })
                s.AllowedTags.Add(t);
            s.AllowedAttributes.Clear();
            foreach (var a in new[] { "style", "href", "align", "color", "face", "size" })
                s.AllowedAttributes.Add(a);
            s.AllowedCssProperties.Clear();
            foreach (var p in new[] { "color", "background-color", "font-size", "font-family", "font-weight",
                                      "font-style", "text-decoration", "text-decoration-line", "text-align",
                                      "line-height", "margin-left", "padding-left" })
                s.AllowedCssProperties.Add(p);
            s.AllowedSchemes.Clear();
            foreach (var sc in new[] { "http", "https", "mailto", "tel" })
                s.AllowedSchemes.Add(sc);
            s.AllowedAtRules.Clear();
            s.KeepChildNodes = true;   // a disallowed wrapper (e.g. pasted <table>) keeps its text
            return s;
        }

        // Blocks whose text must not survive as visible words (KeepChildNodes keeps text otherwise).
        private static readonly Regex DropWithContent = new(
            @"<(script|style|title|head|template|noscript|iframe|object)\b[^>]*>[\s\S]*?</\1\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Sanitize(string html) =>
            Sanitizer.Sanitize(DropWithContent.Replace(html ?? "", ""));

        private static readonly Regex ButtonBlock = new(
            @"<(div|p)([^>]*)>\s*\[([^\]<]+)\]\((https?://[^\s)<""]+)\)\s*(?:<br\s*/?>)?\s*</\1>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex InlineLink = new(
            @"\[([^\]<]+)\]\((https?://[^\s)<""]+)\)", RegexOptions.Compiled);
        private static readonly Regex Bold = new(@"\*\*([^*<]+?)\*\*", RegexOptions.Compiled);
        private static readonly Regex UnstyledLink = new(@"<a (?![^>]*\bstyle=)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Sanitized body HTML → email HTML: logo, buttons, link colors and
        /// list styles (email clients and the app's own CSS drop list bullets).
        /// Variables are filled in afterwards, so lead data can't add markup.
        /// </summary>
        public static string Finish(string html, string accent, string? logoUrl)
        {
            html = TemplateRenderer.LogoToken.Replace(html, m =>
            {
                if (logoUrl == null) return "";
                var h = m.Groups[1].Value.ToLowerInvariant() switch { "small" => 36, "large" => 100, _ => 60 };
                return "<img src=\"" + WebUtility.HtmlEncode(logoUrl) + "\" alt=\"\" height=\"" + h + "\" style=\"height:" + h +
                       "px;width:auto;max-width:100%;border:0;display:inline-block;vertical-align:middle\">";
            });

            // Text and URL are already HTML-encoded here (it's HTML source).
            html = ButtonBlock.Replace(html, m =>
                "<div" + WithStyle(m.Groups[2].Value, "margin:10px 0") + "><a href=\"" + m.Groups[4].Value + "\" style=\"display:inline-block;" +
                "background:" + accent + ";color:#ffffff;padding:12px 24px;border-radius:10px;font-weight:bold;text-decoration:none;" +
                "font-family:Arial,Helvetica,sans-serif;font-size:15px\">" + m.Groups[3].Value + "</a></div>");
            html = InlineLink.Replace(html, m =>
                "<a href=\"" + m.Groups[2].Value + "\" style=\"color:" + accent + ";font-weight:bold\">" + m.Groups[1].Value + "</a>");
            html = Bold.Replace(html, "<strong>$1</strong>");
            html = UnstyledLink.Replace(html, "<a style=\"color:" + accent + "\" ");

            // Browsers save colors as rgb()/rgba(); older Outlook only reads hex.
            html = Regex.Replace(html, @"rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*([\d.]+)\s*)?\)", m =>
                m.Groups[4].Success && double.TryParse(m.Groups[4].Value, System.Globalization.NumberStyles.Float,
                                                       System.Globalization.CultureInfo.InvariantCulture, out var a) && a == 0
                    ? "transparent"
                    : $"#{int.Parse(m.Groups[1].Value):x2}{int.Parse(m.Groups[2].Value):x2}{int.Parse(m.Groups[3].Value):x2}");

            html = Regex.Replace(html, @"<ul(?=[\s>])", "<ul style=\"margin:4px 0;padding-left:24px;list-style-type:disc\"", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<ol(?=[\s>])", "<ol style=\"margin:4px 0;padding-left:24px;list-style-type:decimal\"", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<blockquote(?=[\s>])", "<blockquote style=\"margin:8px 0;padding-left:12px;border-left:3px solid " + accent + "\"", RegexOptions.IgnoreCase);
            return html;
        }

        // Adds CSS to a tag's attributes, merging with an existing style="…" (a second
        // style attribute would be ignored).
        private static string WithStyle(string attrs, string css)
        {
            var m = Regex.Match(attrs, @"\bstyle=""([^""]*)""", RegexOptions.IgnoreCase);
            if (!m.Success) return attrs + " style=\"" + css + "\"";
            var existing = m.Groups[1].Value.TrimEnd().TrimEnd(';');
            return attrs.Remove(m.Index, m.Length).Insert(m.Index, "style=\"" + existing + ";" + css + "\"");
        }

        /// <summary>HTML body → the plain-text part of the email.</summary>
        public static string ToPlainText(string html)
        {
            var t = html ?? "";
            t = Regex.Replace(t, @"<a\b[^>]*href=""([^""]*)""[^>]*>(.*?)</a>", m =>
            {
                var href = WebUtility.HtmlDecode(m.Groups[1].Value);
                var text = Regex.Replace(m.Groups[2].Value, "<[^>]+>", "");
                return WebUtility.HtmlDecode(text).Trim() == href ? text : $"{text} ({m.Groups[1].Value})";
            }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            t = Regex.Replace(t, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"<li[^>]*>", "- ", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"</(div|p|li|blockquote|ul|ol)>", "\n", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"<hr[^>]*>", "\n----\n", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "<[^>]+>", "");
            t = WebUtility.HtmlDecode(t).Replace(' ', ' ');
            t = Regex.Replace(t, @"[ \t]+\n", "\n");
            t = Regex.Replace(t, @"\n{3,}", "\n\n");
            return t.Trim();
        }
    }
}
