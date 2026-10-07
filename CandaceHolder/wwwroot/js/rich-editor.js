// rich-editor.js — the formatting editor used for email templates and signatures.
//
// Markup: a wrapper holding Views/Shared/_RichToolbar.cshtml (.rt-toolbar) and a
// contenteditable .rt-body. One shared #imgMenu on the page sizes/removes pictures.
//
// Uses the browser's built-in editing, set to write inline styles (what email
// apps keep). The server cleans the HTML before saving or sending.
//
//   var ed = new RichEditor(wrapEl, { onChange: fn, onFocus: fn, logoUrl: url });
//
// With logoUrl, {{logo}} codes show as the actual logo (click it to resize);
// html() turns them back into {{logo}} / {{logo|small}} / {{logo|large}}.
//   ed.html() / ed.setHtml(html) / ed.setText(text) / ed.insertText(text)
var RichEditor = (function () {
    var IMG_WIDTHS = { small: 200, medium: 360, full: 544 };   // 544 = message width in the email
    // {{logo}} heights — must match EmailHtml.Finish / TemplateRenderer.ToHtml
    var LOGO_HEIGHTS = { small: 36, medium: 60, large: 100 };
    var LOGO_TOKEN = /\{\{\s*logo\s*(?:\|\s*(small|medium|large)\s*)?\}\}/gi;
    var instances = [];
    var selectedImg = null, imgOwner = null;
    var openMenuBtn = null;    // toolbar button whose color menu is open
    var ready = false;

    function toast(msg, ok) { if (window.showToast) window.showToast(msg, ok); }
    function esc(s) {
        return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    // Page-wide listeners, set up once for all editors.
    function setupPage() {
        if (ready) return;
        ready = true;
        try { document.execCommand('defaultParagraphSeparator', false, 'div'); } catch (e) {}

        document.addEventListener('selectionchange', function () {
            var sel = window.getSelection();
            if (!sel.rangeCount) return;
            var node = sel.getRangeAt(0).commonAncestorContainer;
            instances.forEach(function (ed) {
                if (ed.body.contains(node)) { ed.savedRange = sel.getRangeAt(0).cloneRange(); ed.updateState(); }
            });
        });
        document.addEventListener('click', function (e) {
            if (!e.target.closest('.tb-menu, [data-menu]')) closeMenus();
            if (!e.target.closest('#imgMenu, .rt-body')) selectImage(null, null);
        });
        // Open menus follow their button / picture when the page scrolls
        window.addEventListener('scroll', function () {
            if (openMenuBtn) placeMenu(openMenuBtn);
            if (selectedImg) placeImgMenu();
        }, true);
        window.addEventListener('resize', closeMenus);

        var menu = document.getElementById('imgMenu');
        if (menu) {
            menu.addEventListener('mousedown', function (e) { e.preventDefault(); });
            menu.querySelectorAll('[data-img-size]').forEach(function (b) {
                b.addEventListener('click', function () { sizeImage(b.dataset.imgSize); });
            });
        }
    }

    // ── Floating menus (colors) ─────────────────────────────────────
    // Fixed-position and kept inside the window, so the editor box (which
    // clips its contents for the rounded corners) can't cut them off.
    function toggleMenu(btn) {
        var panel = btn.nextElementSibling;
        var open = panel.classList.contains('hidden');
        closeMenus();
        if (!open) return;
        panel.classList.remove('hidden');
        openMenuBtn = btn;
        placeMenu(btn);
    }
    function placeMenu(btn) {
        var panel = btn.nextElementSibling;
        var r = btn.getBoundingClientRect(), w = panel.offsetWidth, h = panel.offsetHeight;
        var left = Math.min(r.left, window.innerWidth - w - 8);
        var top  = r.bottom + 4;
        if (top + h > window.innerHeight - 8) top = Math.max(8, r.top - h - 4);
        panel.style.left = Math.max(8, left) + 'px';
        panel.style.top  = top + 'px';
    }
    function closeMenus() {
        openMenuBtn = null;
        document.querySelectorAll('.tb-menu').forEach(function (m) { m.classList.add('hidden'); });
    }

    // ── Picture menu ────────────────────────────────────────────────
    function selectImage(img, owner) {
        if (selectedImg) selectedImg.classList.remove('img-selected');
        selectedImg = img;
        imgOwner = owner;
        var menu = document.getElementById('imgMenu');
        if (!menu) return;
        if (!img || owner.body.getAttribute('contenteditable') !== 'true') { menu.classList.add('hidden'); return; }
        img.classList.add('img-selected');
        // Same menu for the logo; its biggest size is "Large" rather than full width
        var full = menu.querySelector('[data-img-size="full"]');
        if (full) full.textContent = img.dataset.logoSize ? 'Large' : 'Full width';
        menu.classList.remove('hidden');
        placeImgMenu();
    }
    function placeImgMenu() {
        var menu = document.getElementById('imgMenu'), r = selectedImg.getBoundingClientRect();
        menu.style.left = Math.max(8, Math.min(r.left, window.innerWidth - menu.offsetWidth - 8)) + 'px';
        menu.style.top  = Math.max(8, r.top - menu.offsetHeight - 6) + 'px';
    }
    function sizeImage(size) {
        var img = selectedImg, owner = imgOwner;
        if (!img) return;
        if (size === 'remove') {
            img.remove();
        } else if (img.dataset.logoSize) {
            var logoSize = size === 'full' ? 'large' : size;
            img.dataset.logoSize = logoSize;
            img.style.height = LOGO_HEIGHTS[logoSize] + 'px';
        } else {
            var w = IMG_WIDTHS[size];
            img.setAttribute('width', w);
            img.style.width = size === 'full' ? '100%' : w + 'px';
        }
        selectImage(null, null);
        owner.changed();
    }

    // Photos straight off a phone are huge: scale anything wider than 1200px
    // down (2x the email width, so it stays sharp on retina screens).
    // GIFs are left alone so animations keep working.
    function shrinkImage(file) {
        return new Promise(function (resolve, reject) {
            var url = URL.createObjectURL(file), img = new Image();
            img.onload = function () {
                URL.revokeObjectURL(url);
                var max = 1200;
                if (file.type === 'image/gif' || img.naturalWidth <= max) {
                    if (file.size > 5 * 1024 * 1024) { reject(new Error('pictures must be under 5 MB')); return; }
                    resolve({ blob: file, name: file.name, width: img.naturalWidth });
                    return;
                }
                var c = document.createElement('canvas');
                c.width = max;
                c.height = Math.round(img.naturalHeight * max / img.naturalWidth);
                c.getContext('2d').drawImage(img, 0, 0, c.width, c.height);
                var type = file.type === 'image/png' ? 'image/png' : 'image/jpeg';
                c.toBlob(function (b) {
                    resolve({ blob: b, name: 'image' + (type === 'image/png' ? '.png' : '.jpg'), width: max });
                }, type, 0.85);
            };
            img.onerror = function () { URL.revokeObjectURL(url); reject(new Error('that file isn\'t a picture this browser can read')); };
            img.src = url;
        });
    }

    function cleanUrl(url, allowContact) {
        url = (url || '').trim();
        if (!url || url === 'https://') return null;
        if (allowContact && /^(mailto|tel):/i.test(url)) return url;
        return /^https?:\/\//i.test(url) ? url : 'https://' + url;
    }

    // ── An editor ───────────────────────────────────────────────────
    function RichEditor(wrap, opts) {
        setupPage();
        var self = this;
        this.opts = opts || {};
        this.body = wrap.querySelector('.rt-body');
        this.toolbar = wrap.querySelector('.rt-toolbar');
        this.logoUrl = this.opts.logoUrl || null;
        this.savedRange = null;
        instances.push(this);

        var body = this.body;
        body.addEventListener('input', function () { self.changed(); });
        body.addEventListener('focus', function () { if (self.opts.onFocus) self.opts.onFocus(self); });
        // Pictures upload; text comes in plain so other sites' fonts don't come along
        body.addEventListener('paste', function (e) {
            e.preventDefault();
            var cd = e.clipboardData || window.clipboardData;
            var img = Array.from(cd.files || []).find(function (f) { return /^image\//.test(f.type); });
            if (img) { self.uploadImage(img); return; }
            document.execCommand('insertText', false, cd.getData('text/plain'));
        });
        body.addEventListener('dragover', function (e) {
            if (Array.from(e.dataTransfer.items || []).some(function (i) { return i.kind === 'file'; })) {
                e.preventDefault();
                body.classList.add('drop-target');
            }
        });
        body.addEventListener('dragleave', function () { body.classList.remove('drop-target'); });
        body.addEventListener('drop', function (e) {
            body.classList.remove('drop-target');
            var img = Array.from(e.dataTransfer.files || []).find(function (f) { return /^image\//.test(f.type); });
            if (!img) return;
            e.preventDefault();
            var r = document.caretRangeFromPoint ? document.caretRangeFromPoint(e.clientX, e.clientY) : null;
            if (r) { var sel = window.getSelection(); sel.removeAllRanges(); sel.addRange(r); self.savedRange = r.cloneRange(); }
            self.uploadImage(img);
        });
        body.addEventListener('click', function (e) {
            if (e.target.tagName === 'IMG') { selectImage(e.target, self); e.stopPropagation(); }
            else selectImage(null, null);
        });

        var tb = this.toolbar;
        if (!tb) return;   // read-only
        // Clicking a toolbar button must not move focus (and the selection) out of the editor
        tb.addEventListener('mousedown', function (e) { if (!e.target.closest('select, input')) e.preventDefault(); });
        tb.querySelectorAll('[data-cmd]').forEach(function (b) {
            b.addEventListener('click', function () { self.exec(b.dataset.cmd); });
        });
        tb.querySelectorAll('[data-menu]').forEach(function (b) {
            b.addEventListener('click', function (e) { e.stopPropagation(); toggleMenu(b); });
        });
        tb.querySelectorAll('[data-color]').forEach(function (b) {
            b.addEventListener('click', function () { self.exec('foreColor', b.dataset.color); closeMenus(); });
        });
        tb.querySelectorAll('[data-highlight]').forEach(function (b) {
            b.addEventListener('click', function () { self.exec('hiliteColor', b.dataset.highlight); closeMenus(); });
        });
        tb.querySelector('.rt-custom-color').addEventListener('change', function () { self.exec('foreColor', this.value); closeMenus(); });
        tb.querySelector('.rt-custom-highlight').addEventListener('change', function () { self.exec('hiliteColor', this.value); closeMenus(); });
        tb.querySelector('.rt-font').addEventListener('change', function () { if (this.value) self.exec('fontName', this.value); this.value = ''; });
        tb.querySelector('.rt-size').addEventListener('change', function () { if (this.value) self.setFontSize(this.value); this.value = ''; });
        tb.querySelector('.rt-file').addEventListener('change', function () {
            if (this.files[0]) self.uploadImage(this.files[0]);
            this.value = '';
        });
        tb.querySelectorAll('[data-action]').forEach(function (b) {
            b.addEventListener('click', function () {
                switch (b.dataset.action) {
                    case 'link':      self.addLink(); break;
                    case 'button':    self.addButton(); break;
                    case 'image':     tb.querySelector('.rt-file').click(); break;
                    case 'logo':      self.insertLogo(); break;
                    case 'signature': self.insertBlock('{{signature}}'); break;
                }
            });
        });
    }

    RichEditor.prototype.changed = function () {
        this.updateState();
        if (this.opts.onChange) this.opts.onChange(this);
    };

    // Put the cursor back where it was. Only needed when focus went elsewhere
    // (a dropdown, a prompt); if it's still in this editor, use it.
    RichEditor.prototype.restoreSelection = function () {
        var sel = window.getSelection();
        var inside = sel.rangeCount && this.body.contains(sel.getRangeAt(0).commonAncestorContainer);
        this.body.focus();
        if (inside || !this.savedRange) return;
        sel.removeAllRanges();
        sel.addRange(this.savedRange);
    };

    RichEditor.prototype.exec = function (cmd, value) {
        this.restoreSelection();
        document.execCommand('styleWithCSS', false, true);
        document.execCommand(cmd, false, value == null ? null : value);
        this.changed();
    };

    // execCommand only knows sizes 1–7: mark the selection as size 7, then swap
    // those marks for exact pixel sizes, keeping the text selected.
    RichEditor.prototype.setFontSize = function (px) {
        this.restoreSelection();
        document.execCommand('styleWithCSS', false, false);
        document.execCommand('fontSize', false, '7');
        document.execCommand('styleWithCSS', false, true);
        var spans = [];
        this.body.querySelectorAll('font[size="7"]').forEach(function (f) {
            var span = document.createElement('span');
            span.style.fontSize = px + 'px';
            while (f.firstChild) span.appendChild(f.firstChild);
            f.replaceWith(span);
            spans.push(span);
        });
        if (spans.length) {
            var r = document.createRange();
            r.setStartBefore(spans[0]);
            r.setEndAfter(spans[spans.length - 1]);
            var sel = window.getSelection();
            sel.removeAllRanges();
            sel.addRange(r);
            this.savedRange = r.cloneRange();
        }
        this.changed();
    };

    RichEditor.prototype.updateState = function () {
        if (!this.toolbar) return;
        this.toolbar.querySelectorAll('[data-cmd]').forEach(function (b) {
            var on = false;
            try { on = document.queryCommandState(b.dataset.cmd); } catch (e) {}
            b.classList.toggle('active', on);
        });
    };

    RichEditor.prototype.insertText = function (text) {
        this.restoreSelection();
        document.execCommand('insertText', false, text);
        this.changed();
    };

    RichEditor.prototype.addLink = function () {
        var hasText = this.savedRange && !this.savedRange.collapsed;
        var url = cleanUrl(prompt('Link address (website, or mailto: / tel:)', 'https://'), true);
        if (!url) return;
        this.restoreSelection();
        if (hasText) document.execCommand('createLink', false, url);
        else document.execCommand('insertHTML', false, '<a href="' + esc(url) + '">' + esc(url.replace(/^(https?:\/\/|mailto:|tel:)/i, '')) + '</a>&nbsp;');
        this.changed();
    };

    // Buttons, the logo and the signature are short codes on their own line;
    // the preview shows the real thing.
    RichEditor.prototype.addButton = function () {
        var text = prompt('Button text', 'Book a time');
        if (!text) return;
        var url = cleanUrl(prompt('Where should the button go?', 'https://'), false);
        if (!url) return;
        this.insertBlock('[' + text.replace(/[\[\]]/g, '') + '](' + url.replace(/[\s)]/g, '') + ')');
    };

    RichEditor.prototype.insertBlock = function (code) {
        this.restoreSelection();
        var sel = window.getSelection();
        var line = sel.anchorNode && (sel.anchorNode.nodeType === 1 ? sel.anchorNode : sel.anchorNode.parentElement);
        var onEmptyLine = line && line !== this.body && this.body.contains(line) && line.textContent.trim() === '';
        if (this.body.textContent.trim() === '' || onEmptyLine) {
            document.execCommand('insertText', false, code);
        } else {
            document.execCommand('insertParagraph');
            document.execCommand('insertText', false, code);
        }
        document.execCommand('insertParagraph');
        this.changed();
    };

    function logoImgHtml(url, size) {
        size = (size || 'medium').toLowerCase();
        return '<img src="' + esc(url) + '" alt="Logo" data-logo-size="' + size + '" style="height:' + LOGO_HEIGHTS[size] +
               'px;width:auto" title="Your logo. Click to change its size.">';
    }

    // The logo goes on its own line. Shown as the real logo when there is one.
    RichEditor.prototype.insertLogo = function () {
        if (!this.logoUrl) { this.insertBlock('{{logo}}'); return; }
        this.restoreSelection();
        var sel = window.getSelection();
        var line = sel.anchorNode && (sel.anchorNode.nodeType === 1 ? sel.anchorNode : sel.anchorNode.parentElement);
        var onEmptyLine = line && line !== this.body && this.body.contains(line) && line.textContent.trim() === '' && !line.querySelector('img');
        if (!(this.body.textContent.trim() === '' && !this.body.querySelector('img')) && !onEmptyLine)
            document.execCommand('insertParagraph');
        document.execCommand('insertHTML', false, logoImgHtml(this.logoUrl));
        document.execCommand('insertParagraph');
        this.changed();
    };

    RichEditor.prototype.uploadImage = async function (file) {
        if (!/^image\/(png|jpeg|gif|webp)$/.test(file.type)) { toast('Use a PNG, JPG, GIF or WebP picture.', false); return; }
        toast('Adding picture…', true);
        try {
            var shrunk = await shrinkImage(file);
            var fd = new FormData();
            fd.append('file', shrunk.blob, shrunk.name);
            var resp = await fetch('/Email/Images', { method: 'POST', body: fd });
            var r = await resp.json().catch(function () { return {}; });
            if (!resp.ok) throw new Error(r.error || 'HTTP ' + resp.status);

            // Wide pictures fill the message; small ones keep their own size.
            var w = shrunk.width >= IMG_WIDTHS.medium ? IMG_WIDTHS.full : shrunk.width;
            var style = w >= IMG_WIDTHS.full ? 'width:100%' : 'width:' + w + 'px';
            this.restoreSelection();
            document.execCommand('insertHTML', false, '<img src="' + esc(r.url) + '" alt="" width="' + w + '" style="' + style + '">');
            this.changed();
            toast('Picture added. Click it to change the size.', true);
        } catch (e) {
            toast('Couldn\'t add the picture: ' + e.message, false);
        }
    };

    // HTML for saving/preview. Text typed on the very first line isn't wrapped
    // in a <div> by the browser, so wrap loose top-level pieces.
    RichEditor.prototype.html = function () {
        var out = document.createElement('div'), line = null;
        var copy = this.body.cloneNode(true);
        copy.querySelectorAll('.img-selected').forEach(function (i) { i.removeAttribute('class'); });
        // The logo is saved as its code, so a new logo upload updates every template
        copy.querySelectorAll('img[data-logo-size]').forEach(function (i) {
            var s = i.dataset.logoSize;
            i.replaceWith(document.createTextNode(s === 'medium' ? '{{logo}}' : '{{logo|' + s + '}}'));
        });
        Array.from(copy.childNodes).forEach(function (n) {
            var block = n.nodeType === 1 && /^(DIV|P|UL|OL|BLOCKQUOTE|HR)$/.test(n.nodeName);
            if (block) { out.appendChild(n); line = null; return; }
            if (!line) { line = document.createElement('div'); out.appendChild(line); }
            line.appendChild(n);
        });
        return out.innerHTML;
    };

    RichEditor.prototype.setHtml = function (html) {
        var url = this.logoUrl;
        html = html || '';
        if (url) html = html.replace(LOGO_TOKEN, function (m, size) { return logoImgHtml(url, size); });
        this.body.innerHTML = html;
        this.savedRange = null;
    };

    // Older plain-text templates: one <div> per line, **bold** made bold.
    RichEditor.prototype.setText = function (text) {
        this.setHtml((text || '').split(/\r?\n/).map(function (l) {
            return '<div>' + (l ? esc(l).replace(/\*\*(.+?)\*\*/g, '<b>$1</b>') : '<br>') + '</div>';
        }).join(''));
    };

    return RichEditor;
})();
