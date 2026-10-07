// Page logic for Views/Admin/Index.cshtml — emailing team members, with
// admin-only templates (stored server-side; "Manage templates" edits them).
// The formatting editor is wwwroot/js/rich-editor.js.

var adminTemplates = [];
var msgEditor = null;      // message in the Email pop-up
var tplEditor = null;      // message in Manage templates
var editingTplId = null;   // template open in Manage templates (null = new)

document.addEventListener('DOMContentLoaded', function () {
    msgEditor = new RichEditor(document.getElementById('adminMsgEditor'));
    tplEditor = new RichEditor(document.getElementById('adminTplEditor'));
    document.querySelectorAll('.admin-var-chip').forEach(function (chip) {
        chip.addEventListener('click', function () { tplEditor.insertText('{{' + chip.dataset.var + '}}'); });
    });
    ['emailModal', 'manageModal'].forEach(function (id) {
        document.getElementById(id).addEventListener('click', function (e) {
            if (e.target === this) (id === 'emailModal' ? closeEmailModal : closeManageTemplates)();
        });
    });
    loadAdminTemplates();
});

async function loadAdminTemplates() {
    var resp = await fetch('/Admin/Templates/List', { cache: 'no-store' });
    adminTemplates = resp.ok ? await resp.json() : [];
    var sel = document.getElementById('emailTemplateSelect');
    var current = sel.value;
    sel.innerHTML = '<option value="">Custom (blank)</option>' + adminTemplates.map(function (t) {
        return '<option value="' + t.id + '">' + esc(t.name) + '</option>';
    }).join('');
    if (adminTemplates.some(function (t) { return String(t.id) === current; })) sel.value = current;
    renderAdminTplList();
}

// ── Email pop-up ──────────────────────────────────────────────────
function openEmailModal(btn) {
    var form = document.getElementById('emailModalForm');
    form.action = '/Admin/Users/' + btn.dataset.id + '/Email';
    document.getElementById('emailModalTo').value = btn.dataset.email || '';
    document.getElementById('emailModalName').textContent = '— ' + (btn.dataset.name || btn.dataset.email || '');
    resetComposer();
}

function openBroadcastEmailModal(count) {
    var form = document.getElementById('emailModalForm');
    form.action = '/Admin/EmailAll';
    document.getElementById('emailModalTo').value = 'All users (' + count + ' recipient' + (count === 1 ? '' : 's') + ', each gets their own copy)';
    document.getElementById('emailModalName').textContent = '— All Users';
    resetComposer();
}

function resetComposer() {
    document.getElementById('emailTemplateSelect').value = '';
    applyEmailTemplate();
    document.getElementById('emailModal').classList.remove('hidden');
}

function closeEmailModal() {
    document.getElementById('emailModal').classList.add('hidden');
}

function applyEmailTemplate() {
    var id = document.getElementById('emailTemplateSelect').value;
    var t  = adminTemplates.find(function (x) { return String(x.id) === id; });
    document.getElementById('emailSubject').value = t ? t.subject : '';
    msgEditor.setHtml(t ? t.body : '');
}

// The editor isn't a form field: copy its HTML into the hidden input.
function beforeSendAdminEmail() {
    var html = msgEditor.html();
    if (!msgEditor.body.textContent.trim() && !msgEditor.body.querySelector('img')) {
        showToast('Write a message first', false);
        return false;
    }
    document.getElementById('emailBodyHtml').value = html;
    return true;
}

// ── Manage templates ──────────────────────────────────────────────
function openManageTemplates() {
    var id = document.getElementById('emailTemplateSelect').value;
    if (id) openAdminTemplate(Number(id));
    else if (adminTemplates.length) openAdminTemplate(adminTemplates[0].id);
    else newAdminTemplate();
    document.getElementById('manageModal').classList.remove('hidden');
}

function closeManageTemplates() {
    document.getElementById('manageModal').classList.add('hidden');
}

