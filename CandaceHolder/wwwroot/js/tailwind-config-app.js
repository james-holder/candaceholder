// Shared Tailwind CDN theme config for the app's internal (dark navy) pages.
// Extracted 2026-07-17 from ~12 nearly-identical inline <script> blocks that had drifted
// slightly apart (some only defined navy.DEFAULT/700, or brand as a bare string instead of
// an object) — this is the richest superset of every variant seen, which is safe: Tailwind's
// CDN JIT only generates a utility class for keys actually referenced in a page's markup, so
// extra unused shades here are inert on pages that don't use them.
//
// Must stay a classic (non-module) <script src="..."> loaded AFTER the Tailwind CDN
// <script src="https://cdn.tailwindcss.com"> tag and BEFORE Tailwind scans the page —
// same position the inline block used to occupy in each view's <head>.
//
// NOT used by Views/Home/Landing.cshtml or Views/Help/Index.cshtml — those two also set a
// custom `fontFamily` (Inter) and have their own slightly different color sets, so they keep
// their own dedicated config files (tailwind-config-landing.js / tailwind-config-help.js)
// rather than being folded in here, to avoid changing their typography.
tailwind.config = {
    theme: {
        extend: {
            colors: {
                navy:  { DEFAULT: '#0f172a', 700: '#1e293b', 600: '#334155', 500: '#475569' },
                brand: { DEFAULT: '#f97316', dark: '#ea6c0a', light: '#fdba74' }
            }
        }
    }
};
