// Page logic for Views/Alerts/Index.cshtml — watch-area list, alert history, add/toggle/
// delete area actions, and the test-email button. Extracted 2026-07-17 from an inline
// <script> block. No Razor dependencies — pure JS, safe as a static file.

// ── State ─────────────────────────────────────────────────────────────────
let areas   = [];
let history = [];

// ── Init ──────────────────────────────────────────────────────────────────
document.addEventListener('DOMContentLoaded', () => {
    loadAreas();
    loadHistory();

    document.getElementById('areaAddress').addEventListener('keydown', e => {
        if (e.key === 'Enter') addArea();
    });
});

// ── Load watched areas ────────────────────────────────────────────────────
async function loadAreas() {
    try {
        const resp = await fetch('/Alerts/List');
        areas = await resp.json();
        renderAreas();
    } catch (e) {
        document.getElementById('areasList').innerHTML =
            '<p class="text-red-400 text-sm">Failed to load watched areas.</p>';
    }
}

function renderAreas() {
    const el = document.getElementById('areasList');
    if (!areas.length) {
        el.innerHTML = `
            <div class="bg-navy-700 border border-dashed border-slate-700 rounded-xl p-8 text-center">
                <i class="fa-solid fa-map-location-dot text-slate-600 text-3xl mb-3 block"></i>
                <p class="text-slate-400 text-sm font-medium">No watch areas yet</p>
                <p class="text-slate-600 text-xs mt-1">Add a city or zip code above to start getting storm alerts.</p>
            </div>`;
        return;
    }

    el.innerHTML = areas.map(a => `
        <div class="bg-navy-700 border border-slate-700/60 rounded-xl p-4 mb-3 flex flex-col sm:flex-row sm:items-center gap-3" id="area-${a.id}">
            <div class="flex items-start gap-3 flex-1 min-w-0">
                <div class="w-9 h-9 rounded-lg flex-shrink-0 flex items-center justify-center ${a.alertsEnabled ? 'bg-brand/15 text-brand' : 'bg-slate-700 text-slate-500'}">
                    <i class="fa-solid fa-bell text-sm"></i>
                </div>
                <div class="min-w-0">
                    <p class="text-white font-medium text-sm truncate">${escHtml(a.label)}</p>
                    <p class="text-slate-400 text-xs mt-0.5">
                        ${a.radiusMiles}-mile radius &nbsp;·&nbsp; ${hailThresholdLabel(a.minHailSizeInches)}+ hail
                    </p>
                </div>
            </div>
            <div class="flex items-center gap-2 flex-shrink-0">
                <!-- Toggle alerts -->
                <button onclick="toggleAlert(${a.id}, ${!a.alertsEnabled})" title="${a.alertsEnabled ? 'Pause alerts' : 'Resume alerts'}"
                    class="h-8 px-3 rounded-lg text-xs font-medium border transition
                    ${a.alertsEnabled
                        ? 'bg-emerald-500/10 text-emerald-400 border-emerald-500/30 hover:bg-emerald-500/20'
                        : 'bg-slate-700 text-slate-400 border-slate-600 hover:bg-slate-600'}">
                    <i class="fa-solid ${a.alertsEnabled ? 'fa-bell' : 'fa-bell-slash'} mr-1 text-xs"></i>
                    ${a.alertsEnabled ? 'Active' : 'Paused'}
                </button>
                <!-- Search this area now -->
                <a href="/?address=${encodeURIComponent(a.label)}&radius=${a.radiusMiles}"
                    title="Search this area now"
                    class="w-8 h-8 rounded-lg flex items-center justify-center bg-slate-700 hover:bg-slate-600 text-slate-400 hover:text-brand border border-slate-600 transition">
                    <i class="fa-solid fa-magnifying-glass text-xs"></i>
                </a>
                <!-- Delete -->
                <button onclick="deleteArea(${a.id})" title="Remove watch area"
                    class="w-8 h-8 rounded-lg flex items-center justify-center bg-slate-700 hover:bg-red-500/20 text-slate-400 hover:text-red-400 border border-slate-600 hover:border-red-500/30 transition">
                    <i class="fa-solid fa-trash text-xs"></i>
                </button>
            </div>
        </div>`).join('');
}

// ── Load alert history ────────────────────────────────────────────────────
async function loadHistory() {
    try {
        const resp = await fetch('/Alerts/History');
        history = await resp.json();
        renderHistory();
    } catch (e) {
        document.getElementById('historyList').innerHTML =
            '<p class="text-red-400 text-sm">Failed to load history.</p>';
    }
}

function renderHistory() {
    const el = document.getElementById('historyList');
    if (!history.length) {
        el.innerHTML = '<p class="text-slate-600 text-sm text-center py-4">No alerts sent yet.</p>';
        return;
    }
    el.innerHTML = `
        <div class="bg-navy-700 border border-slate-700/60 rounded-xl overflow-hidden">
            <table class="w-full text-sm">
                <thead>
                    <tr class="border-b border-slate-700/60 text-left">
                        <th class="px-4 py-3 text-xs font-semibold text-slate-400 uppercase tracking-wider">Area</th>
                        <th class="px-4 py-3 text-xs font-semibold text-slate-400 uppercase tracking-wider">Event Date</th>
                        <th class="px-4 py-3 text-xs font-semibold text-slate-400 uppercase tracking-wider">Hail Size</th>
                        <th class="px-4 py-3 text-xs font-semibold text-slate-400 uppercase tracking-wider hidden sm:table-cell">Alert Sent</th>
                    </tr>
                </thead>
                <tbody>
                    ${history.map(h => `
                        <tr class="border-b border-slate-700/40 last:border-0 hover:bg-slate-700/20 transition">
                            <td class="px-4 py-3 text-slate-200 max-w-[160px] truncate">${escHtml(h.areaLabel)}</td>
                            <td class="px-4 py-3 text-slate-300">${fmtDate(h.eventDate)}</td>
                            <td class="px-4 py-3">
                                <span class="text-orange-400 font-medium">${h.hailSizeInches.toFixed(2)}"</span>
                                <span class="text-slate-500 text-xs ml-1">(${hailLabel(h.hailSizeInches)})</span>
                            </td>
                            <td class="px-4 py-3 text-slate-500 text-xs hidden sm:table-cell">${fmtDate(h.sentAt)}</td>
                        </tr>`).join('')}
                </tbody>
            </table>
        </div>`;
}

