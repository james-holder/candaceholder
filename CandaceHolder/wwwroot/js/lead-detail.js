// lead-detail.js  — per-address detail view

var mapInitialized = false;

// ── Tab switching ─────────────────────────────────────────────────
var tabNames = ['overview', 'property', 'contacts', 'activity'];

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

    // Lazy-init the Leaflet map the first time Property tab opens
    if (name === 'property' && !mapInitialized) {
        initPropertyMap();
    }
}

// On load: honour URL fragment or default to overview
document.addEventListener('DOMContentLoaded', function() {
    var frag = (location.hash || '#overview').replace('#', '');
    var valid = tabNames.includes(frag) ? frag : 'overview';
    showTab(valid);

    // Populate the claim countdown card
    renderClaimCountdown();

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

// ── Claim countdown ───────────────────────────────────────────────
function renderClaimCountdown() {
    var card = document.getElementById('claimCountdownCard');
    if (!card) return;

    var dateStr = card.dataset.stormDate;
    var address = card.dataset.address;
    if (!dateStr || dateStr === '' || dateStr === 'No data') {
        card.innerHTML = '<p class="text-slate-500 text-sm">No storm date recorded — scan this address to check for events.</p>';
        return;
    }

    var stateInfo   = getStateClaimWindow(address);
    var stormDate   = new Date(dateStr);
    var deadlineMs  = stormDate.getTime() + stateInfo.years * 365.25 * 24 * 60 * 60 * 1000;
    var daysLeft    = Math.ceil((deadlineMs - Date.now()) / (24 * 60 * 60 * 1000));
    var deadlineStr = new Date(deadlineMs).toLocaleDateString('en-US', { month:'short', day:'numeric', year:'numeric' });

    var isExpired = daysLeft <= 0;
    var isUrgent  = !isExpired && daysLeft <= 30;

    var cardClass = isExpired ? 'claim-expired' : isUrgent ? 'claim-urgent' : '';
    var daysClass = isExpired ? 'expired' : isUrgent ? 'urgent' : '';
    var icon      = isExpired ? 'fa-clock-rotate-left' : isUrgent ? 'fa-triangle-exclamation' : 'fa-clock';
    var iconColor = isExpired ? 'text-slate-500' : isUrgent ? 'text-red-400' : 'text-brand';

    card.className = 'claim-card ' + cardClass;

    if (isExpired) {
        card.innerHTML =
            '<div class="flex items-center gap-3 mb-2">' +
            '<i class="fa-solid ' + icon + ' ' + iconColor + '"></i>' +
            '<span class="text-sm font-semibold text-slate-400">Claim Window Expired</span></div>' +
            '<p class="text-slate-500 text-xs">The ' + stateInfo.years + '-year filing window in ' + stateInfo.stateName + ' closed on ' + deadlineStr + '.</p>';
    } else {
        card.innerHTML =
            '<div class="flex items-start justify-between gap-4">' +
            '<div>' +
            '<div class="claim-days ' + daysClass + '">' + daysLeft + '</div>' +
            '<div class="text-sm font-semibold text-slate-300 mt-0.5">days left to file</div>' +
            '<div class="text-xs text-slate-500 mt-1">' + stateInfo.stateName + ' · ' + stateInfo.years + '-yr statute · deadline ' + deadlineStr + '</div>' +
            '</div>' +
            '<i class="fa-solid ' + icon + ' text-2xl ' + iconColor + ' mt-1 opacity-70"></i>' +
            '</div>';
    }
}

// Delegate to the CLAIM_WINDOW_YEARS table in claim-window.js (already loaded on this page)
// so there's a single source of truth for every state's filing window.
var STATE_NAMES = {
    AL:'Alabama',AK:'Alaska',AZ:'Arizona',AR:'Arkansas',CA:'California',CO:'Colorado',
    CT:'Connecticut',DE:'Delaware',FL:'Florida',GA:'Georgia',HI:'Hawaii',ID:'Idaho',
    IL:'Illinois',IN:'Indiana',IA:'Iowa',KS:'Kansas',KY:'Kentucky',LA:'Louisiana',
    ME:'Maine',MD:'Maryland',MA:'Massachusetts',MI:'Michigan',MN:'Minnesota',MS:'Mississippi',
    MO:'Missouri',MT:'Montana',NE:'Nebraska',NV:'Nevada',NH:'New Hampshire',NJ:'New Jersey',
    NM:'New Mexico',NY:'New York',NC:'North Carolina',ND:'North Dakota',OH:'Ohio',OK:'Oklahoma',
    OR:'Oregon',PA:'Pennsylvania',RI:'Rhode Island',SC:'South Carolina',SD:'South Dakota',
    TN:'Tennessee',TX:'Texas',UT:'Utah',VT:'Vermont',VA:'Virginia',WA:'Washington',
    WV:'West Virginia',WI:'Wisconsin',WY:'Wyoming',DC:'Washington D.C.'
};
function getStateClaimWindow(address) {
    var code = parseStateFromAddress(address);   // from claim-window.js
    var entry = code && CLAIM_WINDOW_YEARS[code]; // from claim-window.js
    if (entry) return { years: entry.years, stateName: STATE_NAMES[code] || code };
    return { years: 2, stateName: 'this state' };
}

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
        if (resp.status === 402) { window.location.href = '/Billing/Upgrade'; return; }
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        showDetailToast('Status updated', true);
        // Update the status badge in the hero
        var badge = document.getElementById('statusBadge');
        if (badge) {
            var labels = { new:'New', contacted:'Contacted', appointment_set:'Appt Set', closed_won:'Won ✓', closed_lost:'Lost' };
            badge.textContent = labels[value] || value;
            badge.className = 'status-select ' + detailStatusClass(value);
        }
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

// ── Enrich ────────────────────────────────────────────────────────
async function detailEnrich(id) {
    var btn  = document.getElementById('enrichBtn');
    if (!btn) return;
    var orig = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin mr-1.5"></i>Enriching…';
    try {
        var resp = await fetch('/Leads/' + id + '/Enrich', { method: 'POST' });
        var r    = await resp.json();
        if (!resp.ok) throw new Error(r.error || 'HTTP ' + resp.status);
        if (r.status === 'completed') {
            showDetailToast('Enriched successfully', true);
            // Reload the page to show fresh contact data
            setTimeout(function() { location.reload(); }, 800);
        } else {
            showDetailToast('No additional data found', false);
            btn.disabled = false;
            btn.innerHTML = orig;
        }
    } catch(e) {
        showDetailToast('Enrichment failed: ' + e.message, false);
        btn.disabled = false;
        btn.innerHTML = orig;
    }
}

// ── Re-enrich (from Contacts tab) ────────────────────────────────
async function detailReEnrich(id) {
    var btn  = document.getElementById('reEnrichBtn');
    if (!btn) return;
    var orig = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin mr-1.5"></i>Re-enriching…';
    try {
        var resp = await fetch('/Leads/' + id + '/Enrich', { method: 'POST' });
        var r    = await resp.json();
        if (!resp.ok) throw new Error(r.error || 'HTTP ' + resp.status);
        showDetailToast('Re-enriched — reloading…', true);
        setTimeout(function() { location.reload(); }, 800);
    } catch(e) {
        showDetailToast('Re-enrich failed: ' + e.message, false);
        btn.disabled = false;
        btn.innerHTML = orig;
    }
}

// ── Hail label ────────────────────────────────────────────────────
function hailLabel(raw) {
    var n = parseFloat(raw);
    if (isNaN(n) || n <= 0) return null;
    if      (n < 0.75) return { label:'Pea',         cls:'text-yellow-500' };
    else if (n < 0.88) return { label:'Penny',        cls:'text-yellow-400' };
    else if (n < 1.00) return { label:'Nickel',       cls:'text-yellow-400' };
    else if (n < 1.25) return { label:'Quarter',      cls:'text-orange-400' };
    else if (n < 1.50) return { label:'Half Dollar',  cls:'text-orange-400' };
    else if (n < 1.75) return { label:'Ping Pong',    cls:'text-orange-500' };
    else if (n < 2.00) return { label:'Golf Ball',    cls:'text-red-400'    };
    else if (n < 2.50) return { label:'Hen Egg',      cls:'text-red-400'    };
    else if (n < 2.75) return { label:'Tennis Ball',  cls:'text-red-500'    };
    else if (n < 4.00) return { label:'Baseball',     cls:'text-red-500'    };
    else               return { label:'Softball',     cls:'text-red-600'    };
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
