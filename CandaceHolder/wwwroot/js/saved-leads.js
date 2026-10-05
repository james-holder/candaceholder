// saved-leads.js
let allLeads      = [];
let sortCol       = 'riskLevel';
// riskOrder (see getSortValue) ranks High=0, Medium=1, Low=2, so 'asc' sorts
// High -> Medium -> Low, i.e. Low sinks to the bottom of the list by default.
let sortDir       = 'asc';
let activeFilter  = 'all';
let activeTab     = 'pipeline';   // 'pipeline' | 'closed' | 'archived'
let selectedIds   = new Set();
let editingId      = null;
let editingNotesId = null;
let viewingContactsId  = null;
let viewingStormHistoryId = null;
let stormHistoryCache = {};   // id → {hail:[...], wind:[...]} or {error:'...'}
let showWindIds = new Set();  // ids where wind panel is expanded
let canEnrich     = false;   // set from /Leads/Stats — owners/managers only

document.addEventListener('DOMContentLoaded', function() { loadLeads(); refreshTabCounts(); });

// ── Tab switching ─────────────────────────────────────────────────
function switchLeadTab(tab) {
    activeTab = tab;
    selectedIds.clear();
    activeFilter = 'all';
    editingId = null;
    editingNotesId = null;
    viewingContactsId = null;
    viewingStormHistoryId = null;
    showWindIds.clear();

    document.getElementById('tabPipeline').classList.toggle('lead-tab-active',  tab === 'pipeline');
    document.getElementById('tabClosed').classList.toggle('lead-tab-active',    tab === 'closed');
    document.getElementById('tabArchived').classList.toggle('lead-tab-active',   tab === 'archived');

    // Bulk toolbar & checkbox visibility - checkboxes on all active tabs, not archived
    document.getElementById('bulkToolbar').classList.add('hidden');
    var thCb = document.getElementById('thCheckbox');
    if (thCb) thCb.classList.toggle('hidden', tab === 'archived');
    var selectAll = document.getElementById('selectAll');
    if (selectAll) { selectAll.checked = false; selectAll.indeterminate = false; }

    loadLeads();
}

// ── Data loading ──────────────────────────────────────────────────
async function loadLeads() {
    setLoading(true);
    try {
        const resp = await fetch('/Leads?tab=' + activeTab, { cache: 'no-store' });
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        allLeads = await resp.json();
        updateTabCounts();
        renderTable();
    } catch (e) {
        setLoading(false);
        document.getElementById('leadsBody').innerHTML =
            '<tr><td colspan="7" class="text-center text-red-400 py-8 text-sm px-4">' +
            '<i class="fa-solid fa-triangle-exclamation mr-2"></i>Failed to load: ' + escapeHtml(e.message) + '</td></tr>';
    }
}

async function refreshTabCounts() {
    try {
        const r = await fetch('/Leads/Stats');
        if (!r.ok) return;
        const s = await r.json();
        var uel = document.getElementById('tabUnenrichedCount');
        var pel = document.getElementById('tabPipelineCount');
        var cel = document.getElementById('tabClosedCount');
        var ael = document.getElementById('tabArchivedCount');
        if (uel) uel.textContent = s.unenrichedCount ?? '';
        if (pel) pel.textContent = s.pipelineCount   ?? '';
        if (cel) cel.textContent = s.closedCount     ?? '';
        if (ael) ael.textContent = s.archivedCount   ?? '';
        // Update role-gated flag so action buttons render correctly
        canEnrich = s.canEnrich === true;
    } catch {}
}

function updateTabCounts() {
    var uel = document.getElementById('tabUnenrichedCount');
    var pel = document.getElementById('tabPipelineCount');
    var cel = document.getElementById('tabClosedCount');
    if (activeTab === 'unenriched' && uel) uel.textContent = allLeads.length;
    if (activeTab === 'pipeline'   && pel) pel.textContent = allLeads.length;
    if (activeTab === 'closed'     && cel) cel.textContent = allLeads.length;
    const cnt = allLeads.length;
    var hero = document.getElementById('heroCount');
    if (hero) hero.textContent = cnt ? '(' + cnt + ')' : '';
}

// ── Filter bar ────────────────────────────────────────────────────
function setFilter(f) {
    activeFilter = f;
    document.querySelectorAll('.filter-btn').forEach(b => b.classList.toggle('active', b.dataset.f === f));
    renderTable();
}

// ── Sort ──────────────────────────────────────────────────────────
function sortBy(col) {
    if (sortCol === col) sortDir = sortDir === 'asc' ? 'desc' : 'asc';
    else { sortCol = col; sortDir = 'asc'; }
    renderTable();
}

function getSortValue(lead, col) {
    const riskOrder = { High:0, Medium:1, Low:2 };
    const statusOrder = { new:0, contacted:1, appointment_set:2, closed_won:3, closed_lost:4 };
    switch (col) {
        case 'address':       return (lead.address      || '').toLowerCase();
        case 'riskLevel':     return riskOrder[lead.riskLevel] != null ? riskOrder[lead.riskLevel] : 3;
        case 'hailSize':      return parseFloat(lead.hailSize) || 0;
        case 'lastStormDate': return lead.lastStormDate || '';
        case 'yearBuilt':     return lead.yearBuilt     || 0;
        case 'ownerName':     return (lead.ownerName    || '').toLowerCase();
        case 'ownerPhone':    return (lead.ownerPhone   || '').toLowerCase();
        case 'ownerEmail':    return (lead.ownerEmail   || '').toLowerCase();
        case 'status':        return statusOrder[lead.status] != null ? statusOrder[lead.status] : 5;
        default:              return '';
    }
}

// ── Checkbox / selection ──────────────────────────────────────────
function toggleSelectAll(cb) {
    const checked = cb.checked;
    document.querySelectorAll('.row-checkbox').forEach(function(c) {
        c.checked = checked;
        var id = parseInt(c.dataset.id);
        if (checked) selectedIds.add(id); else selectedIds.delete(id);
        applyRowHighlight(id, checked);
    });
    cb.indeterminate = false;
    updateBulkToolbar();
}

function toggleRowSelect(cb) {
    var id = parseInt(cb.dataset.id);
    if (cb.checked) selectedIds.add(id); else selectedIds.delete(id);
    applyRowHighlight(id, cb.checked);
    updateBulkToolbar();
    updateSelectAllState();
}

function updateSelectAllState() {
    var allCbs = document.querySelectorAll('.row-checkbox');
    var selectAll = document.getElementById('selectAll');
    if (!selectAll) return;
    var total   = allCbs.length;
    var checked = Array.from(allCbs).filter(function(c) { return c.checked; }).length;
    selectAll.checked       = total > 0 && checked === total;
    selectAll.indeterminate = checked > 0 && checked < total;
}

function applyRowHighlight(id, on) {
    var row = document.querySelector('tr[data-lead-id="' + id + '"]');
    if (!row) return;
    if (on) {
        row.classList.add('row-selected', 'bg-orange-500/5', 'border-l-2', 'border-orange-500');
    } else {
        row.classList.remove('row-selected', 'bg-orange-500/5', 'border-l-2', 'border-orange-500');
    }
}

