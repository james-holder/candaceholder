// Views/Email/Templates.cshtml — list, edit, preview and save email templates.
var templates   = [];
var currentId   = null;      // null = unsaved new template
var lastField   = null;      // subject or body — where variable chips insert
var previewTimer = null;

document.addEventListener('DOMContentLoaded', function () {
    loadTemplates();

    document.querySelectorAll('.tpl-field').forEach(function (f) {
        f.addEventListener('focus', function () { lastField = f; });
        f.addEventListener('input', schedulePreview);
    });
    document.getElementById('tplName').addEventListener('input', markDirty);

    document.querySelectorAll('.var-chip').forEach(function (chip) {
        chip.addEventListener('click', function () { insertVariable('{{' + chip.dataset.var + '}}'); });
    });
});

async function loadTemplates(selectId) {
    var resp = await fetch('/Email/Templates/List', { cache: 'no-store' });
    templates = resp.ok ? await resp.json() : [];
    renderList();
    var pick = templates.find(function (t) { return t.id === selectId; }) || (currentId == null ? null : templates.find(function (t) { return t.id === currentId; }));
    if (pick) openTemplate(pick.id);
    else if (!templates.length && window.CAN_EDIT) newTemplate();
}

function renderList() {
    var el = document.getElementById('templateList');
    if (!templates.length) {
        el.innerHTML = '<p class="text-sm text-slate-500">No templates yet.</p>';
        return;
    }
    el.innerHTML = templates.map(function (t) {
        var active = t.id === currentId;
        return '<button onclick="openTemplate(' + t.id + ')" class="w-full text-left rounded-xl border px-3 py-2.5 transition ' +
               (active ? 'border-pink-300 bg-pink-50' : 'border-slate-700 bg-white hover:border-pink-200') + '">' +
               '<p class="text-sm font-semibold text-slate-50 truncate">' + esc(t.name) + '</p>' +
               '<p class="text-xs text-slate-500 truncate">' + esc(t.subject) + '</p></button>';
    }).join('');
}

function openTemplate(id) {
    var t = templates.find(function (x) { return x.id === id; });
    if (!t) return;
    currentId = t.id;
    document.getElementById('tplName').value    = t.name;
    document.getElementById('tplSubject').value = t.subject;
    document.getElementById('tplBody').value    = t.body;
    showEditor(true);
    renderList();
    setSaveState('');
    refreshPreview();
}

function newTemplate() {
    currentId = null;
    document.getElementById('tplName').value    = '';
    document.getElementById('tplSubject').value = 'Question about {{street}}';
    document.getElementById('tplBody').value    =
        'Hi {{first_name|there}},\n\n' +
        'My name is {{sender_name}} with {{company_name}}. I\'m reaching out about your property at {{property_address}}.\n\n' +
        '...\n\n' +
        'Thanks,\n{{sender_name}}';
    showEditor(true);
    renderList();
    setSaveState('New template, not saved yet');
    refreshPreview();
    document.getElementById('tplName').focus();
}

function showEditor(on) {
    document.getElementById('editorPanel').classList.toggle('hidden', !on);
    document.getElementById('emptyPanel').classList.toggle('hidden', on);
    var del = document.getElementById('deleteBtn');
    if (del) del.classList.toggle('hidden', currentId == null);
}

function insertVariable(text) {
    var f = lastField || document.getElementById('tplBody');
    var start = f.selectionStart ?? f.value.length, end = f.selectionEnd ?? f.value.length;
    f.value = f.value.slice(0, start) + text + f.value.slice(end);
    f.focus();
    f.selectionStart = f.selectionEnd = start + text.length;
    schedulePreview();
}

function markDirty() { setSaveState('Unsaved changes'); }

function schedulePreview() {
    markDirty();
    clearTimeout(previewTimer);
    previewTimer = setTimeout(refreshPreview, 300);
}

async function refreshPreview() {
    var resp = await fetch('/Email/Preview', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            subject: document.getElementById('tplSubject').value,
            body:    document.getElementById('tplBody').value
        })
    });
    if (!resp.ok) return;
    var p = await resp.json();
    document.getElementById('pvTo').textContent      = p.to || '';
    document.getElementById('pvSubject').textContent = p.subject || '(no subject)';
    document.getElementById('pvBody').innerHTML      = p.html;   // server-rendered; values are HTML-encoded
    document.getElementById('addressWarning').classList.toggle('hidden', !p.missingAddress);
}

async function saveTemplate() {
    var btn = document.getElementById('saveBtn');
    btn.disabled = true;
    try {
        var resp = await fetch('/Email/Templates/Save', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                id:      currentId,
                name:    document.getElementById('tplName').value,
                subject: document.getElementById('tplSubject').value,
                body:    document.getElementById('tplBody').value
            })
        });
        var r = await resp.json();
        if (!resp.ok) throw new Error(r.error || 'HTTP ' + resp.status);
        currentId = r.id;
        await loadTemplates(r.id);
        setSaveState('Saved');
        showToast('Template saved', true);
    } catch (e) {
        showToast('Save failed: ' + e.message, false);
    } finally {
        btn.disabled = false;
    }
}

async function deleteTemplate() {
    if (currentId == null) return;
    var t = templates.find(function (x) { return x.id === currentId; });
    if (!confirm('Delete the template "' + (t ? t.name : '') + '"?')) return;
    var resp = await fetch('/Email/Templates/' + currentId, { method: 'DELETE' });
    if (!resp.ok) { showToast('Delete failed', false); return; }
    currentId = null;
    showEditor(false);
    await loadTemplates();
    showToast('Template deleted', true);
}

function setSaveState(text) {
    var el = document.getElementById('saveState');
    if (el) el.textContent = text;
}

function esc(s) {
    return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

var _toastTimer = null;
function showToast(msg, success) {
    var toast = document.getElementById('toast');
    if (_toastTimer) clearTimeout(_toastTimer);
    toast.className = success ? 'success' : 'error';
    document.getElementById('toastIcon').className = 'fa-solid ' + (success ? 'fa-circle-check' : 'fa-circle-xmark');
    document.getElementById('toastMsg').textContent = msg;
    toast.offsetHeight;
    toast.classList.add('show');
    _toastTimer = setTimeout(function () { toast.classList.remove('show'); }, 3500);
}
