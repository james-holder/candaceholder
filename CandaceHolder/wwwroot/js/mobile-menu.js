// Mobile hamburger nav toggle. Extracted 2026-07-17 — this function (or a near-identical
// variant) was duplicated inline across several views (Dashboard/Index, Account/Profile,
// Leads/Detail, Team/Index, Company/Settings). This is the canonical version, with the
// null-guard some copies were missing.
function toggleMobileMenu() {
    var menu = document.getElementById('mobileMenu');
    var icon = document.getElementById('mobileMenuIcon');
    if (!menu) return;
    var nowHidden = menu.classList.toggle('hidden');
    if (icon) icon.className = nowHidden ? 'fa-solid fa-bars text-lg' : 'fa-solid fa-xmark text-lg';
}