function clearSelection() {
    selectedIds.clear();
    document.querySelectorAll('.row-checkbox').forEach(function(c) { c.checked = false; });
    document.querySelectorAll('tr.row-selected').forEach(function(r) {
        r.classList.remove('row-selected', 'bg-orange-500/5', 'border-l-2', 'border-orange-500');
    });
    var selectAll = document.getElementById('selectAll');
    if (selectAll) { selectAll.checked = false; selectAll.indeterminate = false; }
    updateBulkToolbar();
}

function updateBulkToolbar() {
    var toolbar = document.getElementById('bulkToolbar');
    if (!toolbar) return;
    var show = selectedIds.size > 0 && activeTab !== 'archived';
    toolbar.classList.toggle('hidden', !show);
    if (!show) return;

    var countEl  = document.getElementById('selectedCount');
    var pluralEl = document.getElementById('selectedCountPlural');
    if (countEl)  countEl.textContent  = selectedIds.size;
    if (pluralEl) pluralEl.textContent = selectedIds.size === 1 ? '' : 's';

    // Enrich button only makes sense on the unenriched tab
    var enrichBtn = document.getElementById('btnBulkEnrich');
    if (enrichBtn) enrichBtn.classList.toggle('hidden', activeTab !== 'unenriched');
}

// ── Bulk actions ──────────────────────────────────────────────────
async function bulkEnrich() {
    if (selectedIds.size === 0) return;
    var ids  = Array.from(selectedIds);
    var btn  = document.getElementById('btnBulkEnrich');
    var orig = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin mr-1"></i>Enriching ' + ids.length + '...';
    try {
        var resp = await fetch('/Leads/BulkEnrich', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ids: ids })
        });
        var body = await resp.text();
        var r;
        try { r = JSON.parse(body); } catch { throw new Error('Server error - check logs'); }
        if (!resp.ok) throw new Error(r.error || 'HTTP ' + resp.status);
        var enriched = (r.results || []).filter(function(x) { return x.result && x.result.status === 'completed'; }).length;
        showToast('Enriched ' + enriched + ' of ' + ids.length + ' leads', enriched > 0);
        selectedIds.clear();
        updateBulkToolbar();
        await refreshTabCounts();
        await loadLeads();
    } catch (e) {
        showToast('Bulk enrich failed: ' + e.message, false);
    } finally {
        btn.disabled = false;
        btn.innerHTML = orig;
    }
}

async function bulkArchive() {
    if (selectedIds.size === 0) return;
    var ids = Array.from(selectedIds);
    if (!confirm('Archive ' + ids.length + ' lead(s)? They will be removed from this list.')) return;
    var btn  = document.getElementById('btnBulkArchive');
    var orig = btn ? btn.innerHTML : '';
    if (btn) { btn.disabled = true; btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin mr-1"></i>Archiving...'; }
    try {
        var resp = await fetch('/Leads/BulkArchive', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ids: ids })
        });
        var r = await resp.json();
        if (!resp.ok) throw new Error(r.error || 'HTTP ' + resp.status);
        showToast('Archived ' + r.archived + ' lead(s)', true);
        selectedIds.clear();
        updateBulkToolbar();
        await refreshTabCounts();
        await loadLeads();
    } catch (e) {
        showToast('Archive failed: ' + e.message, false);
    } finally {
        if (btn) { btn.disabled = false; btn.innerHTML = orig; }
    }
}

async function bulkDelete() {
    if (selectedIds.size === 0) return;
    var ids = Array.from(selectedIds);
    if (!confirm('Delete ' + ids.length + ' lead(s)? They will be moved to the Archived tab.')) return;
    var btn  = document.getElementById('btnBulkDelete');
    var orig = btn ? btn.innerHTML : '';
    if (btn) { btn.disabled = true; btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin mr-1"></i>Deleting...'; }
    try {
        var resp = await fetch('/Leads/BulkDelete', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ids: ids })
        });
        var body = await resp.text();
        var r;
        try { r = JSON.parse(body); } catch (_) { throw new Error('Server error - check logs'); }
        if (!resp.ok) throw new Error(r.error || 'HTTP ' + resp.status);
        // Remove deleted leads from the in-memory list so the table updates instantly
        allLeads = allLeads.filter(function(l) { return !selectedIds.has(l.id); });
        selectedIds.clear();
        updateTabCounts();
        renderTable();
        updateBulkToolbar();
        showToast('Deleted ' + (r.archived || 0) + ' lead(s)', true);
        refreshTabCounts();
    } catch (e) {
        showToast('Delete failed: ' + e.message, false);
    } finally {
        if (btn) { btn.disabled = false; btn.innerHTML = orig; }
    }
}

function bulkExport() {
    if (selectedIds.size === 0) return;
    var selected = allLeads.filter(function(l) { return selectedIds.has(l.id); });
    exportCSV(selected);
    showToast('Exported ' + selected.length + ' lead(s) to CSV', true);
}

// ── Table rendering ───────────────────────────────────────────────
function renderTable() {
    setLoading(false);
    const query = (document.getElementById('searchInput').value || '').toLowerCase();
    let rows = allLeads
        .filter(l => activeFilter === 'all' || l.riskLevel === activeFilter)
        .filter(l => !query || [l.address, l.ownerName, l.ownerPhone, l.ownerEmail].some(v => (v||'').toLowerCase().includes(query)));

    rows = rows.sort((a, b) => {
        const av = getSortValue(a, sortCol), bv = getSortValue(b, sortCol);
        const cmp = typeof av === 'number' ? av - bv : av.localeCompare(bv, undefined, { sensitivity:'base' });
        return sortDir === 'asc' ? cmp : -cmp;
    });

    document.getElementById('fAll').textContent    = allLeads.length;
    document.getElementById('fHigh').textContent   = allLeads.filter(l => l.riskLevel === 'High').length;
    document.getElementById('fMedium').textContent = allLeads.filter(l => l.riskLevel === 'Medium').length;
    document.getElementById('fLow').textContent    = allLeads.filter(l => l.riskLevel === 'Low').length;

    const body   = document.getElementById('leadsBody');
    const cards  = document.getElementById('mobileCards');
    const empty  = document.getElementById('leadsEmpty');
    const noMatch= document.getElementById('leadsNoMatch');
    empty.classList.add('hidden');
    noMatch.classList.add('hidden');

    if (allLeads.length === 0) {
        body.innerHTML = '';
        if (cards) cards.innerHTML = '';
        empty.classList.remove('hidden');
        return;
    }
    if (rows.length === 0) {
        body.innerHTML = '';
        if (cards) cards.innerHTML = '';
        noMatch.classList.remove('hidden');
        return;
    }

    body.innerHTML = rows.map(l => buildRow(l)).join('');
    if (cards) cards.innerHTML = rows.map(l => buildMobileCard(l)).join('');

    document.querySelectorAll('.row-checkbox').forEach(function(cb) {
        cb.checked = selectedIds.has(parseInt(cb.dataset.id));
    });
    updateSelectAllState();
    updateBulkToolbar();

    if (editingNotesId != null) {
        var ta = document.getElementById('notesArea_' + editingNotesId);
        if (ta) { ta.focus(); ta.setSelectionRange(ta.value.length, ta.value.length); }
    }
}