function renderAdminTplList() {
    var el = document.getElementById('adminTplList');
    if (!el) return;
    el.innerHTML = adminTemplates.length ? adminTemplates.map(function (t) {
        var active = t.id === editingTplId;
        return '<button type="button" onclick="openAdminTemplate(' + t.id + ')" class="w-full text-left rounded-xl border px-3 py-2 text-sm transition ' +
               (active ? 'border-pink-300 bg-pink-50 font-semibold text-slate-50' : 'border-slate-700 bg-white text-slate-300 hover:border-pink-200') + '">' +
               esc(t.name) + '</button>';
    }).join('') : '<p class="text-xs text-slate-500">No templates yet.</p>';
}

function openAdminTemplate(id) {
    var t = adminTemplates.find(function (x) { return x.id === id; });
    if (!t) return;
    editingTplId = t.id;
    document.getElementById('atName').value    = t.name;
    document.getElementById('atSubject').value = t.subject;
    tplEditor.setHtml(t.body);
    document.getElementById('atDeleteBtn').classList.remove('hidden');
    renderAdminTplList();
}

function newAdminTemplate() {
    editingTplId = null;
    document.getElementById('atName').value    = '';
    document.getElementById('atSubject').value = '';
    tplEditor.setHtml('<div>Hi {{first_name|there}},</div><div><br></div><div><br></div><div>Thanks,</div><div>{{signature}}</div>');
    document.getElementById('atDeleteBtn').classList.add('hidden');
    renderAdminTplList();
    document.getElementById('atName').focus();
}

async function saveAdminTemplate() {
    var btn = document.getElementById('atSaveBtn');
    btn.disabled = true;
    try {
        var resp = await fetch('/Admin/Templates/Save', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                id:      editingTplId,
                name:    document.getElementById('atName').value,
                subject: document.getElementById('atSubject').value,
                body:    tplEditor.html()
            })
        });
        var r = await resp.json();
        if (!resp.ok) throw new Error(r.error || 'HTTP ' + resp.status);
        editingTplId = r.id;
        await loadAdminTemplates();
        openAdminTemplate(r.id);
        // Use it straight away in the Email pop-up
        document.getElementById('emailTemplateSelect').value = String(r.id);
        applyEmailTemplate();
        showToast('Template saved', true);
    } catch (e) {
        showToast('Save failed: ' + e.message, false);
    } finally {
        btn.disabled = false;
    }
}

async function deleteAdminTemplate() {
    if (editingTplId == null) return;
    var t = adminTemplates.find(function (x) { return x.id === editingTplId; });
    if (!confirm('Delete the template "' + (t ? t.name : '') + '"?')) return;
    var resp = await fetch('/Admin/Templates/' + editingTplId, { method: 'DELETE' });
    if (!resp.ok) { showToast('Delete failed', false); return; }
    var wasSelected = document.getElementById('emailTemplateSelect').value === String(editingTplId);
    editingTplId = null;
    await loadAdminTemplates();
    if (wasSelected) { document.getElementById('emailTemplateSelect').value = ''; applyEmailTemplate(); }
    if (adminTemplates.length) openAdminTemplate(adminTemplates[0].id); else newAdminTemplate();
    showToast('Template deleted', true);
}

// ── Helpers ───────────────────────────────────────────────────────
function esc(s) {
    return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

var _toastTimer = null;
function showToast(msg, success) {
    var t = document.getElementById('adminToast');
    t.textContent = msg;
    t.className = 'fixed bottom-5 left-1/2 -translate-x-1/2 z-[90] px-4 py-2.5 rounded-xl shadow-lg text-sm font-semibold ' +
                  (success ? 'bg-teal-600 text-white' : 'bg-red-600 text-white');
    clearTimeout(_toastTimer);
    _toastTimer = setTimeout(function () { t.classList.add('hidden'); }, 3500);
}
