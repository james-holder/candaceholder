 // Internal escapeAttr — keeps this file self-contained (no dependency on site.js or saved-leads.js)
function _cwEscape(s) {
    if (s === null || s === undefined) return '';
    return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

/**
 * claim-window.js
 * ──────────────────────────────────────────────────────────────────
 * Claim window countdown for hail / storm damage insurance claims.
 *
 * Each state has a different statute of limitations (or policy-mandated
 * deadline) for homeowners to file a property insurance claim after a
 * storm event.  This table reflects the most applicable deadline —
 * typically the state insurance code deadline, or the general property-
 * damage SOL where no specific insurance rule applies.
 *
 * Sources: state insurance codes, NAIC, United Policyholders, and
 * state-specific case law.  Consult a licensed public adjuster or
 * attorney for the most current ruling in a specific state.
 *
 * Last reviewed: April 2026
 */

const CLAIM_WINDOW_YEARS = {
    AL: { years: 2,  note: 'AL Code § 6-2-38 — 2-year property damage SOL' },
    AK: { years: 3,  note: 'AS § 09.10.053 — 3-year property damage SOL' },
    AZ: { years: 2,  note: 'ARS § 12-542 — 2-year property damage SOL' },
    AR: { years: 5,  note: 'AR Code § 16-56-111 — 5-year written contract SOL' },
    CA: { years: 2,  note: 'CA CCP § 335.1 / Insurance Code § 2071 — 2-year suit deadline' },
    CO: { years: 2,  note: 'CRS § 13-80-102 — 2-year SOL; applies specifically to hail claims' },
    CT: { years: 2,  note: 'CGS § 52-584 — 2-year property damage SOL' },
    DE: { years: 2,  note: '10 Del. C. § 8107 — 2-year property damage SOL' },
    FL: { years: 1,  note: 'FL § 627.70132 — 1-year deadline for windstorm / hail claims (2023 reform)' },
    GA: { years: 2,  note: 'OCGA § 9-3-33 — 2-year property damage SOL' },
    HI: { years: 2,  note: 'HRS § 657-7 — 2-year property damage SOL' },
    ID: { years: 2,  note: 'IC § 5-219 — 2-year property damage SOL' },
    IL: { years: 2,  note: '735 ILCS 5/13-214 / standard policy provision — 2-year suit deadline' },
    IN: { years: 2,  note: 'IC § 34-11-2-4 — 2-year property damage SOL' },
    IA: { years: 5,  note: 'Iowa Code § 614.1(4) — 5-year written contract SOL' },
    KS: { years: 5,  note: 'KSA § 60-511 — 5-year written contract SOL' },
    KY: { years: 2,  note: 'KRS § 413.125 — 2-year property damage SOL' },
    LA: { years: 1,  note: 'LA CC Art. 3492 — 1-year delictual prescription; shortest in the US' },
    ME: { years: 6,  note: '14 MRS § 752 — 6-year written contract SOL' },
    MD: { years: 3,  note: 'MD Code CJ § 5-101 — 3-year general SOL' },
    MA: { years: 2,  note: 'MGL c. 175 § 99 — 2-year limit mandated on all property policies' },
    MI: { years: 1,  note: 'MCL § 500.2836 — standard policy may require suit within 1 year of loss' },
    MN: { years: 1,  note: 'Minn. Stat. § 65A.01 — 1-year carve-out specifically for hail claims' },
    MS: { years: 3,  note: 'Miss. Code § 15-1-49 — 3-year property damage SOL' },
    MO: { years: 5,  note: 'Mo. Rev. Stat. § 516.120 — 5-year written contract SOL' },
    MT: { years: 2,  note: 'MCA § 27-2-204 — 2-year property damage SOL' },
    NE: { years: 4,  note: 'Neb. Rev. Stat. § 25-207 — 4-year property damage SOL' },
    NV: { years: 3,  note: 'NRS § 11.190(3) — 3-year property damage SOL' },
    NH: { years: 3,  note: 'RSA § 508:4 — 3-year general SOL' },
    NJ: { years: 2,  note: 'NJSA § 2A:14-1.1 / standard policy — 2-year suit deadline' },
    NM: { years: 6,  note: 'NMSA § 37-1-3 — 6-year written contract SOL' },
    NY: { years: 2,  note: 'NY Insurance Law § 3404 — standard form limits suit to 2 years' },
    NC: { years: 3,  note: 'NCGS § 1-52(16) — 3-year property damage SOL' },
    ND: { years: 6,  note: 'NDCC § 28-01-16 — 6-year written contract SOL' },
    OH: { years: 2,  note: 'ORC § 2305.10 — 2-year property damage SOL' },
    OK: { years: 5,  note: '12 OS § 95 — 5-year written contract SOL' },
    OR: { years: 2,  note: 'ORS § 742.240 — within 2 years after date of loss' },
    PA: { years: 2,  note: '42 Pa. C.S. § 5524 — 2-year property damage SOL' },
    RI: { years: 3,  note: 'RIGL § 9-1-14 — 3-year property damage SOL' },
    SC: { years: 3,  note: 'SC Code § 15-3-530 — 3-year general SOL' },
    SD: { years: 6,  note: 'SDCL § 15-2-13 — 6-year written contract SOL' },
    TN: { years: 2,  note: 'TCA § 28-3-105 — 2-year property damage SOL' },
    TX: { years: 1,  note: 'TX Insurance Code § 542A.003 — 1-year deadline from date of loss (HB 1774, 2017)' },
    UT: { years: 3,  note: 'UCA § 78B-2-305 — 3-year property damage SOL' },
    VT: { years: 3,  note: '12 VSA § 512 — 3-year property damage SOL' },
    VA: { years: 5,  note: 'VA Code § 8.01-246 — 5-year written contract SOL' },
    WA: { years: 1,  note: 'RCW § 48.18.200 — standard policy requires suit within 1 year of loss' },
    WV: { years: 2,  note: 'WV Code § 55-2-12 — 2-year property damage SOL' },
    WI: { years: 1,  note: 'Wis. Stat. § 631.83 — 1-year limit on suit under property policies' },
    WY: { years: 4,  note: 'WY Stat. § 1-3-105 — 4-year property damage SOL' },
    DC: { years: 3,  note: 'DC Code § 12-301 — 3-year general SOL' },
};

/**
 * Parse a 2-letter US state abbreviation from an address string.
 * Handles formats like:
 *   "1234 Main St, Dallas, TX 75201"
 *   "1234 Main St, Dallas, TX"
 *   "Dallas TX 75201"
 */
function parseStateFromAddress(address) {
    if (!address) return null;
    // Primary: ", ST 12345" or ", ST" at end
    var m = address.match(/,\s*([A-Z]{2})(?:\s+\d{5}(?:-\d{4})?)?(?:\s*,.*)?$/i);
    if (m) return m[1].toUpperCase();
    // Fallback: space-delimited "ST 12345" near end
    m = address.match(/\b([A-Z]{2})\s+\d{5}(-\d{4})?\s*$/i);
    if (m) return m[1].toUpperCase();
    return null;
}

/**
 * Parse a date string like "2024-03-14" or "March 14, 2024" or "03/14/2024"
 * Returns a Date object or null.
 */
function parseStormDate(dateStr) {
    if (!dateStr) return null;
    // Try ISO format first
    var d = new Date(dateStr);
    if (!isNaN(d.getTime())) return d;
    // Try US format MM/DD/YYYY
    var m = dateStr.match(/^(\d{1,2})\/(\d{1,2})\/(\d{4})$/);
    if (m) return new Date(parseInt(m[3]), parseInt(m[1]) - 1, parseInt(m[2]));
    return null;
}

/**
 * Calculate the claim window for a given lead.
 *
 * @param {string} stormDateStr - The last storm date string
 * @param {string} address      - Full address (used to extract state)
 * @returns {object|null}  { deadlineDate, daysLeft, totalDays, pct, state, years, note,
 *                           expired, urgent, warning, healthy }
 *         Returns null if state can't be determined or no storm date.
 */
function getClaimWindow(stormDateStr, address) {
    var stormDate = parseStormDate(stormDateStr);
    if (!stormDate) return null;

    var state = parseStateFromAddress(address);
    if (!state) return null;

    var entry = CLAIM_WINDOW_YEARS[state];
    if (!entry) return null;

    var deadlineDate = new Date(stormDate);
    deadlineDate.setFullYear(deadlineDate.getFullYear() + entry.years);

    var now = new Date();
    now.setHours(0, 0, 0, 0);

    var msLeft    = deadlineDate.getTime() - now.getTime();
    var daysLeft  = Math.ceil(msLeft / (1000 * 60 * 60 * 24));
    var totalDays = Math.round(entry.years * 365.25);
    var pct       = Math.max(0, Math.min(100, (daysLeft / totalDays) * 100));

    return {
        deadlineDate : deadlineDate,
        daysLeft     : daysLeft,
        totalDays    : totalDays,
        pct          : pct,
        state        : state,
        years        : entry.years,
        note         : entry.note,
        expired      : daysLeft <= 0,
        urgent       : daysLeft > 0 && daysLeft <= 30,
        warning      : daysLeft > 30  && daysLeft <= 90,
        healthy      : daysLeft > 90,
    };
}

/**
 * Build the HTML for the claim countdown badge that sits under the storm date.
 */
function buildClaimBadge(stormDateStr, address) {
    var cw = getClaimWindow(stormDateStr, address);
    if (!cw) return '';

    var tooltip = cw.note + ' — Deadline: ' + cw.deadlineDate.toLocaleDateString('en-US', { month:'short', day:'numeric', year:'numeric' });

    if (cw.expired) {
        return '<span class="claim-badge claim-expired" title="' + _cwEscape(tooltip) + '">' +
               '<i class="fa-solid fa-clock-rotate-left mr-0.5"></i>Expired</span>';
    }

    var cls, icon;
    if (cw.urgent)       { cls = 'claim-urgent';  icon = 'fa-circle-exclamation'; }
    else if (cw.warning) { cls = 'claim-warning'; icon = 'fa-triangle-exclamation'; }
    else                 { cls = 'claim-healthy'; icon = 'fa-clock'; }

    var label = cw.daysLeft >= 365
        ? Math.round(cw.daysLeft / 30.4) + ' mo left'
        : cw.daysLeft + 'd left';

    return '<span class="claim-badge ' + cls + '" title="' + _cwEscape(tooltip) + '">' +
           '<i class="fa-solid ' + icon + ' mr-0.5"></i>' + label + '</span>';
}