// ── Status helpers ────────────────────────────────────────────────
function statusClass(status) {
    var map = { new:'new', contacted:'contacted', appointment_set:'appt', closed_won:'won', closed_lost:'lost' };
    return 'status-' + (map[status] || 'new');
}

function buildStatusDropdown(lead) {
    var statuses = [
        { value: 'new',             label: 'New'       },
        { value: 'contacted',       label: 'Contacted' },
        { value: 'appointment_set', label: 'Appt Set'  },
        { value: 'closed_won',      label: 'Won'       },
        { value: 'closed_lost',     label: 'Lost'      },
    ];
    var cur = lead.status || 'new';
    return '<select onchange="setStatus(' + lead.id + ', this.value)" class="status-select ' + statusClass(cur) + '">' +
        statuses.map(function(s) {
            return '<option value="' + s.value + '"' + (s.value === cur ? ' selected' : '') + '>' + s.label + '</option>';
        }).join('') +
        '</select>';
}

async function setStatus(id, value) {
    try {
        var resp = await fetch('/Leads/' + id + '/Status', {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ status: value })
        });
        if (resp.status === 402) { window.location.href = '/Billing/Upgrade'; return; }
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        var lead = allLeads.find(function(l) { return l.id === id; });
        if (lead) lead.status = value;

        var pipelineStatuses = ['new', 'contacted', 'appointment_set'];
        var closedStatuses   = ['closed_won', 'closed_lost'];
        var leavesTab = (activeTab === 'unenriched' && !['new', null].includes(value)) ||
                        (activeTab === 'pipeline'   && !pipelineStatuses.includes(value)) ||
                        (activeTab === 'closed'     && !closedStatuses.includes(value));

        var dest = closedStatuses.includes(value) ? 'Closed' : pipelineStatuses.includes(value) ? 'Pipeline' : 'New Leads';
        showToast(leavesTab ? 'Status updated — moved to ' + dest : 'Status updated', true);
        await loadLeads();
        refreshTabCounts();
    } catch(e) {
        showToast('Failed to update status: ' + e.message, false);
        renderTable();
    }
}

// ── Contacts panel helpers ────────────────────────────────────────
function toggleContacts(id) {
    editingNotesId = null;
    viewingContactsId = (viewingContactsId === id) ? null : id;
    renderTable();
}

// ── Notes helpers ─────────────────────────────────────────────────
function openNotes(id) {
    editingId = null;
    viewingContactsId = null;
    editingNotesId = id;
    renderTable();
}
function closeNotes() {
    editingNotesId = null;
    renderTable();
}

async function saveNotes(id) {
    var ta   = document.getElementById('notesArea_' + id);
    var text = ta ? (ta.value || '').trim() : null;
    try {
        var resp = await fetch('/Leads/' + id + '/Notes', {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ notes: text || null })
        });
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        var lead = allLeads.find(function(l) { return l.id === id; });
        if (lead) lead.notes = text || null;
        editingNotesId = null;
        renderTable();
        showToast('Notes saved', true);
    } catch(e) { showToast('Save failed: ' + e.message, false); }
}

