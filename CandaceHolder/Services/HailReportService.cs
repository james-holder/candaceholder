using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using CandaceHolder.Data.Models;
using System.IO;
using System.Linq;

namespace CandaceHolder.Services
{
    /// <summary>
    /// Generates the informational storm damage PDF report for a single lead
    /// (hail + wind events — headed "STORM DAMAGE REPORT" since 2026-07-23,
    /// previously "HAIL DAMAGE REPORT", to reflect that it isn't hail-only).
    /// This is a door-knocker leave-behind, NOT a certified meteorological assessment.
    /// </summary>
    public class HailReportService
    {
        // Brand palette
        private static readonly string BrandOrange  = "#f97316";
        private static readonly string NavyDark     = "#0f172a";
        private static readonly string SlateLight   = "#94a3b8";
        private static readonly string SlateText    = "#334155";
        private static readonly string White        = "#ffffff";

        public HailReportService()
        {
            // QuestPDF Community licence — free for open-source / small commercial use
            QuestPDF.Settings.License = LicenseType.Community;
        }

        private static string HailSizeReference(double sizeInches) => sizeInches switch
        {
            < 0.75 => "Pea",
            < 0.88 => "Penny",
            < 1.00 => "Nickel",
            < 1.25 => "Quarter",
            < 1.50 => "Half Dollar",
            < 1.75 => "Ping Pong Ball",
            < 2.00 => "Golf Ball",
            < 2.50 => "Hen Egg",
            < 2.75 => "Tennis Ball",
            < 4.00 => "Baseball",
            _      => "Softball"
        };

        // Reference column for wind rows (2026-07-23, was "—"). Damage-tier labels rather
        // than a Beaufort-scale name, to match the hail column's roofing-damage framing —
        // 58 mph is the actual NWS threshold for a "severe" thunderstorm wind report.
        private static string WindReference(double speedMph) => speedMph switch
        {
            < 50  => "Gusty Wind",
            < 58  => "Damaging Wind",
            < 75  => "Severe Thunderstorm Wind",
            < 96  => "Hurricane-Force Wind",
            _     => "Extreme Wind"
        };

        private static string FormatSource(string source) => source switch
        {
            "lsr"      => "LSR",
            "lsr-wind" => "LSR",
            "tomorrow" => "Tomorrow.io",
            // Radar-confirmed via MRMS MESH swath containment (docs/pdf-report-accuracy-punchlist.md
            // item 3) — distinct from a raw point-source report, so it shouldn't read as "NOAA".
            "mesh"     => "Radar-Confirmed",
            _          => "NOAA"
        };

        public byte[] Generate(
            Lead lead,
            string generatedBy,
            IReadOnlyList<RealDataService.HailEvent>? hailHistory = null,
            IReadOnlyList<RealDataService.WindEvent>?  windHistory = null,
            Data.Models.Org? org = null,
            byte[]? logoBytes = null,
            byte[]? mapBytes  = null,
            // SVG logos come through as markup text, not raster bytes — QuestPDF needs its
            // separate .Svg() element for these (see the logo-rendering block below).
            string? logoSvgContent = null)
        {
            // ── Branding resolution ───────────────────────────────────────
            var companyName  = !string.IsNullOrWhiteSpace(org?.CompanyName)  ? org!.CompanyName!  : "StormLead Pro";
            var companyPhone = org?.Phone;
            var companyWeb   = !string.IsNullOrWhiteSpace(org?.Website)       ? org!.Website!      : "stormlead.pro";
            var companyEmail = org?.CompanyEmail;
            var tagline      = org?.Tagline;
            var licenseNo    = org?.LicenseNumber;
            var accentHex    = !string.IsNullOrWhiteSpace(org?.AccentColor)   ? org!.AccentColor!  : BrandOrange;
            var headerHex    = !string.IsNullOrWhiteSpace(org?.HeaderColor)   ? org!.HeaderColor!  : NavyDark;

            var generatedOn  = DateTime.Now.ToString("MMMM d, yyyy 'at' h:mm tt");

            var doc = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.Letter);
                    page.Margin(0);
                    page.DefaultTextStyle(x => x.FontFamily("Helvetica"));

