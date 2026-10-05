// Account menu dropdown/drawer toggles. Extracted 2026-07-17 from inline <script> blocks in
// Views/Shared/_AccountMenu.cshtml (desktop dropdown) and _AccountMenuMobile.cshtml (mobile
// drawer accordion). Both partials are only rendered when the user is authenticated, but
// these functions are harmless to define globally on every page regardless.

function toggleAccountMenu() {
    var menu = document.getElementById('accountMenu');
    if (menu) menu.classList.toggle('hidden');
}
document.addEventListener('click', function (e) {
    var menu = document.getElementById('accountMenu');
    var btn  = document.getElementById('accountMenuBtn');
    if (!menu || menu.classList.contains('hidden')) return;
    if (!menu.contains(e.target) && btn && !btn.contains(e.target)) {
        menu.classList.add('hidden');
    }
});

function toggleAccountMenuMobile() {
    var menu = document.getElementById('accountMenuMobile');
    var icon = document.getElementById('accountMenuIconMobile');
    if (!menu) return;
    var nowHidden = menu.classList.toggle('hidden');
    if (nowHidden) {
        menu.classList.remove('flex');
    } else {
        menu.classList.add('flex');
    }
    if (icon) icon.className = nowHidden ? 'fa-solid fa-chevron-down text-xs text-slate-500' : 'fa-solid fa-chevron-up text-xs text-slate-500';
}
