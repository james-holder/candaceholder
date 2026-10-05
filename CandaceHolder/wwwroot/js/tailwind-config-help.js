// Tailwind CDN theme config for Views/Help/Index.cshtml.
// Extracted 2026-07-17, content unchanged from the original inline <script> block. Kept
// separate from tailwind-config-app.js because this page also sets a custom Inter font
// family (like the marketing landing page) and a slightly different navy shade set.
tailwind.config = {
    theme: {
        extend: {
            colors: {
                brand: '#f97316',
                navy: { DEFAULT: '#0f172a', 700: '#1e293b', 600: '#334155' }
            },
            fontFamily: { sans: ['Inter', 'system-ui', 'sans-serif'] }
        }
    }
};
