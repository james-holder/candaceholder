// saved-leads.js — Views/Leads/Saved.cshtml
let allLeads      = [];
let sortCol       = 'savedAt';
let sortDir       = 'desc';
let activeTab     = 'untraced';   // 'untraced' | 'traced' | 'closed' | 'archived'
const TAB_IDS     = { untraced: 'tabUntraced', traced: 'tabTraced', closed: 'tabClosed', archived: 'tabArchived' };
let selectedIds   = new Set();
let editingId      = null;
let editingNotesId = null;
let viewingContactsId = null;   // lead whose full contact list is expanded
let canEnrich     = false;   // set from /Leads/Stats — owners/managers only

document.addEventListener('DOMContentLoaded', function() { refreshTabCounts().then(loadLeads); });

// ── Tab switching ─────────────────────────────────────────────────
function switchLeadTab(tab) {
    activeTab = tab;
    selectedIds.clear();
    editingId = null;
    editingNotesId = null;
    viewingContactsId = null;

    Object.keys(TAB_IDS).forEach(function (t) {
        document.getElementById(TAB_IDS[t]).classList.toggle('lead-tab-active', t === tab);
    });

    // Checkboxes and the bulk toolbar don't apply to archived leads
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
            '<tr><td colspan="7" class="text-center text-red-600 py-8 text-sm px-4">' +
            '<i class="fa-solid fa-triangle-exclamation mr-2"></i>Failed to load: ' + escapeHtml(e.message) + '</td></tr>';
    }
}

async function refreshTabCounts() {
    try {
        const r = await fetch('/Leads/Stats');
        if (!r.ok) return;
        const s = await r.json();
        document.getElementById('tabUntracedCount').textContent = s.untracedCount ?? '';
        document.getElementById('tabTracedCount').textContent   = s.tracedCount   ?? '';
        document.getElementById('tabClosedCount').textContent   = s.closedCount   ?? '';
        document.getElementById('tabArchivedCount').textContent = s.archivedCount ?? '';
        // Role-gated flag so skip-trace buttons render correctly
        canEnrich = s.canEnrich === true;
    } catch {}
}

function updateTabCounts() {
    var el = document.getElementById(TAB_IDS[activeTab] + 'Count');
    if (el) el.textContent = allLeads.length;
    var hero = document.getElementById('heroCount');
    if (hero) hero.textContent = allLeads.length ? '(' + allLeads.length + ')' : '';
}

// ── Sort ──────────────────────────────────────────────────────────
function sortBy(col) {
    if (sortCol === col) sortDir = sortDir === 'asc' ? 'desc' : 'asc';
    else { sortCol = col; sortDir = 'asc'; }
    renderTable();
}