// ── Row builder ───────────────────────────────────────────────────
function buildRow(lead) {
    const rc  = ({ High:'badge-high', Medium:'badge-medium', Low:'badge-low' }[lead.riskLevel]) || 'badge-low';
    const ed  = editingId === lead.id;

    const cbCell = activeTab !== 'archived'
        ? '<td class="w-8"><input type="checkbox" class="row-checkbox accent-orange-500 w-4 h-4 cursor-pointer" data-id="' + lead.id + '" onchange="toggleRowSelect(this)" /></td>'
        : '';

    const hasNotes = !!(lead.notes && lead.notes.trim());
    const notesBtnCls = hasNotes
        ? 'w-7 h-7 rounded-lg flex items-center justify-center bg-amber-500/15 hover:bg-amber-500/30 text-amber-400 border border-amber-500/30 transition'
        : 'w-7 h-7 rounded-lg flex items-center justify-center bg-slate-700 hover:bg-slate-600 text-slate-400 border border-slate-600 transition';
    const notesBtn = '<button onclick="openNotes(' + lead.id + ')" class="' + notesBtnCls + '" title="' + (hasNotes ? escapeAttr(lead.notes.slice(0,80)) : 'Add notes') + '"><i class="fa-solid fa-note-sticky text-xs"></i></button>';

    // Pencil button toggles the edit expansion row; highlights when active
    const penCls = ed
        ? 'w-7 h-7 rounded-lg flex items-center justify-center bg-brand/20 text-brand border border-brand/40 transition'
        : 'w-7 h-7 rounded-lg flex items-center justify-center bg-slate-700 hover:bg-slate-600 text-slate-400 hover:text-brand border border-slate-600 transition';
    const penBtn = '<button onclick="' + (ed ? 'cancelEdit()' : 'startEdit(' + lead.id + ')') + '" class="' + penCls + '" title="' + (ed ? 'Cancel edit' : 'Edit contact') + '"><i class="fa-solid fa-pen text-xs"></i></button>';

    // Storm history button — highlights when accordion is open
    const stormHistOpen = viewingStormHistoryId === lead.id;
    const stormBtnCls = stormHistOpen
        ? 'w-7 h-7 rounded-lg flex items-center justify-center bg-cyan-500/25 text-cyan-300 border border-cyan-500/40 transition'
        : 'w-7 h-7 rounded-lg flex items-center justify-center bg-slate-700 hover:bg-slate-600 text-slate-400 hover:text-cyan-400 border border-slate-600 transition';
    const stormBtn = lead.lat
        ? '<button onclick="toggleStormHistory(' + lead.id + ')" class="' + stormBtnCls + '" title="Storm history (5 years)"><i class="fa-solid fa-cloud-bolt text-xs"></i></button>'
        : '';

    let ac;
    if (activeTab === 'unenriched') {
        const enrichBtn = canEnrich
            ? '<button onclick="enrichLead(' + lead.id + ', this)" class="w-7 h-7 rounded-lg flex items-center justify-center bg-orange-500/10 hover:bg-orange-500/30 text-orange-400 border border-orange-500/20 transition" title="Enrich"><i class="fa-solid fa-bolt text-xs"></i></button>'
            : '';
        ac = '<div class="flex items-center justify-center gap-1">' +
             enrichBtn + penBtn + notesBtn + stormBtn +
             '<button onclick="archiveLead(' + lead.id + ', this)" class="w-7 h-7 rounded-lg flex items-center justify-center bg-slate-600/40 hover:bg-slate-600 text-slate-400 border border-slate-600 transition" title="Archive"><i class="fa-solid fa-box-archive text-xs"></i></button></div>';
    } else if (activeTab === 'pipeline' || activeTab === 'closed') {
        ac = '<div class="flex items-center justify-center gap-1">' +
             '<button onclick="downloadReport(' + lead.id + ', this)" class="w-7 h-7 rounded-lg flex items-center justify-center bg-sky-500/10 hover:bg-sky-500/30 text-sky-400 border border-sky-500/20 transition" title="Download hail report PDF"><i class="fa-solid fa-file-pdf text-xs"></i></button>' +
             penBtn + notesBtn + stormBtn +
             '<span class="w-7 h-7 flex items-center justify-center" title="Enriched leads are protected"><i class="fa-solid fa-shield-halved text-xs text-slate-600"></i></span></div>';
    } else {
        ac = '<div class="flex items-center justify-center gap-1">' +
             '<button onclick="restoreLead(' + lead.id + ', this)" class="w-7 h-7 rounded-lg flex items-center justify-center bg-green-500/10 hover:bg-green-500/30 text-green-400 border border-green-500/20 transition" title="Restore to active"><i class="fa-solid fa-rotate-left text-xs"></i></button>' +
             notesBtn + stormBtn + '</div>';
    }

    const statusCell = activeTab !== 'archived'
        ? '<td class="hidden md:table-cell">' + buildStatusDropdown(lead) + '</td>'
        : '<td class="hidden md:table-cell"><span class="text-xs text-slate-600 italic">archived</span></td>';

    // Edit contact — expands a row below (pencil icon toggles it)
    const editExpRow = ed
        ? '<tr class="notes-row" data-edit-for="' + lead.id + '">' +
          '<td colspan="7" class="notes-row-cell">' +
          '<div class="flex items-center gap-2">' +
          '<span class="text-xs font-semibold text-slate-400 uppercase tracking-wide shrink-0"><i class="fa-solid fa-user mr-1.5"></i>Contact</span>' +
          '<input class="owner-input flex-1" id="eName_' + lead.id + '" value="' + escapeAttr(lead.ownerName || '') + '" placeholder="Owner name..." />' +
          '<input class="owner-input flex-1" id="ePhone_' + lead.id + '" value="' + escapeAttr(lead.ownerPhone || '') + '" placeholder="(555) 000-0000" />' +
          '<input class="owner-input flex-1" id="eEmail_' + lead.id + '" value="' + escapeAttr(lead.ownerEmail || '') + '" placeholder="owner@example.com" />' +
          '<button onclick="saveOwner(' + lead.id + ')" class="flex-shrink-0 w-7 h-7 rounded-lg flex items-center justify-center bg-green-500/20 hover:bg-green-500/40 text-green-400 border border-green-500/30 transition" title="Save"><i class="fa-solid fa-check text-xs"></i></button>' +
          '<button onclick="cancelEdit()" class="flex-shrink-0 w-7 h-7 rounded-lg flex items-center justify-center bg-slate-600/40 hover:bg-slate-600 text-slate-400 border border-slate-600 transition" title="Cancel"><i class="fa-solid fa-xmark text-xs"></i></button>' +
          '</div></td></tr>'
        : '';

    const notesExpRow = editingNotesId === lead.id
        ? '<tr class="notes-row" data-notes-for="' + lead.id + '">' +
          '<td colspan="7" class="notes-row-cell">' +
          '<div class="flex items-start gap-2">' +
          '<textarea id="notesArea_' + lead.id + '" class="notes-textarea" rows="2" placeholder="Add notes about this lead..." ' +
          'onkeydown="if(event.key===\'Escape\'){closeNotes();}else if((event.metaKey||event.ctrlKey)&&event.key===\'Enter\'){saveNotes(' + lead.id + ');}">' +
          escapeHtml(lead.notes || '') +
          '</textarea>' +
          '<button onclick="saveNotes(' + lead.id + ')" class="flex-shrink-0 mt-0.5 w-7 h-7 rounded-lg flex items-center justify-center bg-green-500/20 hover:bg-green-500/40 text-green-400 border border-green-500/30 transition" title="Save (Ctrl+Enter)"><i class="fa-solid fa-check text-xs"></i></button>' +
          '<button onclick="closeNotes()" class="flex-shrink-0 mt-0.5 w-7 h-7 rounded-lg flex items-center justify-center bg-slate-600/40 hover:bg-slate-600 text-slate-400 border border-slate-600 transition" title="Cancel (Esc)"><i class="fa-solid fa-xmark text-xs"></i></button>' +
          '</div></td></tr>'
        : '';

    // Hail cell with size comparison label
    var hailCell = (function() {
        var hl = hailLabel(lead.hailSize);
        return hl
            ? escapeHtml(lead.hailSize) + '<br><span class="text-xs ' + hl.cls + '">' + hl.label + '</span>'
            : escapeHtml(lead.hailSize);
    })();

    const rowSelectedCls = selectedIds.has(lead.id) ? ' row-selected bg-orange-500/5 border-l-2 border-orange-500' : '';
    return '<tr data-lead-id="' + lead.id + '" class="' + (ed ? 'editing' : '') + rowSelectedCls + '">' +
        cbCell +
        '<td class="font-medium text-white" style="max-width:200px"><span class="block truncate" title="' + escapeAttr(lead.address) + '">' + escapeHtml(lead.address) + '</span>' +
        (lead.sourceAddress ? '<span class="block text-xs text-slate-500 truncate">from ' + escapeHtml(lead.sourceAddress) + '</span>' : '') + '</td>' +
        '<td><span class="' + rc + ' px-2 py-0.5 rounded-full text-xs font-bold">' + escapeHtml(lead.riskLevel) + '</span></td>' +
        '<td class="hidden md:table-cell">' + hailCell + '</td>' +
        '<td class="hidden md:table-cell whitespace-nowrap">' +
            escapeHtml(lead.lastStormDate || '') +
            (lead.lastStormDate ? '<br>' + buildClaimBadge(lead.lastStormDate, lead.address) : '') +
        '</td>' +
        statusCell +
        '<td class="sticky-actions">' + ac + '</td></tr>' +
        editExpRow + notesExpRow + buildStormHistoryExpRow(lead);
}

// ── Row actions ───────────────────────────────────────────────────
function startEdit(id) { editingNotesId = null; editingId = id; renderTable(); var i = document.getElementById('eName_' + id); if (i) i.focus(); }
function cancelEdit()  { editingId = null; renderTable(); }

async function saveOwner(id) {
    var name  = (document.getElementById('eName_'  + id) || {}).value || null;
    var phone = (document.getElementById('ePhone_' + id) || {}).value || null;
    var email = (document.getElementById('eEmail_' + id) || {}).value || null;
    try {
        var resp = await fetch('/Leads/' + id + '/Owner', { method:'PATCH', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ ownerName:name, ownerPhone:phone, ownerEmail:email }) });
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        var lead = allLeads.find(function(l) { return l.id === id; });
        if (lead) { lead.ownerName = name; lead.ownerPhone = phone; lead.ownerEmail = email; }
        editingId = null; renderTable(); showToast('Owner info saved', true);
    } catch (e) { showToast('Save failed: ' + e.message, false); }
}

async function restoreLead(id, btn) {
    btn.disabled = true;
    var orig = btn.innerHTML;
    btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin text-xs"></i>';
    try {
        var resp = await fetch('/Leads/' + id + '/Restore', { method: 'POST' });
        if (!resp.ok) { var r = await resp.json(); throw new Error(r.error || 'HTTP ' + resp.status); }
        allLeads = allLeads.filter(function(l) { return l.id !== id; });
        updateTabCounts(); renderTable(); showToast('Lead restored', true);
        refreshTabCounts();
    } catch (e) {
        btn.disabled = false; btn.innerHTML = orig;
        showToast('Restore failed: ' + e.message, false);
    }
}

