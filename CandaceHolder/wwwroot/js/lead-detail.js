// lead-detail.js  — per-address detail view

var mapInitialized = false;

// ── Tab switching ─────────────────────────────────────────────────
var tabNames = ['overview', 'contacts', 'activity'];

function showTab(name) {
    tabNames.forEach(function(t) {
        var panel = document.getElementById('panel_' + t);
        var btn   = document.getElementById('tab_' + t);
        if (!panel || !btn) return;
        var active = (t === name);
        panel.classList.toggle('hidden', !active);
        btn.classList.toggle('detail-tab-active', active);
    });

    // Update URL fragment without adding history entry
    history.replaceState(null, '', '#' + name);

    // Lazy-init the Leaflet map the first time the Overview tab is visible
    // (Leaflet can't size itself inside a hidden panel)
    if (name === 'overview' && !mapInitialized) {
        initPropertyMap();
    }
}

// On load: honour URL fragment or default to overview
document.addEventListener('DOMContentLoaded', function() {
    var frag = (location.hash || '#overview').replace('#', '');
    var valid = tabNames.includes(frag) ? frag : 'overview';
    showTab(valid);

    // Keyboard navigation for tabs
    document.querySelectorAll('.detail-tab').forEach(function(btn) {
        btn.addEventListener('keydown', function(e) {
            var tabs = Array.from(document.querySelectorAll('.detail-tab'));
            var idx  = tabs.indexOf(btn);
            if (e.key === 'ArrowRight' && idx < tabs.length - 1) { tabs[idx + 1].focus(); tabs[idx + 1].click(); }
            if (e.key === 'ArrowLeft'  && idx > 0)               { tabs[idx - 1].focus(); tabs[idx - 1].click(); }
        });
    });
});

// ── Leaflet mini-map ──────────────────────────────────────────────
function initPropertyMap() {
    mapInitialized = true;
    var el = document.getElementById('propertyMap');
    if (!el) return;
    var lat = parseFloat(el.dataset.lat);
    var lng = parseFloat(el.dataset.lng);
    if (isNaN(lat) || isNaN(lng)) {
        el.innerHTML = '<div class="flex items-center justify-center h-full text-slate-500 text-sm"><i class="fa-solid fa-map-location-dot mr-2"></i>No coordinates</div>';
        return;
    }
    var map = L.map('propertyMap', { zoomControl: true, scrollWheelZoom: false }).setView([lat, lng], 18);
    L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', {
        attribution: 'Tiles &copy; Esri', maxZoom: 20
    }).addTo(map);
    // World_Imagery has no place names — layer Esri's Boundaries_and_Places
    // reference tiles (transparent, city/place labels only) on top.
    L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}', {
        attribution: 'Labels &copy; Esri', maxZoom: 20
    }).addTo(map);
    L.marker([lat, lng]).addTo(map);
}

// ── Status update ─────────────────────────────────────────────────
async function detailSetStatus(id, value) {
    try {
        var resp = await fetch('/Leads/' + id + '/Status', {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ status: value })
        });
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        showDetailToast('Status updated', true);
        // Recolor the status dropdown (its selected option already changed)
        var badge = document.getElementById('statusBadge');
        if (badge) badge.className = 'status-select ' + detailStatusClass(value);
    } catch(e) {
        showDetailToast('Failed: ' + e.message, false);
    }
}

function detailStatusClass(status) {
    var map = { new:'new', contacted:'contacted', appointment_set:'appt', closed_won:'won', closed_lost:'lost' };
    return 'status-' + (map[status] || 'new');
}

// ── Notes ─────────────────────────────────────────────────────────
var notesSaveTimer = null;

function detailNotesChanged(id) {
    clearTimeout(notesSaveTimer);
    var saveBtn = document.getElementById('notesSaveBtn');
    if (saveBtn) saveBtn.classList.remove('hidden');
}

async function detailSaveNotes(id) {
    var ta   = document.getElementById('detailNotesArea');
    var text = ta ? ta.value.trim() || null : null;
    var btn  = document.getElementById('notesSaveBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin text-xs mr-1"></i>Saving…'; }
    try {
        var resp = await fetch('/Leads/' + id + '/Notes', {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ notes: text })
        });
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        showDetailToast('Notes saved', true);
        if (btn) { btn.classList.add('hidden'); btn.disabled = false; btn.innerHTML = '<i class="fa-solid fa-check text-xs mr-1"></i>Saved'; }
        // Update the notes dot indicator
        var dot = document.getElementById('notesDot');
        if (dot) dot.classList.toggle('hidden', !text);
    } catch(e) {
        showDetailToast('Save failed: ' + e.message, false);
        if (btn) { btn.disabled = false; btn.innerHTML = '<i class="fa-solid fa-check text-xs mr-1"></i>Save Note'; }
    }
}

// ── Skip trace ────────────────────────────────────────────────────
async function detailEnrich(id) {
    var btn  = document.getElementById('enrichBtn');
    if (!btn) return;
    var orig = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin mr-1.5"></i>Tracing…';
    try {
        var resp = await fetch('/Leads/' + id + '/Enrich', { method: 'POST' });
        var r    = await resp.json();
        if (!resp.ok) throw new Error(r.error || 'HTTP ' + resp.status);
        if (r.status === 'completed') {
            showDetailToast('Skip trace complete', true);
            // Reload the page to show fresh contact data
            setTimeout(function() { location.reload(); }, 800);
        } else {
            showDetailToast('No additional data found', false);
            btn.disabled = false;
            btn.innerHTML = orig;
        }
    } catch(e) {
        showDetailToast('Skip trace failed: ' + e.message, false);
        btn.disabled = false;
        btn.innerHTML = orig;
    }
}

// ── Trace again (from Contacts tab) ──────────────────────────────
async function detailReEnrich(id) {
    var btn  = document.getElementById('reEnrichBtn');
    if (!btn) return;
    var orig = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin mr-1.5"></i>Tracing…';
    try {
        var resp = await fetch('/Leads/' + id + '/Enrich', { method: 'POST' });
        var r    = await resp.json();
        if (!resp.ok) throw new Error(r.error || 'HTTP ' + resp.status);
        showDetailToast('Traced — reloading…', true);
        setTimeout(function() { location.reload(); }, 800);
    } catch(e) {
        showDetailToast('Skip trace failed: ' + e.message, false);
        btn.disabled = false;
        btn.innerHTML = orig;
    }
}

// ── Toast ─────────────────────────────────────────────────────────
var _detailToastTimer = null;
function showDetailToast(msg, success) {
    var toast = document.getElementById('toast');
    if (!toast) return;
    if (_detailToastTimer) clearTimeout(_detailToastTimer);
    toast.className = success ? 'success' : 'error';
    document.getElementById('toastIcon').className = 'fa-solid ' + (success ? 'fa-circle-check' : 'fa-circle-xmark');
    document.getElementById('toastMsg').textContent = msg;
    toast.offsetHeight;
    toast.classList.add('show');
    _detailToastTimer = setTimeout(function() { toast.classList.remove('show'); }, 3500);
}