                    // ── Header bar ───────────────────────────────────────────
                    page.Header().Element(header =>
                    {
                        header.Background(headerHex).Padding(0).Column(col =>
                        {
                            // Bold accent strip at top — brand color
                            col.Item().Background(accentHex).Height(10);

                            col.Item().Padding(24).Row(row =>
                            {
                                // Left: logo if available, else accent bar placeholder.
                                // Rendering is wrapped in try/catch (mirrors the "optional, don't fail
                                // PDF generation" pattern already used for the map fetch below) — a
                                // malformed/corrupted logo file of any format should never 500 the whole
                                // report; worst case it just falls back to no-logo layout. Root cause of
                                // the 2026-07-23 500 was specifically SVG logos being force-fed into
                                // .Image(), which only decodes raster bytes — those now go through
                                // logoSvgContent + .Svg() instead.
                                var logoRendered = false;
                                if (!string.IsNullOrWhiteSpace(logoSvgContent))
                                {
                                    try
                                    {
                                        row.ConstantItem(80).AlignMiddle()
                                            .Height(52).Svg(logoSvgContent).FitHeight();
                                        logoRendered = true;
                                    }
                                    catch { /* malformed/unsupported SVG — fall back to no-logo layout */ }
                                }
                                else if (logoBytes != null && logoBytes.Length > 0)
                                {
                                    try
                                    {
                                        row.ConstantItem(80).AlignMiddle()
                                            .Height(52).Image(logoBytes).FitHeight();
                                        logoRendered = true;
                                    }
                                    catch { /* corrupted/unsupported image bytes — same fallback */ }
                                }
                                if (logoRendered)
                                {
                                    row.ConstantItem(16); // spacer
                                }

                                // Title block
                                row.RelativeItem().AlignMiddle().Column(inner =>
                                {
                                    inner.Item().Text("STORM DAMAGE REPORT")
                                        .FontSize(20).Bold().FontColor(accentHex);
                                    inner.Item().PaddingTop(3).Text("Informational Property Assessment")
                                        .FontSize(10).FontColor(SlateLight);
                                });

                                // Right: company name + website
                                row.ConstantItem(160).AlignRight().AlignMiddle().Column(inner =>
                                {
                                    inner.Item().AlignRight().Text(companyName)
                                        .FontSize(13).Bold().FontColor(White);
                                    if (!string.IsNullOrWhiteSpace(companyWeb))
                                        inner.Item().AlignRight().PaddingTop(3).Text(companyWeb)
                                            .FontSize(8).FontColor(SlateLight);
                                });
                            });

                            // Bottom accent line
                            col.Item().Background(accentHex).Height(2);
                        });
                    });