async function archiveLead(id, btn) {
    if (!confirm('Archive this lead? It will be removed from the active list.')) return;
    btn.disabled = true;
    try {
        var resp = await fetch('/Leads/BulkArchive', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ ids:[id] }) });
        if (!resp.ok) { var r = await resp.json(); throw new Error(r.error || 'HTTP ' + resp.status); }
        allLeads = allLeads.filter(function(l) { return l.id !== id; });
        selectedIds.delete(id);
        updateTabCounts(); renderTable(); showToast('Lead archived', true);
        refreshTabCounts();
    } catch (e) { btn.disabled = false; showToast('Archive failed: ' + e.message, false); }
}

function getFilenameFromResponse(resp) {
    var header = resp.headers.get('content-disposition');
    if (!header) return null;
    var match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(header);
    return match ? decodeURIComponent(match[1]) : null;
}

async function downloadReport(id, btn) {
    const icon = btn.querySelector('i');
    const origClass = icon ? icon.className : '';
    btn.disabled = true;
    if (icon) icon.className = 'fa-solid fa-spinner fa-spin text-xs';
    try {
        const resp = await fetch('/Leads/' + id + '/Report');
        if (resp.status === 402) { window.location.href = '/Billing/Upgrade'; return; }
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        const blob = await resp.blob();
        const url  = URL.createObjectURL(blob);
        const a    = document.createElement('a');
        a.href     = url;
        a.download = getFilenameFromResponse(resp) || ('HailReport-' + id + '.pdf');
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    } catch (e) {
        showToast('Report failed: ' + e.message, false);
    } finally {
        btn.disabled = false;
        if (icon) icon.className = origClass;
    }
}

async function enrichLead(id, btn) {
    btn.disabled = true;
    var origHtml = btn.innerHTML;
    btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin text-xs"></i>';
    try {
        var resp = await fetch('/Leads/' + id + '/Enrich', { method:'POST' });
        var r    = await resp.json();
        if (!resp.ok) throw new Error(r.error || 'HTTP ' + resp.status);

        if (r.status === 'completed') {
            allLeads = allLeads.filter(function(l) { return l.id !== id; });
            selectedIds.delete(id);
            var contactCount = (r.contacts && r.contacts.length) ? r.contacts.length : 0;
            var contactBit = contactCount > 1 ? contactCount + ' contacts' : (r.ownerPhone || r.ownerEmail ? '1 contact' : null);
            var found = [r.ownerName, r.yearBuilt ? 'built ' + r.yearBuilt : null, contactBit].filter(Boolean).join(' · ');
            showToast(found ? 'Found: ' + found : 'Parcel found - no additional data', true);
            updateTabCounts(); renderTable(); refreshTabCounts();
        } else {
            showToast('No parcel data found for this address', false);
            btn.disabled = false; btn.innerHTML = origHtml;
        }
    } catch (e) {
        showToast('Enrichment failed: ' + e.message, false);
        btn.disabled = false; btn.innerHTML = origHtml;
    }
}

