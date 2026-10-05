// Tailwind CDN theme config for the public marketing landing page (Views/Home/Landing.cshtml).
// Extracted 2026-07-17, content unchanged from the original inline <script> block. Kept
// separate from tailwind-config-app.js because this page uses a different, simpler color
// set (no navy) and a custom Inter font family the internal app pages don't use.
tailwind.config = {
    theme: {
        extend: {
            colors: { brand: '#f97316' },
            fontFamily: { sans: ['Inter', 'system-ui', 'sans-serif'] }
        }
    }
};