function getSortValue(lead, col) {
    const statusOrder = { new:0, contacted:1, appointment_set:2, closed_won:3, closed_lost:4 };
    switch (col) {
        case 'address':    return (lead.address    || '').toLowerCase();
        case 'ownerName':  return (lead.ownerName  || '').toLowerCase();
        case 'ownerPhone': return (lead.ownerPhone || '').toLowerCase();
        case 'ownerEmail': return (lead.ownerEmail || '').toLowerCase();
        case 'status':     return statusOrder[lead.status] != null ? statusOrder[lead.status] : 5;
        case 'savedAt':    return lead.savedAt || '';
        default:           return '';
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
    ['row-selected', 'bg-orange-500/5', 'border-l-2', 'border-orange-500'].forEach(function(c) { row.classList.toggle(c, on); });
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

function selectedUntracedIds() {
    return allLeads.filter(function(l) { return selectedIds.has(l.id) && !l.isEnriched; }).map(function(l) { return l.id; });
}

function updateBulkToolbar() {
    var toolbar = document.getElementById('bulkToolbar');
    if (!toolbar) return;
    var show = selectedIds.size > 0 && activeTab !== 'archived';
    toolbar.classList.toggle('hidden', !show);
    if (!show) return;

    document.getElementById('selectedCount').textContent       = selectedIds.size;
    document.getElementById('selectedCountPlural').textContent = selectedIds.size === 1 ? '' : 's';

    // Skip trace only applies to selected leads that haven't been traced yet
    var enrichBtn = document.getElementById('btnBulkEnrich');
    var untraced  = selectedUntracedIds().length;
    enrichBtn.classList.toggle('hidden', !canEnrich || untraced === 0);
    enrichBtn.innerHTML = '<i class="fa-solid fa-magnifying-glass-dollar"></i>Skip Trace ' + untraced;

    // Email only makes sense for selected leads that have an email address
    var emailBtn  = document.getElementById('btnBulkEmail');
    var emailable = selectedEmailableLeads().length;
    emailBtn.classList.toggle('hidden', !canEnrich || emailable === 0);
    emailBtn.innerHTML = '<i class="fa-solid fa-paper-plane"></i>Email ' + emailable;
}

// ── Bulk actions ──────────────────────────────────────────────────
async function bulkEnrich() {
    var ids = selectedUntracedIds();
    if (ids.length === 0) return;
    if (!confirm('Skip trace ' + ids.length + ' address' + (ids.length === 1 ? '' : 'es') + '?\n\n' +
                 'Each lookup is billed by your skip-tracing provider.')) return;

    var btn  = document.getElementById('btnBulkEnrich');
    var orig = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin mr-1"></i>Tracing ' + ids.length + '…';
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
        var found = (r.results || []).filter(function(x) { return x.result && x.result.status === 'completed'; }).length;
        showToast('Found data for ' + found + ' of ' + r.processed + ' address' + (r.processed === 1 ? '' : 'es'), found > 0);
        selectedIds.clear();
        updateBulkToolbar();
        await loadLeads();
        refreshTabCounts();
    } catch (e) {
        showToast('Skip trace failed: ' + e.message, false);
    } finally {
        btn.disabled = false;
        btn.innerHTML = orig;
    }
}

async function bulkDelete() {
    if (selectedIds.size === 0) return;
    var ids = Array.from(selectedIds);
    if (!confirm('Archive ' + ids.length + ' lead(s)? You can restore them from the Archived tab.')) return;
    var btn  = document.getElementById('btnBulkDelete');
    var orig = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin mr-1"></i>Archiving…';
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
        allLeads = allLeads.filter(function(l) { return !selectedIds.has(l.id); });
        selectedIds.clear();
        updateTabCounts();
        renderTable();
        updateBulkToolbar();
        showToast('Archived ' + (r.archived || 0) + ' lead(s)', true);
        refreshTabCounts();
    } catch (e) {
        showToast('Archive failed: ' + e.message, false);
    } finally {
        btn.disabled = false;
        btn.innerHTML = orig;
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
        .filter(l => !query || [l.address, l.ownerName, l.ownerPhone, l.ownerEmail].some(v => (v||'').toLowerCase().includes(query)));

    rows = rows.sort((a, b) => {
        const av = getSortValue(a, sortCol), bv = getSortValue(b, sortCol);
        const cmp = typeof av === 'number' ? av - bv : av.localeCompare(bv, undefined, { sensitivity:'base', numeric:true });
        return sortDir === 'asc' ? cmp : -cmp;
    });


    const body    = document.getElementById('leadsBody');
    const cards   = document.getElementById('mobileCards');
    const empty   = document.getElementById('leadsEmpty');
    const noMatch = document.getElementById('leadsNoMatch');
    empty.classList.add('hidden');
    noMatch.classList.add('hidden');

    if (allLeads.length === 0) {
        body.innerHTML = '';
        cards.innerHTML = '';
        empty.classList.remove('hidden');
        return;
    }
    if (rows.length === 0) {
        body.innerHTML = '';
        cards.innerHTML = '';
        noMatch.classList.remove('hidden');
        return;
    }

    body.innerHTML  = rows.map(l => buildRow(l)).join('');
    cards.innerHTML = rows.map(l => buildMobileCard(l)).join('');

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
        if (!resp.ok) throw new Error('HTTP ' + resp.status);

        var closedStatuses = ['closed_won', 'closed_lost'];
        var lead      = allLeads.find(function (l) { return l.id === id; });
        var leavesTab = (activeTab !== 'closed' && closedStatuses.includes(value)) ||
                        (activeTab === 'closed' && !closedStatuses.includes(value));
        var dest = closedStatuses.includes(value) ? 'Closed' : (lead && lead.isEnriched ? 'Traced' : 'Not traced');
        showToast(leavesTab ? 'Status updated — moved to ' + dest : 'Status updated', true);
        await loadLeads();
        refreshTabCounts();
    } catch(e) {
        showToast('Failed to update status: ' + e.message, false);
        renderTable();
    }
}