// ── Export ────────────────────────────────────────────────────────
function exportCSV(leadsOverride) {
    var header = ['Address','Risk Level','Hail Size','Last Storm Date','Year Built',
                  'Contact Name','Phone','Email','Contact Type','Is Primary',
                  'Status','Notes','Source Address','Saved At'];

    function q(v) { return '"' + (v || '').toString().replace(/"/g,'""') + '"'; }

    var rows     = [header.join(',')];
    var srcLeads = Array.isArray(leadsOverride) ? leadsOverride : allLeads;

    srcLeads.forEach(function(l) {
        var base = [q(l.address), q(l.riskLevel), q(l.hailSize), q(l.lastStormDate), q(l.yearBuilt)];
        var tail = [q(l.status), q(l.notes), q(l.sourceAddress), q(l.savedAt)];
        var contacts = (l.contacts && l.contacts.length > 0) ? l.contacts : null;
        if (contacts) {
            contacts.forEach(function(c) {
                rows.push(base.concat([q(c.name), q(c.phone), q(c.email), q(c.contactType), q(c.isPrimary ? 'Yes' : 'No')]).concat(tail).join(','));
            });
        } else {
            rows.push(base.concat([q(l.ownerName), q(l.ownerPhone), q(l.ownerEmail), q('owner'), q('Yes')]).concat(tail).join(','));
        }
    });

    var a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([rows.join('\n')], { type:'text/csv' }));
    a.download = 'StormLeads_' + new Date().toISOString().slice(0,10) + '.csv';
    a.click();
}

// ── Hail size helper ──────────────────────────────────────────────
function hailLabel(raw) {
    var n = parseFloat(raw);
    if (isNaN(n) || n <= 0) return null;
    var ref;
    if      (n < 0.75) ref = { label: 'Pea',        cls: 'text-yellow-500' };
    else if (n < 0.88) ref = { label: 'Penny',       cls: 'text-yellow-400' };
    else if (n < 1.00) ref = { label: 'Nickel',      cls: 'text-yellow-400' };
    else if (n < 1.25) ref = { label: 'Quarter',     cls: 'text-orange-400' };
    else if (n < 1.50) ref = { label: 'Half Dollar', cls: 'text-orange-400' };
    else if (n < 1.75) ref = { label: 'Ping Pong',   cls: 'text-orange-500' };
    else if (n < 2.00) ref = { label: 'Golf Ball',   cls: 'text-red-400'    };
    else if (n < 2.50) ref = { label: 'Hen Egg',     cls: 'text-red-400'    };
    else if (n < 2.75) ref = { label: 'Tennis Ball', cls: 'text-red-500'    };
    else if (n < 4.00) ref = { label: 'Baseball',    cls: 'text-red-500'    };
    else               ref = { label: 'Softball',    cls: 'text-red-600'    };
    return ref;
}

// ── Utility ───────────────────────────────────────────────────────
function setLoading(on) {
    document.getElementById('leadsLoading').classList.toggle('hidden', !on);
    if (on) {
        document.getElementById('leadsBody').innerHTML = '';
        var cards = document.getElementById('mobileCards');
        if (cards) cards.innerHTML = '';
    }
}

function escapeHtml(s) {
    if (s === null || s === undefined) return '';
    return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}
function escapeAttr(s) { return escapeHtml(s); }

// ── Mobile card view ──────────────────────────────────────────────
function buildMobileCard(lead) {
    const rc = ({ High:'badge-high', Medium:'badge-medium', Low:'badge-low' }[lead.riskLevel]) || 'badge-low';
    const ed = editingId === lead.id;
    const enotes = editingNotesId === lead.id;

    if (ed) {
        return '<div class="bg-slate-800 border border-brand/40 rounded-2xl p-4 shadow-md" data-lead-id="' + lead.id + '">' +
            '<p class="text-white font-semibold text-sm mb-3 truncate">' + escapeHtml(lead.address) + '</p>' +
            '<div class="space-y-2 mb-3">' +
            '<input class="owner-input w-full" id="eName_'  + lead.id + '" value="' + escapeAttr(lead.ownerName  || '') + '" placeholder="Owner name" />' +
            '<input class="owner-input w-full" id="ePhone_' + lead.id + '" value="' + escapeAttr(lead.ownerPhone || '') + '" placeholder="(555) 000-0000" />' +
            '<input class="owner-input w-full" id="eEmail_' + lead.id + '" value="' + escapeAttr(lead.ownerEmail || '') + '" placeholder="owner@example.com" />' +
            '</div>' +
            '<div class="flex gap-2">' +
            '<button onclick="saveOwner(' + lead.id + ')" class="flex-1 py-2.5 rounded-xl bg-green-500/20 border border-green-500/30 text-green-400 text-sm font-semibold hover:bg-green-500/30 transition"><i class="fa-solid fa-check mr-1"></i>Save</button>' +
            '<button onclick="cancelEdit()" class="py-2.5 px-4 rounded-xl bg-slate-700 border border-slate-600 text-slate-300 text-sm font-semibold hover:bg-slate-600 transition"><i class="fa-solid fa-xmark"></i></button>' +
            '</div></div>';
    }

    const phoneHtml = lead.ownerPhone
        ? '<a href="tel:' + escapeAttr(lead.ownerPhone) + '" class="flex-1 flex items-center justify-center gap-2 py-3 rounded-xl bg-green-500/15 border border-green-500/30 text-green-400 text-sm font-semibold active:bg-green-500/30 transition"><i class="fa-solid fa-phone"></i>' + escapeHtml(lead.ownerPhone) + '</a>'
        : '<span class="flex-1 flex items-center justify-center gap-2 py-3 rounded-xl bg-slate-700/40 border border-slate-600/60 text-slate-500 text-sm"><i class="fa-solid fa-phone-slash"></i>No phone yet</span>';

    const hailBit = (lead.hailSize && lead.hailSize !== 'No data') ? (function() {
        var hl = hailLabel(lead.hailSize);
        return '<span><i class="fa-solid fa-cloud-bolt mr-1 text-orange-400"></i>' + escapeHtml(lead.hailSize) +
               (hl ? ' <span class="' + hl.cls + '">(' + hl.label + ')</span>' : '') + '</span>';
    })() : '';
    const dateBit = (lead.lastStormDate && lead.lastStormDate !== 'No data')
        ? '<span><i class="fa-solid fa-calendar mr-1 text-slate-500"></i>' + escapeHtml(lead.lastStormDate) + ' ' + buildClaimBadge(lead.lastStormDate, lead.address) + '</span>'
        : '';
    const yearBit = lead.yearBuilt ? '<span><i class="fa-solid fa-house mr-1 text-slate-500"></i>Built ' + lead.yearBuilt + '</span>' : '';

    const enrichedBadge = lead.isEnriched ? '<span class="text-xs text-green-400 font-semibold"><i class="fa-solid fa-check-circle mr-1"></i>Enriched</span>' : '';

    const reportBtn = '<button onclick="downloadReport(' + lead.id + ', this)" class="py-2 px-3.5 rounded-xl bg-sky-500/10 border border-sky-500/20 text-sky-400 text-xs font-semibold hover:bg-sky-500/20 transition" title="Download hail report PDF"><i class="fa-solid fa-file-pdf"></i></button>';
    const actionBtns = activeTab === 'unenriched'
        ? (canEnrich ? '<button onclick="enrichLead(' + lead.id + ', this)" class="flex-1 py-2 rounded-xl bg-orange-500/10 border border-orange-500/20 text-orange-400 text-xs font-semibold hover:bg-orange-500/20 active:bg-orange-500/30 transition"><i class="fa-solid fa-bolt mr-1"></i>Enrich</button>' : '') +
          '<button onclick="startEdit(' + lead.id + ')" class="flex-1 py-2 rounded-xl bg-slate-700 border border-slate-600 text-slate-300 text-xs font-semibold hover:bg-slate-600 transition"><i class="fa-solid fa-pen mr-1"></i>Edit</button>' +
          '<button onclick="archiveLead(' + lead.id + ', this)" class="py-2 px-3.5 rounded-xl bg-slate-600/40 border border-slate-600 text-slate-400 text-xs font-semibold hover:bg-slate-600 transition"><i class="fa-solid fa-box-archive"></i></button>'
        : (activeTab === 'pipeline' || activeTab === 'closed')
        ? reportBtn + '<button onclick="startEdit(' + lead.id + ')" class="flex-1 py-2 rounded-xl bg-slate-700 border border-slate-600 text-slate-300 text-xs font-semibold hover:bg-slate-600 transition"><i class="fa-solid fa-pen mr-1"></i>Edit</button>'
        : '<button onclick="restoreLead(' + lead.id + ', this)" class="flex-1 py-2 rounded-xl bg-green-500/10 border border-green-500/20 text-green-400 text-xs font-semibold hover:bg-green-500/20 transition"><i class="fa-solid fa-rotate-left mr-1"></i>Restore</button>';

    const statusRow = activeTab !== 'archived'
        ? '<div class="flex items-center justify-between mt-3 pt-3 border-t border-slate-700/60">' +
          '<span class="text-xs text-slate-500 font-semibold uppercase tracking-wide">Pipeline</span>' +
          buildStatusDropdown(lead) +
          '</div>'
        : '';

    const hasNotes = !!(lead.notes && lead.notes.trim());
    const notesSection = enotes
        ? '<div class="mt-3 pt-3 border-t border-slate-700/60 space-y-2">' +
          '<textarea id="notesArea_' + lead.id + '" class="notes-textarea w-full" rows="3" placeholder="Add notes about this lead"></textarea>' +
          '<div class="flex gap-2">' +
          '<button onclick="saveNotes(' + lead.id + ')" class="flex-1 py-2 rounded-xl bg-green-500/20 border border-green-500/30 text-green-400 text-xs font-semibold hover:bg-green-500/30 transition"><i class="fa-solid fa-check mr-1"></i>Save Note</button>' +
          '<button onclick="closeNotes()" class="py-2 px-3.5 rounded-xl bg-slate-700 border border-slate-600 text-slate-300 text-xs font-semibold hover:bg-slate-600 transition"><i class="fa-solid fa-xmark"></i></button>' +
          '</div></div>'
        : '<div class="mt-3 pt-3 border-t border-slate-700/60">' +
          (hasNotes ? '<p class="text-xs text-slate-400 mb-2 leading-relaxed"><i class="fa-solid fa-note-sticky mr-1.5 text-amber-400"></i>' + escapeHtml(lead.notes.slice(0,100)) + (lead.notes.length > 100 ? '...' : '') + '</p>' : '') +
          '<button onclick="openNotes(' + lead.id + ')" class="w-full py-2 rounded-xl bg-slate-700/60 border border-slate-600/60 text-xs font-semibold hover:bg-slate-700 transition ' + (hasNotes ? 'text-amber-400' : 'text-slate-400') + '">' +
          '<i class="fa-solid fa-note-sticky mr-1.5"></i>' + (hasNotes ? 'Edit Note' : 'Add Note') + '</button>' +
          '</div>';

    // Storm history section for mobile card
    const mobileStormHistOpen = viewingStormHistoryId === lead.id;
    const mobileStormBtnCls = mobileStormHistOpen
        ? 'w-full py-2 rounded-xl bg-cyan-500/20 border border-cyan-500/30 text-cyan-300 text-xs font-semibold transition'
        : 'w-full py-2 rounded-xl bg-slate-700/60 border border-slate-600/60 text-slate-400 text-xs font-semibold hover:bg-slate-700 transition';

    var mobileStormSection = lead.lat
        ? '<div class="mt-2 pt-2 border-t border-slate-700/60">' +
          '<button onclick="toggleStormHistory(' + lead.id + ')" class="' + mobileStormBtnCls + '">' +
          '<i class="fa-solid fa-cloud-bolt mr-1.5"></i>' + (mobileStormHistOpen ? 'Hide Storm History' : 'Storm History (5 yrs)') + '</button>' +
          (mobileStormHistOpen ? buildMobileStormHistoryPanel(lead.id) : '') +
          '</div>'
        : '';

    var cardHtml = '<div class="bg-slate-800 border border-slate-700/60 rounded-2xl p-4 shadow-md" data-lead-id="' + lead.id + '">' +
        '<div class="flex items-start justify-between gap-2 mb-1">' +
        '<div class="flex-1 min-w-0">' +
        '<p class="font-semibold text-white text-sm leading-tight">' + escapeHtml(lead.address) + '</p>' +
        (lead.ownerName ? '<p class="text-xs text-slate-400 mt-0.5"><i class="fa-solid fa-user mr-1"></i>' + escapeHtml(lead.ownerName) + '</p>' : '') +
        '</div>' +
        '<div class="flex flex-col items-end gap-1">' +
        '<span class="' + rc + ' px-2.5 py-0.5 rounded-full text-xs font-bold">' + escapeHtml(lead.riskLevel) + '</span>' +
        enrichedBadge +
        '</div></div>' +
        ((hailBit || dateBit || yearBit) ? '<div class="flex items-center gap-3 text-xs text-slate-400 mb-3 mt-2 flex-wrap">' + hailBit + dateBit + yearBit + '</div>' : '<div class="mb-3"></div>') +
        '<div class="flex gap-2 mb-3">' + phoneHtml + '</div>' +
        '<div class="flex items-center gap-2">' + actionBtns + '</div>' +
        statusRow + notesSection + mobileStormSection +
        '</div>';
    return cardHtml;
}

// ── Storm history helpers ─────────────────────────────────────────
function toggleWindForLead(id) {
    if (showWindIds.has(id)) showWindIds.delete(id); else showWindIds.add(id);
    renderTable();
}

function buildMobileStormHistoryPanel(id) {
    var cached = stormHistoryCache[id];
    if (!cached) {
        return '<div class="mt-2 flex items-center gap-2 text-slate-400 text-xs py-1">' +
               '<i class="fa-solid fa-spinner fa-spin text-cyan-400"></i><span>Loading…</span></div>';
    }
    if (cached.error) {
        return '<p class="mt-2 text-red-400 text-xs"><i class="fa-solid fa-triangle-exclamation mr-1"></i>' + escapeHtml(cached.error) + '</p>';
    }
    var hail = (cached.hail || []);
    var wind = (cached.wind || []);
    var showWind = showWindIds.has(id);

    if (hail.length === 0 && wind.length === 0) {
        return '<p class="mt-2 text-slate-500 text-xs italic">No storm events within 10 miles.</p>';
    }

    var hailRows = hail.slice(0, 20).map(function(e) {
        var hl = hailLabel(e.sizeInches);
        var sizeRef = hl ? ' <span class="' + hl.cls + '">(' + hl.label + ')</span>' : '';
        return '<div class="flex items-center gap-2 py-1 border-b border-slate-700/40 last:border-0 text-xs">' +
               '<i class="fa-solid fa-cloud-bolt text-orange-400 w-3 shrink-0"></i>' +
               '<span class="w-20 shrink-0 text-slate-300 font-mono">' + escapeHtml(e.date) + '</span>' +
               '<span class="text-orange-400 font-semibold">' + e.sizeInches.toFixed(2) + '"' + sizeRef + '</span>' +
               '<span class="text-slate-500 ml-auto shrink-0">' + (e.miles != null ? e.miles.toFixed(1) + ' mi' : '') + '</span>' +
               '</div>';
    }).join('');

    var windRows = showWind ? wind.slice(0, 20).map(function(w) {
        return '<div class="flex items-center gap-2 py-1 border-b border-slate-700/40 last:border-0 text-xs">' +
               '<i class="fa-solid fa-wind text-sky-400 w-3 shrink-0"></i>' +
               '<span class="w-20 shrink-0 text-slate-300 font-mono">' + escapeHtml(w.date) + '</span>' +
               '<span class="text-sky-400 font-semibold">' + w.windMph + ' mph</span>' +
               '<span class="text-slate-500 ml-auto shrink-0">' + (w.miles != null ? w.miles.toFixed(1) + ' mi' : '') + '</span>' +
               '</div>';
    }).join('') : '';

    var windBtn = wind.length > 0
        ? '<button onclick="toggleWindForLead(' + id + ')" class="mt-2 text-xs px-2 py-1 rounded-lg border ' +
          (showWind ? 'bg-sky-500/20 text-sky-300 border-sky-500/30' : 'bg-slate-700 text-slate-400 border-slate-600') +
          '">' + (showWind ? 'Hide wind' : 'Show wind (' + wind.length + ')') + '</button>'
        : '';

    return '<div class="mt-2">' + hailRows + windRows + windBtn + '</div>';
}

// ── Storm history accordion ───────────────────────────────────────
async function toggleStormHistory(id) {
    if (viewingStormHistoryId === id) {
        viewingStormHistoryId = null;
        renderTable();
        return;
    }
    viewingStormHistoryId = id;
    renderTable(); // show loading spinner immediately

    if (!stormHistoryCache[id]) {
        var lead = allLeads.find(function(l) { return l.id === id; });
        if (!lead) return;

        // Try to parse a 2-letter US state code from the address string
        var stateMatch = (lead.address || '').match(/\b([A-Z]{2})\b\s*\d{5}/);
        var state = stateMatch ? stateMatch[1] : '';

        var url = '/RoofHealth/StormHistory?lat=' + lead.lat + '&lng=' + lead.lng;
        if (state) url += '&state=' + encodeURIComponent(state);

        try {
            var resp = await fetch(url);
            if (!resp.ok) throw new Error('HTTP ' + resp.status);
            stormHistoryCache[id] = await resp.json();
        } catch (e) {
            stormHistoryCache[id] = { error: e.message };
        }
        renderTable(); // re-render with loaded data
    }
}

function buildStormHistoryExpRow(lead) {
    if (viewingStormHistoryId !== lead.id) return '';

    var innerHtml = '';
    var cached = stormHistoryCache[lead.id];
    var closeBtn = '<button onclick="toggleStormHistory(' + lead.id + ')" class="text-xs text-slate-500 hover:text-slate-300 transition" title="Close"><i class="fa-solid fa-xmark"></i></button>';

    if (!cached) {
        innerHtml = '<div class="flex items-center gap-2 text-slate-400 text-sm py-1">' +
                    '<i class="fa-solid fa-spinner fa-spin text-cyan-400 text-xs"></i>' +
                    '<span>Loading storm history…</span></div>';
    } else if (cached.error) {
        innerHtml = '<div class="flex items-center justify-between">' +
                    '<span class="text-red-400 text-xs"><i class="fa-solid fa-triangle-exclamation mr-1.5"></i>Failed to load: ' + escapeHtml(cached.error) + '</span>' + closeBtn + '</div>';
    } else {
        var hail = cached.hail || [];
        var wind = cached.wind || [];
        var showWind = showWindIds.has(lead.id);

        // ── Header bar ─────────────────────────────────────────────────
        var hailLabel2 = hail.length + ' hail event' + (hail.length !== 1 ? 's' : '') + ' (5 yr)';
        var windToggleBtn = wind.length > 0
            ? '<button onclick="toggleWindForLead(' + lead.id + ')" class="px-2 py-0.5 rounded-lg text-xs border transition ' +
              (showWind ? 'bg-sky-500/20 text-sky-300 border-sky-500/30 hover:bg-sky-500/30'
                        : 'bg-slate-700 text-slate-400 border-slate-600 hover:bg-slate-600 hover:text-sky-400') + '">' +
              '<i class="fa-solid fa-wind mr-1"></i>' + (showWind ? 'Hide wind' : 'Wind (' + wind.length + ')') +
              '</button>'
            : '';

        var headerBar = '<div class="flex items-center justify-between mb-2 gap-2">' +
                        '<div class="flex items-center gap-2">' +
                        '<span class="text-xs font-semibold text-cyan-400 uppercase tracking-wide">' +
                        '<i class="fa-solid fa-cloud-bolt mr-1.5"></i>' + hailLabel2 + '</span>' +
                        windToggleBtn + '</div>' + closeBtn + '</div>';

        if (hail.length === 0 && !showWind) {
            innerHtml = headerBar +
                        '<p class="text-slate-500 text-xs italic"><i class="fa-solid fa-cloud-sun mr-1"></i>' +
                        'No hail events found within 10 miles in the last 5 years.' +
                        (wind.length > 0 ? ' Wind data available — click Wind to view.' : '') + '</p>';
        } else {
            var srcBadge = function(src) {
                return src === 'lsr'
                    ? '<span class="px-1.5 py-0.5 rounded text-xs bg-green-500/15 text-green-400 border border-green-500/20">LSR</span>'
                    : src === 'tomorrow'
                    ? '<span class="px-1.5 py-0.5 rounded text-xs bg-violet-500/15 text-violet-400 border border-violet-500/20">Tomorrow.io</span>'
                    : src === 'lsr-wind'
                    ? '<span class="px-1.5 py-0.5 rounded text-xs bg-sky-500/15 text-sky-400 border border-sky-500/20">LSR</span>'
                    : '<span class="px-1.5 py-0.5 rounded text-xs bg-slate-700 text-slate-400 border border-slate-600">NOAA</span>';
            };

            // ── Stats summary bar ──────────────────────────────────────────
            var peakHail  = hail.length > 0 ? Math.max.apply(null, hail.map(function(e){ return e.sizeInches; })) : null;
            var peakWind  = wind.length > 0 ? Math.max.apply(null, wind.map(function(w){ return w.windMph; })) : null;
            var statsBar  = '<div class="flex flex-wrap gap-3 mb-3 mt-1">';
            statsBar += '<div class="flex items-center gap-1.5 px-3 py-1.5 rounded-lg bg-orange-500/10 border border-orange-500/20 text-xs">' +
                        '<i class="fa-solid fa-cloud-bolt text-orange-400"></i>' +
                        '<span class="text-slate-400">Events:</span> <span class="text-orange-300 font-bold">' + hail.length + '</span></div>';
            if (peakHail !== null) {
                statsBar += '<div class="flex items-center gap-1.5 px-3 py-1.5 rounded-lg bg-orange-500/10 border border-orange-500/20 text-xs">' +
                            '<i class="fa-solid fa-ruler text-orange-400"></i>' +
                            '<span class="text-slate-400">Peak hail:</span> <span class="text-orange-300 font-bold">' + peakHail.toFixed(2) + '"</span></div>';
            }
            if (peakWind !== null) {
                statsBar += '<div class="flex items-center gap-1.5 px-3 py-1.5 rounded-lg bg-sky-500/10 border border-sky-500/20 text-xs">' +
                            '<i class="fa-solid fa-wind text-sky-400"></i>' +
                            '<span class="text-slate-400">Peak wind:</span> <span class="text-sky-300 font-bold">' + peakWind + ' mph</span></div>';
            }
            statsBar += '</div>';

            var hailRows = hail.map(function(e) {
                var hl = hailLabel(e.sizeInches);
                var sizeRef = hl ? ' <span class="' + hl.cls + ' text-xs">(' + hl.label + ')</span>' : '';
                return '<div class="flex items-center gap-3 py-1.5 border-b border-slate-700/40 last:border-0">' +
                       '<i class="fa-solid fa-cloud-bolt text-orange-400 text-xs w-3 shrink-0"></i>' +
                       '<span class="w-24 shrink-0 text-slate-300 font-mono text-xs">' + escapeHtml(e.date) + '</span>' +
                       '<span class="w-28 shrink-0 text-orange-400 font-semibold text-xs">' + e.sizeInches.toFixed(2) + '"' + sizeRef + '</span>' +
                       srcBadge(e.source) +
                       '<span class="text-slate-500 text-xs ml-auto shrink-0">' + (e.miles != null ? e.miles.toFixed(1) + ' mi' : '') + '</span>' +
                       '</div>';
            }).join('');

            var windRows = showWind ? wind.map(function(w) {
                return '<div class="flex items-center gap-3 py-1.5 border-b border-slate-700/40 last:border-0">' +
                       '<i class="fa-solid fa-wind text-sky-400 text-xs w-3 shrink-0"></i>' +
                       '<span class="w-24 shrink-0 text-slate-300 font-mono text-xs">' + escapeHtml(w.date) + '</span>' +
                       '<span class="w-28 shrink-0 text-sky-400 font-semibold text-xs">' + w.windMph + ' mph gusts</span>' +
                       srcBadge(w.source) +
                       '<span class="text-slate-500 text-xs ml-auto shrink-0">' + (w.miles != null ? w.miles.toFixed(1) + ' mi' : '') + '</span>' +
                       '</div>';
            }).join('') : '';

            innerHtml = headerBar + statsBar + hailRows + windRows;
        }
    }

    return '<tr class="notes-row" data-storm-history-for="' + lead.id + '">' +
           '<td colspan="7" class="notes-row-cell">' + innerHtml + '</td></tr>';
}

// ── Mobile nav ────────────────────────────────────────────────────
function toggleMobileMenu() {
    var menu = document.getElementById('mobileMenu');
    var icon = document.getElementById('mobileMenuIcon');
    if (!menu) return;
    var nowHidden = menu.classList.toggle('hidden');
    icon.className = nowHidden ? 'fa-solid fa-bars text-lg' : 'fa-solid fa-xmark text-lg';
}

// ── Toast ─────────────────────────────────────────────────────────
var _toastTimer = null;
function showToast(msg, success) {
    var toast = document.getElementById('toast');
    if (_toastTimer) clearTimeout(_toastTimer);
    toast.className = success ? 'success' : 'error';
    document.getElementById('toastIcon').className = 'fa-solid ' + (success ? 'fa-circle-check' : 'fa-circle-xmark');
    document.getElementById('toastMsg').textContent = msg;
    toast.offsetHeight;
    toast.classList.add('show');
    _toastTimer = setTimeout(function() { toast.classList.remove('show'); }, 3500);
}