// ── Actions ───────────────────────────────────────────────────────────────
async function addArea() {
    const address = document.getElementById('areaAddress').value.trim();
    const radius  = parseFloat(document.getElementById('areaRadius').value);
    const minHail = parseFloat(document.getElementById('areaMinHail').value);
    const errEl   = document.getElementById('addAreaError');
    const btn     = document.getElementById('addAreaBtn');

    if (!address) { showError(errEl, 'Please enter a city, zip code, or address.'); return; }
    errEl.classList.add('hidden');

    btn.disabled = true;
    btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin text-xs"></i><span>Adding…</span>';

    try {
        const resp = await fetch('/Alerts', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify({ address, radiusMiles: radius, minHailSizeInches: minHail })
        });
        const data = await resp.json();
        if (!resp.ok) { showError(errEl, data.error || 'Failed to add area.'); return; }

        areas.unshift(data);
        renderAreas();
        document.getElementById('areaAddress').value = '';
        showToast('Watch area added — you\'ll be alerted when hail hits ' + data.label, true);
    } catch (e) {
        showError(errEl, 'Network error — please try again.');
    } finally {
        btn.disabled = false;
        btn.innerHTML = '<i class="fa-solid fa-plus text-xs"></i><span>Add Area</span>';
    }
}

async function toggleAlert(id, enabled) {
    await fetch('/Alerts/' + id, {
        method:  'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body:    JSON.stringify({ alertsEnabled: enabled })
    });
    const area = areas.find(a => a.id === id);
    if (area) area.alertsEnabled = enabled;
    renderAreas();
    showToast(enabled ? 'Alerts resumed' : 'Alerts paused', true);
}

async function deleteArea(id) {
    if (!confirm('Remove this watch area? You will no longer receive alerts for it.')) return;
    await fetch('/Alerts/' + id, { method: 'DELETE' });
    areas = areas.filter(a => a.id !== id);
    renderAreas();
    showToast('Watch area removed', true);
}

// ── Helpers ───────────────────────────────────────────────────────────────
function hailThresholdLabel(inches) {
    if (inches >= 1.75) return '⛳ Golf Ball';
    if (inches >= 1.50) return '🏓 Ping Pong';
    if (inches >= 1.25) return '🪙 Half Dollar';
    if (inches >= 1.00) return '🪙 Quarter';
    return '🪙 Penny';
}

function hailLabel(inches) {
    if (inches >= 4.00) return 'Softball';
    if (inches >= 2.75) return 'Baseball';
    if (inches >= 2.50) return 'Tennis Ball';
    if (inches >= 2.00) return 'Hen Egg';
    if (inches >= 1.75) return 'Golf Ball';
    if (inches >= 1.50) return 'Ping Pong';
    if (inches >= 1.25) return 'Half Dollar';
    if (inches >= 1.00) return 'Quarter';
    if (inches >= 0.88) return 'Nickel';
    if (inches >= 0.75) return 'Penny';
    return 'Pea';
}

function fmtDate(iso) {
    if (!iso) return '—';
    const d = new Date(iso);
    return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
}

function escHtml(s) {
    return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

function showError(el, msg) {
    el.textContent = msg;
    el.classList.remove('hidden');
}

function showToast(msg, ok) {
    const t = document.getElementById('toast');
    t.textContent = msg;
    t.className = 'fixed bottom-5 right-5 z-50 px-4 py-3 rounded-xl text-sm font-medium shadow-xl border transition-all '
        + (ok ? 'bg-emerald-900/80 text-emerald-300 border-emerald-700' : 'bg-red-900/80 text-red-300 border-red-700');
    t.classList.remove('hidden');
    setTimeout(() => t.classList.add('hidden'), 3500);
}

// ── Test email ────────────────────────────────────────────────────────────
async function sendTestEmail() {
    const btn  = document.getElementById('testEmailBtn');
    const orig = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin text-xs"></i> Sending…';
    console.log('[StormLead] Sending test alert email…');

    try {
        const resp = await fetch('/Alerts/TestEmail');
        const data = await resp.json();

        if (resp.ok) {
            console.log('[StormLead] Test email sent successfully:', data.message);
            showToast('✓ ' + data.message, true);
        } else {
            console.error('[StormLead] Test email failed:', data.error);
            showToast('✗ ' + (data.error || 'Failed to send test email'), false);
        }
    } catch (e) {
        console.error('[StormLead] Test email network error:', e);
        showToast('✗ Network error — check the console', false);
    } finally {
        btn.disabled = false;
        btn.innerHTML = orig;
    }
}
