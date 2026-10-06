// Shared Tailwind CDN theme config for every page.
//
// Must stay a classic (non-module) <script src="..."> loaded AFTER the Tailwind CDN
// <script src="https://cdn.tailwindcss.com"> tag and BEFORE Tailwind scans the page.
//
// LIGHT THEME: the views were originally written for a dark UI (bg-slate-800 cards,
// text-slate-400 secondary text, border-slate-700 dividers...). Rather than rewrite
// every class, the slate scale below is INVERTED — slate-800/900/950 are now near-white
// surfaces and slate-50…400 are dark text shades — so the existing markup renders as a
// light theme. Text that should be dark uses text-slate-50/100 (darkest); text-white is
// reserved for text sitting on a solid color (buttons, logo tiles).
//
// Brand palette (Candace's picks): hot pink primary, teal, and sunny yellow/orange.
tailwind.config = {
    theme: {
        extend: {
            colors: {
                slate: {
                    50:  '#1f1235',   // headings / primary text (deep plum-ink)
                    100: '#2a1a40',
                    200: '#3b2d52',
                    300: '#4a3f5e',   // body text
                    400: '#625a73',   // secondary text
                    500: '#7c7590',   // muted text, placeholders
                    600: '#b3aec2',   // faint text, strong borders
                    700: '#ece6f3',   // borders, secondary buttons
                    800: '#ffffff',   // cards
                    900: '#fbf8fd',   // inputs, inset panels
                    950: '#ffffff'
                },
                navy:  { DEFAULT: '#ffffff', 700: '#ffffff', 600: '#f6f1fa', 500: '#ece6f3' },
                brand: { DEFAULT: '#e11d74', dark: '#be185d', light: '#f9a8d4' },
                teal:  { DEFAULT: '#0d9488' },
                sunny: { DEFAULT: '#f59e0b', light: '#fde68a' }
            },
            fontFamily: {
                sans: ['Nunito', 'ui-sans-serif', 'system-ui', 'sans-serif']
            }
        }
    }
};