// ── Notes helpers ─────────────────────────────────────────────────
function openNotes(id) {
    editingId = null;
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
const iconBtn = 'w-7 h-7 rounded-lg flex items-center justify-center border transition';

function buildRow(lead) {
    const ed = editingId === lead.id;

    const cbCell = activeTab !== 'archived'
        ? '<td class="w-8"><input type="checkbox" class="row-checkbox accent-pink-600 w-4 h-4 cursor-pointer" data-id="' + lead.id + '" onchange="toggleRowSelect(this)" /></td>'
        : '';

    const hasNotes = !!(lead.notes && lead.notes.trim());
    const notesBtn = '<button onclick="openNotes(' + lead.id + ')" class="' + iconBtn + ' ' +
        (hasNotes ? 'bg-amber-500/15 hover:bg-amber-500/30 text-amber-600 border-amber-500/30' : 'bg-slate-700 hover:bg-slate-600 text-slate-400 border-slate-600') +
        '" title="' + (hasNotes ? escapeAttr(lead.notes.slice(0,80)) : 'Add notes') + '"><i class="fa-solid fa-note-sticky text-xs"></i></button>';

    const penBtn = '<button onclick="' + (ed ? 'cancelEdit()' : 'startEdit(' + lead.id + ')') + '" class="' + iconBtn + ' ' +
        (ed ? 'bg-brand/20 text-brand border-brand/40' : 'bg-slate-700 hover:bg-slate-600 text-slate-400 hover:text-brand border-slate-600') +
        '" title="' + (ed ? 'Cancel edit' : 'Edit owner info') + '"><i class="fa-solid fa-pen text-xs"></i></button>';

    const openBtn = '<a href="/Leads/' + lead.id + '" class="' + iconBtn + ' bg-slate-700 hover:bg-slate-600 text-slate-400 hover:text-brand border-slate-600" title="Open lead"><i class="fa-solid fa-up-right-from-square text-xs"></i></a>';

    const traceBtn = !lead.isEnriched && canEnrich
        ? '<button onclick="enrichLead(' + lead.id + ', this)" class="' + iconBtn + ' bg-orange-500/10 hover:bg-orange-500/30 text-orange-600 border-orange-500/20" title="Skip trace"><i class="fa-solid fa-magnifying-glass-dollar text-xs"></i></button>'
        : lead.isEnriched
            ? '<span class="w-7 h-7 flex items-center justify-center" title="Skip traced"><i class="fa-solid fa-circle-check text-xs text-green-500"></i></span>'
            : '';

    let ac;
    if (activeTab === 'archived') {
        ac = '<button onclick="restoreLead(' + lead.id + ', this)" class="' + iconBtn + ' bg-green-500/10 hover:bg-green-500/30 text-green-600 border-green-500/20" title="Restore"><i class="fa-solid fa-rotate-left text-xs"></i></button>' +
             notesBtn + openBtn;
    } else {
        ac = traceBtn + penBtn + notesBtn + openBtn;
    }
    ac = '<div class="flex items-center justify-center gap-1">' + ac + '</div>';

    const statusCell = activeTab !== 'archived'
        ? '<td>' + buildStatusDropdown(lead) + '</td>'
        : '<td><span class="text-xs text-slate-600 italic">archived</span></td>';

    const editExpRow = ed
        ? '<tr class="notes-row" data-edit-for="' + lead.id + '">' +
          '<td colspan="7" class="notes-row-cell">' +
          '<div class="flex items-center gap-2">' +
          '<span class="text-xs font-semibold text-slate-400 uppercase tracking-wide shrink-0"><i class="fa-solid fa-user mr-1.5"></i>Owner</span>' +
          '<input class="owner-input flex-1" id="eName_' + lead.id + '" value="' + escapeAttr(lead.ownerName || '') + '" placeholder="Owner name..." />' +
          '<input class="owner-input flex-1" id="ePhone_' + lead.id + '" value="' + escapeAttr(lead.ownerPhone || '') + '" placeholder="(555) 000-0000" />' +
          '<input class="owner-input flex-1" id="eEmail_' + lead.id + '" value="' + escapeAttr(lead.ownerEmail || '') + '" placeholder="owner@example.com" />' +
          '<button onclick="saveOwner(' + lead.id + ')" class="flex-shrink-0 ' + iconBtn + ' bg-green-500/20 hover:bg-green-500/40 text-green-600 border-green-500/30" title="Save"><i class="fa-solid fa-check text-xs"></i></button>' +
          '<button onclick="cancelEdit()" class="flex-shrink-0 ' + iconBtn + ' bg-slate-600/40 hover:bg-slate-600 text-slate-400 border-slate-600" title="Cancel"><i class="fa-solid fa-xmark text-xs"></i></button>' +
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
          '<button onclick="saveNotes(' + lead.id + ')" class="flex-shrink-0 mt-0.5 ' + iconBtn + ' bg-green-500/20 hover:bg-green-500/40 text-green-600 border-green-500/30" title="Save (Ctrl+Enter)"><i class="fa-solid fa-check text-xs"></i></button>' +
          '<button onclick="closeNotes()" class="flex-shrink-0 mt-0.5 ' + iconBtn + ' bg-slate-600/40 hover:bg-slate-600 text-slate-400 border-slate-600" title="Cancel (Esc)"><i class="fa-solid fa-xmark text-xs"></i></button>' +
          '</div></td></tr>'
        : '';

    const contactsOpen  = viewingContactsId === lead.id;
    const extraContacts = (lead.contacts || []).length > 1
        ? '<button onclick="toggleContacts(' + lead.id + ')" class="block text-xs font-semibold text-brand hover:underline">' +
          (contactsOpen ? 'Hide contacts' : '+' + (lead.contacts.length - 1) + ' more contact' + (lead.contacts.length > 2 ? 's' : '')) +
          ' <i class="fa-solid fa-chevron-' + (contactsOpen ? 'up' : 'down') + ' text-[9px]"></i></button>'
        : '';
    const phoneCell = lead.ownerPhone
        ? '<a href="tel:' + escapeAttr(lead.ownerPhone) + '" class="text-green-600 hover:text-green-700 whitespace-nowrap">' + escapeHtml(lead.ownerPhone) + '</a>' +
          contactWarning(lead) + extraContacts
        : '<span class="text-slate-600">—</span>';
    const emailCell = lead.ownerEmail
        ? '<a href="mailto:' + escapeAttr(lead.ownerEmail) + '" class="text-indigo-700 hover:text-indigo-700 truncate block" style="max-width:200px">' + escapeHtml(lead.ownerEmail) + '</a>'
        : '<span class="text-slate-600">—</span>';

    const rowSelectedCls = selectedIds.has(lead.id) ? ' row-selected bg-orange-500/5 border-l-2 border-orange-500' : '';
    return '<tr data-lead-id="' + lead.id + '" class="' + (ed ? 'editing' : '') + rowSelectedCls + '">' +
        cbCell +
        '<td class="font-medium text-slate-50" style="max-width:240px"><span class="block truncate" title="' + escapeAttr(lead.address) + '">' + escapeHtml(lead.address) + '</span>' +
        (lead.sourceAddress ? '<span class="block text-xs text-slate-500 truncate">from ' + escapeHtml(lead.sourceAddress) + '</span>' : '') + '</td>' +
        '<td>' + (lead.ownerName ? escapeHtml(lead.ownerName) : '<span class="text-slate-600">—</span>') + '</td>' +
        '<td>' + phoneCell + '</td>' +
        '<td class="hidden lg:table-cell">' + emailCell + '</td>' +
        statusCell +
        '<td class="sticky-actions">' + ac + '</td></tr>' +
        editExpRow + notesExpRow +
        (contactsOpen ? '<tr class="notes-row"><td colspan="7" class="notes-row-cell">' + buildContactList(lead) + '</td></tr>' : '');
}

function toggleContacts(id) {
    viewingContactsId = viewingContactsId === id ? null : id;
    renderTable();
}

// Every contact found for a lead: name, phone (type + DNC/litigator flags), email.
function buildContactList(lead) {
    var badge = function (text, title) {
        return '<span class="ml-1 px-1.5 py-0.5 rounded text-[10px] font-bold bg-red-100 text-red-700" title="' + title + '">' + text + '</span>';
    };
    var rows = (lead.contacts || []).map(function (c) {
        var phone = c.phone
            ? '<a href="tel:' + escapeAttr(c.phone) + '" class="text-green-600 hover:text-green-700 whitespace-nowrap">' +
              '<i class="fa-solid ' + (/mobile/i.test(c.phoneType || '') ? 'fa-mobile-screen' : 'fa-phone') + ' text-[10px] mr-1"></i>' +
              escapeHtml(c.phone) + '</a>' +
              (c.phoneType ? ' <span class="text-[10px] text-slate-500">' + escapeHtml(c.phoneType) + '</span>' : '') +
              (c.isDnc ? badge('DNC', 'On a Do Not Call list, do not cold call or text') : '')
            : '<span class="text-slate-600">—</span>';
        var email = c.email
            ? '<a href="mailto:' + escapeAttr(c.email) + '" class="text-indigo-700 hover:underline">' + escapeHtml(c.email) + '</a>'
            : '<span class="text-slate-600">—</span>';
        return '<div class="grid grid-cols-1 sm:grid-cols-3 gap-1 sm:gap-3 py-1.5 border-b border-slate-700/60 last:border-0 text-xs">' +
               '<span class="text-slate-200 font-semibold">' + escapeHtml(c.name || 'Unknown') +
               (c.isPrimary ? ' <span class="text-[10px] font-semibold text-green-600">Primary</span>' : '') +
               (c.isLitigator ? badge('LITIGATOR', 'Known TCPA litigator, do not contact') : '') + '</span>' +
               '<span>' + phone + '</span>' +
               '<span class="truncate">' + email + '</span></div>';
    }).join('');
    return '<div class="pl-1"><p class="text-[10px] font-bold text-slate-500 uppercase tracking-widest mb-1">' +
           '<i class="fa-solid fa-users mr-1"></i>All contacts</p>' + rows + '</div>';
}

// DNC / litigator badge for the lead's main phone (flags come from the skip trace)
function contactWarning(lead) {
    var c = (lead.contacts || []).find(function(x) { return x.phone === lead.ownerPhone; });
    if (!c) return '';
    if (c.isLitigator) return ' <span class="ml-1 px-1.5 py-0.5 rounded text-[10px] font-bold bg-red-100 text-red-700" title="Known TCPA litigator, do not contact">LITIGATOR</span>';
    if (c.isDnc)       return ' <span class="ml-1 px-1.5 py-0.5 rounded text-[10px] font-bold bg-red-100 text-red-700" title="On a Do Not Call list, do not cold call or text">DNC</span>';
    return '';
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

async function enrichLead(id, btn) {
    btn.disabled = true;
    var origHtml = btn.innerHTML;
    btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin text-xs"></i>';
    try {
        var resp = await fetch('/Leads/' + id + '/Enrich', { method:'POST' });
        var r    = await resp.json();
        if (!resp.ok) throw new Error(r.error || 'HTTP ' + resp.status);

        if (r.status === 'completed') {
            var found = [r.ownerName, r.ownerPhone, r.ownerEmail].filter(Boolean).join(' · ');
            showToast(found ? 'Found: ' + found + ' — moved to Traced' : 'Traced — no contact details found', !!found);
        } else {
            showToast('No data found for this address — moved to Traced', false);
        }
        await loadLeads();
        refreshTabCounts();
    } catch (e) {
        showToast('Skip trace failed: ' + e.message, false);
        btn.disabled = false; btn.innerHTML = origHtml;
    }
}

// ── Export ────────────────────────────────────────────────────────
// One row per contact (so multiple phones/emails from a skip trace all
// come through), falling back to the lead's owner fields.
function exportCSV(leadsOverride) {
    var header = ['Address','Owner / Contact Name','Phone','Phone Type','DNC','Litigator','Email','Contact Type','Is Primary',
                  'Year Built','Traced','Status','Notes','Source','Saved At'];

    function q(v) { return '"' + (v == null ? '' : v).toString().replace(/"/g,'""') + '"'; }

    var rows     = [header.join(',')];
    var srcLeads = Array.isArray(leadsOverride) ? leadsOverride : allLeads;

    srcLeads.forEach(function(l) {
        var tail = [q(l.yearBuilt), q(l.isEnriched ? 'Yes' : 'No'), q(l.status), q(l.notes), q(l.sourceAddress), q(l.savedAt)];
        var contacts = (l.contacts && l.contacts.length > 0) ? l.contacts : null;
        if (contacts) {
            contacts.forEach(function(c) {
                rows.push([q(l.address), q(c.name), q(c.phone), q(c.phoneType), q(c.isDnc ? 'Yes' : ''), q(c.isLitigator ? 'Yes' : ''),
                           q(c.email), q(c.contactType), q(c.isPrimary ? 'Yes' : 'No')].concat(tail).join(','));
            });
        } else {
            rows.push([q(l.address), q(l.ownerName), q(l.ownerPhone), q(''), q(''), q(''), q(l.ownerEmail), q('owner'), q('Yes')].concat(tail).join(','));
        }
    });

    var a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([rows.join('\r\n')], { type:'text/csv' }));
    a.download = 'Leads_' + new Date().toISOString().slice(0,10) + '.csv';
    a.click();
    URL.revokeObjectURL(a.href);
}

// ── Utility ───────────────────────────────────────────────────────
function setLoading(on) {
    document.getElementById('leadsLoading').classList.toggle('hidden', !on);
    if (on) {
        document.getElementById('leadsBody').innerHTML = '';
        document.getElementById('mobileCards').innerHTML = '';
    }
}

function escapeHtml(s) {
    if (s === null || s === undefined) return '';
    return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}
function escapeAttr(s) { return escapeHtml(s); }

// ── Mobile card view ──────────────────────────────────────────────
function buildMobileCard(lead) {
    const ed     = editingId === lead.id;
    const enotes = editingNotesId === lead.id;

    if (ed) {
        return '<div class="bg-slate-800 border border-brand/40 rounded-2xl p-4 shadow-md" data-lead-id="' + lead.id + '">' +
            '<p class="text-slate-50 font-semibold text-sm mb-3 truncate">' + escapeHtml(lead.address) + '</p>' +
            '<div class="space-y-2 mb-3">' +
            '<input class="owner-input w-full" id="eName_'  + lead.id + '" value="' + escapeAttr(lead.ownerName  || '') + '" placeholder="Owner name" />' +
            '<input class="owner-input w-full" id="ePhone_' + lead.id + '" value="' + escapeAttr(lead.ownerPhone || '') + '" placeholder="(555) 000-0000" />' +
            '<input class="owner-input w-full" id="eEmail_' + lead.id + '" value="' + escapeAttr(lead.ownerEmail || '') + '" placeholder="owner@example.com" />' +
            '</div>' +
            '<div class="flex gap-2">' +
            '<button onclick="saveOwner(' + lead.id + ')" class="flex-1 py-2.5 rounded-xl bg-green-500/20 border border-green-500/30 text-green-600 text-sm font-semibold hover:bg-green-500/30 transition"><i class="fa-solid fa-check mr-1"></i>Save</button>' +
            '<button onclick="cancelEdit()" class="py-2.5 px-4 rounded-xl bg-slate-700 border border-slate-600 text-slate-300 text-sm font-semibold hover:bg-slate-600 transition"><i class="fa-solid fa-xmark"></i></button>' +
            '</div></div>';
    }

    const phoneHtml = lead.ownerPhone
        ? contactWarning(lead).replace('ml-1 ', 'self-center ') + '<a href="tel:' + escapeAttr(lead.ownerPhone) + '" class="flex-1 flex items-center justify-center gap-2 py-3 rounded-xl bg-green-500/15 border border-green-500/30 text-green-600 text-sm font-semibold active:bg-green-500/30 transition"><i class="fa-solid fa-phone"></i>' + escapeHtml(lead.ownerPhone) + '</a>'
        : '<span class="flex-1 flex items-center justify-center gap-2 py-3 rounded-xl bg-slate-700/40 border border-slate-600/60 text-slate-500 text-sm"><i class="fa-solid fa-phone-slash"></i>No phone yet</span>';

    const tracedBadge = lead.isEnriched
        ? '<span class="text-xs text-green-600 font-semibold"><i class="fa-solid fa-circle-check mr-1"></i>Traced</span>'
        : '<span class="text-xs text-slate-500">Not traced</span>';

    const actionBtns = activeTab === 'archived'
        ? '<button onclick="restoreLead(' + lead.id + ', this)" class="flex-1 py-2 rounded-xl bg-green-500/10 border border-green-500/20 text-green-600 text-xs font-semibold hover:bg-green-500/20 transition"><i class="fa-solid fa-rotate-left mr-1"></i>Restore</button>'
        : (!lead.isEnriched && canEnrich
              ? '<button onclick="enrichLead(' + lead.id + ', this)" class="flex-1 py-2 rounded-xl bg-orange-500/10 border border-orange-500/20 text-orange-600 text-xs font-semibold hover:bg-orange-500/20 active:bg-orange-500/30 transition"><i class="fa-solid fa-magnifying-glass-dollar mr-1"></i>Skip Trace</button>'
              : '') +
          '<button onclick="startEdit(' + lead.id + ')" class="flex-1 py-2 rounded-xl bg-slate-700 border border-slate-600 text-slate-300 text-xs font-semibold hover:bg-slate-600 transition"><i class="fa-solid fa-pen mr-1"></i>Edit</button>' +
          '<a href="/Leads/' + lead.id + '" class="py-2 px-3.5 rounded-xl bg-slate-700 border border-slate-600 text-slate-300 text-xs font-semibold hover:bg-slate-600 transition"><i class="fa-solid fa-up-right-from-square"></i></a>';

    const statusRow = activeTab !== 'archived'
        ? '<div class="flex items-center justify-between mt-3 pt-3 border-t border-slate-700/60">' +
          '<span class="text-xs text-slate-500 font-semibold uppercase tracking-wide">Status</span>' +
          buildStatusDropdown(lead) +
          '</div>'
        : '';

    const hasNotes = !!(lead.notes && lead.notes.trim());
    const notesSection = enotes
        ? '<div class="mt-3 pt-3 border-t border-slate-700/60 space-y-2">' +
          '<textarea id="notesArea_' + lead.id + '" class="notes-textarea w-full" rows="3" placeholder="Add notes about this lead">' + escapeHtml(lead.notes || '') + '</textarea>' +
          '<div class="flex gap-2">' +
          '<button onclick="saveNotes(' + lead.id + ')" class="flex-1 py-2 rounded-xl bg-green-500/20 border border-green-500/30 text-green-600 text-xs font-semibold hover:bg-green-500/30 transition"><i class="fa-solid fa-check mr-1"></i>Save Note</button>' +
          '<button onclick="closeNotes()" class="py-2 px-3.5 rounded-xl bg-slate-700 border border-slate-600 text-slate-300 text-xs font-semibold hover:bg-slate-600 transition"><i class="fa-solid fa-xmark"></i></button>' +
          '</div></div>'
        : '<div class="mt-3 pt-3 border-t border-slate-700/60">' +
          (hasNotes ? '<p class="text-xs text-slate-400 mb-2 leading-relaxed"><i class="fa-solid fa-note-sticky mr-1.5 text-amber-600"></i>' + escapeHtml(lead.notes.slice(0,100)) + (lead.notes.length > 100 ? '...' : '') + '</p>' : '') +
          '<button onclick="openNotes(' + lead.id + ')" class="w-full py-2 rounded-xl bg-slate-700/60 border border-slate-600/60 text-xs font-semibold hover:bg-slate-700 transition ' + (hasNotes ? 'text-amber-600' : 'text-slate-400') + '">' +
          '<i class="fa-solid fa-note-sticky mr-1.5"></i>' + (hasNotes ? 'Edit Note' : 'Add Note') + '</button>' +
          '</div>';

    const cb = activeTab !== 'archived'
        ? '<input type="checkbox" class="row-checkbox accent-pink-600 w-4 h-4 mt-0.5 cursor-pointer" data-id="' + lead.id + '" onchange="toggleRowSelect(this)" />'
        : '';

    return '<div class="bg-slate-800 border border-slate-700/60 rounded-2xl p-4 shadow-md" data-lead-id="' + lead.id + '">' +
        '<div class="flex items-start justify-between gap-2 mb-3">' +
        cb +
        '<div class="flex-1 min-w-0">' +
        '<p class="font-semibold text-slate-50 text-sm leading-tight">' + escapeHtml(lead.address) + '</p>' +
        (lead.ownerName ? '<p class="text-xs text-slate-400 mt-0.5"><i class="fa-solid fa-user mr-1"></i>' + escapeHtml(lead.ownerName) + '</p>' : '') +
        '</div>' +
        tracedBadge +
        '</div>' +
        '<div class="flex gap-2 mb-3">' + phoneHtml + '</div>' +
        '<div class="flex items-center gap-2">' + actionBtns + '</div>' +
        ((lead.contacts || []).length > 1
            ? '<div class="mt-3">' +
              '<button onclick="toggleContacts(' + lead.id + ')" class="w-full py-2 rounded-xl bg-slate-700/60 border border-slate-600/60 text-xs font-semibold text-brand hover:bg-slate-700 transition">' +
              '<i class="fa-solid fa-users mr-1.5"></i>' + (viewingContactsId === lead.id ? 'Hide contacts' : 'All ' + lead.contacts.length + ' contacts') + '</button>' +
              (viewingContactsId === lead.id ? '<div class="mt-2">' + buildContactList(lead) + '</div>' : '') +
              '</div>'
            : '') +
        statusRow + notesSection +
        '</div>';
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

// ── Email selected leads (templates from /Email/Templates) ─────────
var MAX_EMAILS_PER_SEND = 50;   // matches EmailController.MaxPerSend

function selectedEmailableLeads() {
    return allLeads.filter(function (l) { return selectedIds.has(l.id) && l.ownerEmail; });
}

async function openEmailModal() {
    var leads = selectedEmailableLeads();
    if (!leads.length) return;
    document.getElementById('emailModal').classList.remove('hidden');

    var resp = await fetch('/Email/Templates/List', { cache: 'no-store' });
    var templates = resp.ok ? await resp.json() : [];
    var none = templates.length === 0;
    document.getElementById('emailNoTemplates').classList.toggle('hidden', !none);
    document.getElementById('emailPickRow').classList.toggle('hidden', none);
    document.getElementById('emailPreviewBox').classList.toggle('hidden', none);
    document.getElementById('emailSendBtn').disabled = none;

    var sel = document.getElementById('emailTemplateSelect');
    sel.innerHTML = templates.map(function (t) {
        return '<option value="' + t.id + '">' + escapeHtml(t.name) + '</option>';
    }).join('');
    sel._templates = templates;

    var skipped = selectedIds.size - leads.length;
    var over    = leads.length > MAX_EMAILS_PER_SEND;
    document.getElementById('emailCounts').innerHTML =
        '<b class="text-slate-200">' + leads.length + '</b> selected lead' + (leads.length === 1 ? ' has' : 's have') + ' an email address' +
        (skipped ? ' (' + skipped + ' without one will be skipped)' : '') + '.' +
        (over ? ' <span class="text-red-600 font-semibold">Select ' + MAX_EMAILS_PER_SEND + ' or fewer per send.</span>' : '');
    document.getElementById('emailSendBtn').disabled = none || over;
    document.getElementById('emailSendBtn').innerHTML =
        '<i class="fa-solid fa-paper-plane mr-1.5"></i>Send to ' + Math.min(leads.length, MAX_EMAILS_PER_SEND);

    if (!none) previewEmail();
}

function closeEmailModal() {
    document.getElementById('emailModal').classList.add('hidden');
}

// Preview with the first selected lead's real details.
async function previewEmail() {
    var sel  = document.getElementById('emailTemplateSelect');
    var t    = (sel._templates || []).find(function (x) { return String(x.id) === sel.value; });
    var lead = selectedEmailableLeads()[0];
    if (!t || !lead) return;

    var resp = await fetch('/Email/Preview', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ subject: t.subject, body: t.body, leadId: lead.id })
    });
    if (!resp.ok) return;
    var p = await resp.json();
    document.getElementById('emailPvLead').textContent    = lead.ownerName || lead.address;
    document.getElementById('emailPvTo').textContent      = p.to || '';
    document.getElementById('emailPvSubject').textContent = p.subject || '';
    document.getElementById('emailPvBody').innerHTML      = p.html;   // server-rendered; values HTML-encoded
    document.getElementById('emailAddressWarning').classList.toggle('hidden', !p.missingAddress);
    if (p.missingAddress) document.getElementById('emailSendBtn').disabled = true;
}

async function sendEmails() {
    var leads = selectedEmailableLeads();
    var templateId = parseInt(document.getElementById('emailTemplateSelect').value, 10);
    if (!leads.length || !templateId) return;
    if (!confirm('Send this email to ' + leads.length + ' lead' + (leads.length === 1 ? '' : 's') + '?')) return;

    var btn = document.getElementById('emailSendBtn');
    var orig = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = '<i class="fa-solid fa-spinner fa-spin mr-1.5"></i>Sending…';
    try {
        var resp = await fetch('/Email/Send', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ templateId: templateId, leadIds: leads.map(function (l) { return l.id; }) })
        });
        var r = await resp.json();
        if (!resp.ok) throw new Error(r.error || 'HTTP ' + resp.status);

        var notes = [];
        if (r.skippedDuplicate) notes.push(r.skippedDuplicate + ' already got this template');
        if (r.skippedOptOut)    notes.push(r.skippedOptOut + ' unsubscribed');
        if (r.noEmail)          notes.push(r.noEmail + ' had no valid email');
        if (r.failed && r.failed.length) notes.push(r.failed.length + ' failed');
        showToast('Sent ' + r.sent + ' email' + (r.sent === 1 ? '' : 's') + (notes.length ? ' (' + notes.join(', ') + ')' : ''),
                  r.sent > 0 && !(r.failed && r.failed.length));
        closeEmailModal();
        clearSelection();
        await loadLeads();
        refreshTabCounts();
    } catch (e) {
        showToast('Send failed: ' + e.message, false);
    } finally {
        btn.disabled = false;
        btn.innerHTML = orig;
    }
}