                    // ── Body ────────────────────────────────────────────────
                    page.Content().Padding(32).Column(col =>
                    {
                        col.Spacing(20);

                        // ── Property address card ────────────────────────
                        col.Item().Background("#f8fafc").Border(1).BorderColor("#e2e8f0")
                            .Row(row =>
                        {
                            // Address info (left)
                            row.RelativeItem().Padding(20).Column(inner =>
                            {
                                inner.Item().Text("PROPERTY ADDRESS")
                                    .FontSize(9).Bold().FontColor(SlateLight)
                                    .LetterSpacing(0.08f);
                                inner.Item().PaddingTop(6).Text(lead.Address)
                                    .FontSize(16).Bold().FontColor(NavyDark);
                                if (lead.Lat.HasValue && lead.Lng.HasValue)
                                {
                                    inner.Item().PaddingTop(4).Text(
                                        $"GPS: {lead.Lat:F5}, {lead.Lng:F5}")
                                        .FontSize(9).FontColor(SlateLight);
                                }
                            });

                            // Map image (right) — only shown when available
                            if (mapBytes != null && mapBytes.Length > 0)
                            {
                                row.ConstantItem(220).Height(110)
                                    .Image(mapBytes).FitArea();
                            }
                        });

                        // ── Data source ──────────────────────────────────
                        col.Item().Background("#f1f5f9").Padding(14)
                            .Column(inner =>
                        {
                            inner.Item().Text("DATA SOURCE")
                                .FontSize(8).Bold().FontColor(SlateLight)
                                .LetterSpacing(0.08f);
                            inner.Item().PaddingTop(4).Text(
                                "Storm event and hail data sourced from NOAA National Centers for " +
                                "Environmental Information (NCEI) storm event records and radar-derived " +
                                "hail swath analysis. Property parcel data via Regrid.")
                                .FontSize(9).FontColor(SlateText).LineHeight(1.5f);
                        });

                        // ── Storm History — combined hail + wind table ───
                        // Merged 2026-07-17 (was two separate tables) so the report doesn't read as
                        // thin now that the swath-containment pass above (docs/pdf-report-accuracy-
                        // punchlist.md item 3) trims hail down to only radar-confirmed events. Wind
                        // events are still plain point-radius — there's no MESH-equivalent swath
                        // product for wind — so rows are color-coded by type (orange=hail,
                        // blue=wind) rather than implying wind carries the same location-specific
                        // confirmation hail rows now do. The existing Source column still shows
                        // "Radar-Confirmed" vs "LSR"/"NOAA"/"Tomorrow.io" per row for that distinction.
                        // Distance-from-property column removed 2026-07-23 (James: take it out completely).
                        var combinedRows = new List<(DateTime Date, bool IsHail, string SizeText, string RefText, string SourceText)>();

                        if (hailHistory != null)
                        {
                            foreach (var e in hailHistory)
                            {
                                combinedRows.Add((e.Date, true, $"{e.SizeInches:F2}\"", HailSizeReference(e.SizeInches), FormatSource(e.Source)));
                            }
                        }

                        if (windHistory != null)
                        {
                            foreach (var w in windHistory)
                            {
                                combinedRows.Add((w.Date, false, $"{(int)Math.Round(w.SpeedMph)} mph", WindReference(w.SpeedMph), FormatSource(w.Source)));
                            }
                        }

                        combinedRows = combinedRows.OrderByDescending(r => r.Date).ToList();

                        if (combinedRows.Count > 0)
                        {
                            col.Item().Column(inner =>
                            {
                                inner.Item().Text("STORM HISTORY")
                                    .FontSize(9).Bold().FontColor(SlateLight)
                                    .LetterSpacing(0.08f);
                                inner.Item().PaddingTop(2).Text(
                                    $"{combinedRows.Count} storm event{(combinedRows.Count == 1 ? "" : "s")} near this property — " +
                                    "hail (last 5 years, radar-confirmed where available) and wind (last 12 months).")
                                    .FontSize(9).FontColor(SlateText);

                                inner.Item().PaddingTop(8).Table(table =>
                                {
                                    table.ColumnsDefinition(cols =>
                                    {
                                        cols.RelativeColumn(3);  // Date
                                        cols.RelativeColumn(2);  // Type (Hail/Wind)
                                        cols.RelativeColumn(2);  // Size / Speed
                                        cols.RelativeColumn(3);  // Reference
                                        cols.RelativeColumn(2);  // Source
                                    });

                                    table.Header(h =>
                                    {
                                        static void HdrCell(IContainer c, string text) =>
                                            c.Background("#e2e8f0").Padding(5)
                                             .Text(text).FontSize(8).Bold().FontColor("#475569");

                                        HdrCell(h.Cell(), "Date");
                                        HdrCell(h.Cell(), "Type");
                                        HdrCell(h.Cell(), "Size / Speed");
                                        HdrCell(h.Cell(), "Reference");
                                        HdrCell(h.Cell(), "Source");
                                    });

                                    for (int i = 0; i < combinedRows.Count; i++)
                                    {
                                        var row      = combinedRows[i];
                                        var bg       = i % 2 == 0 ? White : "#f8fafc";
                                        // Event-type badge — orange for hail, blue for wind (2026-07-23).
                                        var typeColor = row.IsHail ? BrandOrange : "#0ea5e9";

                                        table.Cell().Background(bg).Padding(5)
                                            .Text(row.Date.ToString("MMM d, yyyy"))
                                            .FontSize(9).FontColor(NavyDark);
                                        table.Cell().Background(bg).Padding(5).Element(c =>
                                            c.Background(typeColor).Padding(3).AlignCenter()
                                             .Text(row.IsHail ? "HAIL" : "WIND")
                                             .FontSize(7).Bold().FontColor(White));
                                        table.Cell().Background(bg).Padding(5)
                                            .Text(row.SizeText)
                                            .FontSize(9).Bold().FontColor(typeColor);
                                        table.Cell().Background(bg).Padding(5)
                                            .Text(row.RefText)
                                            .FontSize(9).FontColor(SlateText);
                                        table.Cell().Background(bg).Padding(5)
                                            .Text(row.SourceText)
                                            .FontSize(8).FontColor(SlateLight);
                                    }
                                });
                            });
                        }
                        else if (hailHistory != null || windHistory != null)
                        {
                            col.Item().Column(inner =>
                            {
                                inner.Item().Text("STORM HISTORY")
                                    .FontSize(9).Bold().FontColor(SlateLight)
                                    .LetterSpacing(0.08f);
                                inner.Item().PaddingTop(4).Text(
                                    "No confirmed hail or wind events were found near this property in the lookback window.")
                                    .FontSize(9).FontColor(SlateText).Italic();
                            });
                        }

                        // ── Disclaimer ───────────────────────────────────
                        col.Item().Background("#fef3c7").Border(1).BorderColor("#fcd34d")
                            .Padding(14).Column(inner =>
                        {
                            inner.Item().Text("IMPORTANT DISCLAIMER")
                                .FontSize(8).Bold().FontColor("#92400e")
                                .LetterSpacing(0.08f);
                            inner.Item().PaddingTop(4).Text(
                                "This is an informational report only. It is based on publicly available " +
                                "NOAA storm data and is NOT a certified meteorological assessment. It does " +
                                "not constitute a formal inspection, an insurance claim, or a guarantee of " +
                                "damage. A licensed roofing contractor and/or insurance adjuster should " +
                                "physically inspect the property to determine actual damage.")
                                .FontSize(9).FontColor("#78350f").LineHeight(1.5f);
                        });
                    });

