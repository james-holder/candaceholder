// Views/Email/Templates.cshtml — list, edit, preview and save email templates.
var templates   = [];
var currentId   = null;      // null = unsaved new template
var lastField   = null;      // subject or body — where variable chips insert
var previewTimer = null;
var editor      = null;      // the message box (contenteditable)
var savedRange  = null;      // last cursor/selection inside the editor

document.addEventListener('DOMContentLoaded', function () {
    editor = document.getElementById('tplBody');
    initEditor();
    loadTemplates();

    document.querySelectorAll('.tpl-field').forEach(function (f) {
        f.addEventListener('focus', function () { lastField = f; });
        f.addEventListener('input', schedulePreview);
    });
    document.getElementById('tplName').addEventListener('input', markDirty);
    document.querySelectorAll('input[name=tplStyle]').forEach(function (r) {
        r.addEventListener('change', function () { markDirty(); refreshPreview(); });
    });

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
    setBody(t.body, t.isHtml);
    setBranded(t.branded !== false);
    showEditor(true);
    renderList();
    setSaveState('');
    refreshPreview();
}

function newTemplate() {
    currentId = null;
    document.getElementById('tplName').value    = '';
    document.getElementById('tplSubject').value = 'Question about {{street}}';
    setBody(
        'Hi {{first_name|there}},\n\n' +
        'My name is {{sender_name}} with {{company_name}}. I\'m reaching out about your property at {{property_address}}.\n\n' +
        '...\n\n' +
        'Thanks,\n{{sender_name}}', false);
    setBranded(true);
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

// Variable chips insert into the subject or the message, wherever the cursor was last.
function insertVariable(text) {
    var f = lastField || editor;
    if (f === editor) {
        restoreSelection();
        document.execCommand('insertText', false, text);
        afterEdit();
        return;
    }
    var start = f.selectionStart ?? f.value.length, end = f.selectionEnd ?? f.value.length;
    f.value = f.value.slice(0, start) + text + f.value.slice(end);
    f.focus();
    f.selectionStart = f.selectionEnd = start + text.length;
    schedulePreview();
}

// ── Formatting editor ─────────────────────────────────────────────
// The browser's built-in editing, set to write inline styles (what email
// apps keep). The server cleans the HTML before saving or sending.
function initEditor() {
    editor.addEventListener('input', schedulePreview);
    editor.addEventListener('focus', function () { lastField = editor; });
    // Paste as plain text so other sites' fonts and layouts don't come along
    editor.addEventListener('paste', function (e) {
        e.preventDefault();
        document.execCommand('insertText', false, (e.clipboardData || window.clipboardData).getData('text/plain'));
    });
    document.addEventListener('selectionchange', function () {
        var sel = window.getSelection();
        if (sel.rangeCount && editor.contains(sel.getRangeAt(0).commonAncestorContainer)) {
            savedRange = sel.getRangeAt(0).cloneRange();
            updateToolbarState();
        }
    });
    try { document.execCommand('defaultParagraphSeparator', false, 'div'); } catch (e) {}

    var tb = document.getElementById('editorToolbar');
    if (!tb) return;   // read-only
    // Clicking a toolbar button must not move focus (and the selection) out of the editor
    tb.addEventListener('mousedown', function (e) { if (!e.target.closest('select, input')) e.preventDefault(); });
    tb.querySelectorAll('[data-cmd]').forEach(function (b) {
        b.addEventListener('click', function () { exec(b.dataset.cmd); });
    });
    tb.querySelectorAll('[data-menu]').forEach(function (b) {
        b.addEventListener('click', function (e) { e.stopPropagation(); toggleMenu(b.dataset.menu); });
    });
    tb.querySelectorAll('[data-color]').forEach(function (b) {
        b.addEventListener('click', function () { exec('foreColor', b.dataset.color); closeMenus(); });
    });
    tb.querySelectorAll('[data-highlight]').forEach(function (b) {
        b.addEventListener('click', function () { exec('hiliteColor', b.dataset.highlight); closeMenus(); });
    });
    document.getElementById('tbCustomColor').addEventListener('change', function () { exec('foreColor', this.value); closeMenus(); });
    document.getElementById('tbFont').addEventListener('change', function () { if (this.value) exec('fontName', this.value); this.value = ''; });
    document.getElementById('tbSize').addEventListener('change', function () { if (this.value) setFontSize(this.value); this.value = ''; });
    document.addEventListener('click', function (e) { if (!e.target.closest('.tb-menu')) closeMenus(); });
}

// Put the cursor back where it was in the message. Only needed when focus
// went elsewhere (a dropdown, a prompt); if it's still in the editor, use it.
function restoreSelection() {
    var sel = window.getSelection();
    var inEditor = sel.rangeCount && editor.contains(sel.getRangeAt(0).commonAncestorContainer);
    editor.focus();
    if (inEditor || !savedRange) return;
    sel.removeAllRanges();
    sel.addRange(savedRange);
}

function exec(cmd, value) {
    restoreSelection();
    document.execCommand('styleWithCSS', false, true);
    document.execCommand(cmd, false, value == null ? null : value);
    afterEdit();
}

function afterEdit() {
    schedulePreview();
    updateToolbarState();
}

// execCommand only knows sizes 1–7: mark the selection as size 7, then swap
// those marks for exact pixel sizes.
function setFontSize(px) {
    restoreSelection();
    document.execCommand('styleWithCSS', false, false);
    document.execCommand('fontSize', false, '7');
    document.execCommand('styleWithCSS', false, true);
    var spans = [];
    editor.querySelectorAll('font[size="7"]').forEach(function (f) {
        var span = document.createElement('span');
        span.style.fontSize = px + 'px';
        while (f.firstChild) span.appendChild(f.firstChild);
        f.replaceWith(span);
        spans.push(span);
    });
    // Keep the same text selected so another format can be applied straight away
    if (spans.length) {
        var r = document.createRange();
        r.setStartBefore(spans[0]);
        r.setEndAfter(spans[spans.length - 1]);
        var sel = window.getSelection();
        sel.removeAllRanges();
        sel.addRange(r);
        savedRange = r.cloneRange();
    }
    afterEdit();
}

function updateToolbarState() {
    document.querySelectorAll('#editorToolbar [data-cmd]').forEach(function (b) {
        var on = false;
        try { on = document.queryCommandState(b.dataset.cmd); } catch (e) {}
        b.classList.toggle('active', on);
    });
}

function toggleMenu(id) {
    var m = document.getElementById(id), open = m.classList.contains('hidden');
    closeMenus();
    m.classList.toggle('hidden', !open);
}
function closeMenus() {
    document.querySelectorAll('.tb-menu').forEach(function (m) { m.classList.add('hidden'); });
}

function cleanUrl(url, allowContact) {
    url = (url || '').trim();
    if (!url || url === 'https://') return null;
    if (allowContact && /^(mailto|tel):/i.test(url)) return url;
    return /^https?:\/\//i.test(url) ? url : 'https://' + url;
}

function addLink() {
    var sel = window.getSelection();
    var hasText = savedRange && !savedRange.collapsed;
    var url = cleanUrl(prompt('Link address (website, or mailto: / tel:)', 'https://'), true);
    if (!url) return;
    restoreSelection();
    if (hasText) document.execCommand('createLink', false, url);
    else document.execCommand('insertHTML', false, '<a href="' + esc(url) + '">' + esc(url.replace(/^(https?:\/\/|mailto:|tel:)/i, '')) + '</a>&nbsp;');
    afterEdit();
}

// Buttons and the logo are short codes on their own line; the preview shows the real thing.
function addButton() {
    var text = prompt('Button text', 'Book a time');
    if (!text) return;
    var url = cleanUrl(prompt('Where should the button go?', 'https://'), false);
    if (!url) return;
    insertBlock('[' + text.replace(/[\[\]]/g, '') + '](' + url.replace(/[\s)]/g, '') + ')');
}
function insertLogo() { insertBlock('{{logo}}'); }

function insertBlock(code) {
    restoreSelection();
    var sel = window.getSelection();
    var line = sel.anchorNode && (sel.anchorNode.nodeType === 1 ? sel.anchorNode : sel.anchorNode.parentElement);
    var onEmptyLine = line && line !== editor && line.closest('#tplBody') && line.textContent.trim() === '';
    if (editor.textContent.trim() === '' || onEmptyLine) {
        document.execCommand('insertText', false, code);
    } else {
        document.execCommand('insertParagraph');
        document.execCommand('insertText', false, code);
    }
    document.execCommand('insertParagraph');
    afterEdit();
}

// Message HTML for saving/preview. Text typed on the very first line isn't
// wrapped in a <div> by the browser, so wrap loose top-level pieces.
function bodyHtml() {
    var out = document.createElement('div'), line = null;
    Array.from(editor.cloneNode(true).childNodes).forEach(function (n) {
        var block = n.nodeType === 1 && /^(DIV|P|UL|OL|BLOCKQUOTE|HR)$/.test(n.nodeName);
        if (block) { out.appendChild(n); line = null; return; }
        if (!line) { line = document.createElement('div'); out.appendChild(line); }
        line.appendChild(n);
    });
    return out.innerHTML;
}

// Older templates are plain text: one <div> per line, **bold** made bold.
function setBody(body, isHtml) {
    editor.innerHTML = isHtml ? body : (body || '').split(/\r?\n/).map(function (l) {
        return '<div>' + (l ? esc(l).replace(/\*\*(.+?)\*\*/g, '<b>$1</b>') : '<br>') + '</div>';
    }).join('');
    savedRange = null;
}

function markDirty() { setSaveState('Unsaved changes'); }

function isBranded() {
    var r = document.querySelector('input[name=tplStyle]:checked');
    return !r || r.value === 'branded';
}
function setBranded(on) {
    document.querySelector('input[name=tplStyle][value=' + (on ? 'branded' : 'plain') + ']').checked = true;
}

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
            body:    bodyHtml(),
            isHtml:  true,
            branded: isBranded()
        })
    });
    if (!resp.ok) return;
    var p = await resp.json();
    document.getElementById('pvTo').textContent      = p.to || '';
    document.getElementById('pvSubject').textContent = p.subject || '(no subject)';
    document.getElementById('pvBody').innerHTML      = p.html;   // server-rendered; values are HTML-encoded
    document.getElementById('addressWarning').classList.toggle('hidden', !p.missingAddress);
    document.getElementById('svgLogoWarning').classList.toggle('hidden', !(p.svgLogo && (isBranded() || p.noLogo)));
    document.getElementById('noLogoWarning').classList.toggle('hidden', !p.noLogo || p.svgLogo);
    // Plain emails have no outer padding of their own
    document.getElementById('pvBody').classList.toggle('p-4', !isBranded());
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
                body:    bodyHtml(),
                isHtml:  true,
                branded: isBranded()
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