                    // ── Footer ───────────────────────────────────────────
                    page.Footer().Background(headerHex).Padding(16).Row(row =>
                    {
                        row.RelativeItem().Column(col =>
                        {
                            col.Item().Text(companyName)
                                .FontSize(10).Bold().FontColor(White);
                            if (!string.IsNullOrWhiteSpace(tagline))
                                col.Item().PaddingTop(1).Text(tagline)
                                    .FontSize(7).FontColor(SlateLight).Italic();
                            if (!string.IsNullOrWhiteSpace(licenseNo))
                                col.Item().PaddingTop(1).Text($"Lic# {licenseNo}")
                                    .FontSize(7).FontColor(SlateLight);
                            col.Item().PaddingTop(2).Text($"Generated {generatedOn}")
                                .FontSize(7).FontColor(SlateLight);
                        });
                        row.ConstantItem(220).AlignRight().AlignMiddle().Column(col =>
                        {
                            if (!string.IsNullOrWhiteSpace(companyPhone))
                                col.Item().AlignRight().PaddingTop(2).Text(companyPhone)
                                    .FontSize(8).FontColor(SlateLight);
                            if (!string.IsNullOrWhiteSpace(companyEmail))
                                col.Item().AlignRight().PaddingTop(1).Text(companyEmail)
                                    .FontSize(7).FontColor(SlateLight);
                            col.Item().AlignRight().PaddingTop(2).Text(companyWeb)
                                .FontSize(8).FontColor(accentHex);
                        });
                    });
                });
            });

            return doc.GeneratePdf();
        }
    }
}
