// Page logic for Views/Team/Index.cshtml — member/invite list rendering, invite/remove/role
// actions. Extracted 2026-07-17 from an inline <script> block.
//
// Depends on window.isOwner / window.canManage, which the view sets in a small inline
// bootstrap script (Razor-computed booleans, @(isOwner ? "true" : "false")) loaded just
// before this file — these can't be baked into a static file since they're per-request,
// per-user values.
//
// toggleMobileMenu() used to be duplicated here too — now in the shared mobile-menu.js.

function showToast(msg, ok = true) {
    const t = document.getElementById('toast');
    t.textContent = msg;
    t.className = `fixed bottom-5 right-5 z-50 px-4 py-3 rounded-xl text-sm font-medium shadow-xl transition-all ${ok ? 'bg-green-900/90 border border-green-700 text-green-700' : 'bg-red-900/90 border border-red-700 text-red-700'}`;
    t.classList.remove('hidden');
    setTimeout(() => t.classList.add('hidden'), 4000);
}

function roleBadge(role) {
    const colors = {
        owner:   'bg-brand/20 text-brand border-brand/30',
        manager: 'bg-purple-900/40 text-purple-700 border-purple-700/40',
        rep:     'bg-slate-700/60 text-slate-300 border-slate-600'
    };
    const c = colors[role] || colors.rep;
    return `<span class="px-2 py-0.5 rounded-full text-xs font-semibold border ${c} capitalize">${role}</span>`;
}

async function loadMembers() {
    try {
        const res  = await fetch('/Team/Members');
        const list = await res.json();
        const el   = document.getElementById('memberList');
        document.getElementById('memberCount').textContent = `(${list.length})`;

        if (!list.length) {
            el.innerHTML = '<div class="px-6 py-8 text-center text-slate-500 text-sm">No members found.</div>';
            return;
        }

        el.innerHTML = list.map(m => `
            <div class="px-6 py-4 flex items-center gap-4" id="member-${m.id}">
                <div class="w-9 h-9 rounded-full bg-slate-700 flex items-center justify-center text-slate-300 shrink-0">
                    <i class="fa-solid fa-circle-user"></i>
                </div>
                <div class="flex-1 min-w-0">
                    <div class="flex items-center gap-2 flex-wrap">
                        <span class="text-sm font-medium text-slate-50">${esc(m.name)}</span>
                        ${m.isMe ? '<span class="text-xs text-slate-500">(you)</span>' : ''}
                        ${roleBadge(m.role)}
                    </div>
                    <div class="text-xs text-slate-500 mt-0.5">${esc(m.email)}</div>
                </div>
                <div class="flex items-center gap-2 shrink-0">
                    ${window.isOwner && !m.isMe && m.role !== 'owner' ? `
                    <select onchange="updateRole(${m.id}, this.value)"
                        class="bg-navy border border-slate-600 rounded-lg px-2 py-1 text-xs text-slate-300 focus:outline-none focus:border-brand/60">
                        <option value="rep"     ${m.role === 'rep'     ? 'selected' : ''}>Rep</option>
                        <option value="manager" ${m.role === 'manager' ? 'selected' : ''}>Manager</option>
                    </select>
                    <button onclick="removeMember(${m.id}, '${esc(m.name)}')"
                        class="p-1.5 rounded-lg text-slate-500 hover:text-red-600 hover:bg-red-900/20 transition text-xs" title="Remove from team">
                        <i class="fa-solid fa-user-minus"></i>
                    </button>` : ''}
                </div>
            </div>
        `).join('');
    } catch(e) {
        document.getElementById('memberList').innerHTML =
            '<div class="px-6 py-6 text-center text-red-600 text-sm">Failed to load members.</div>';
    }
}

async function loadInvites() {
    if (!window.canManage) return;
    try {
        const res  = await fetch('/Team/Invites');
        const list = await res.json();
        const el   = document.getElementById('inviteList');
        document.getElementById('inviteCount').textContent = `(${list.length})`;

        if (!list.length) {
            el.innerHTML = '<div class="px-6 py-8 text-center text-slate-500 text-sm">No pending invites.</div>';
            return;
        }

        el.innerHTML = list.map(i => `
            <div class="px-6 py-4 flex items-center gap-4" id="invite-${i.id}">
                <div class="w-9 h-9 rounded-full bg-slate-700/60 flex items-center justify-center text-slate-500 shrink-0">
                    <i class="fa-solid fa-envelope text-sm"></i>
                </div>
                <div class="flex-1 min-w-0">
                    <div class="flex items-center gap-2 flex-wrap">
                        <span class="text-sm font-medium text-slate-200">${esc(i.email)}</span>
                        ${roleBadge(i.role)}
                    </div>
                    <div class="text-xs text-slate-500 mt-0.5">Expires ${esc(i.expiresAt)}</div>
                </div>
                <button onclick="revokeInvite(${i.id}, '${esc(i.email)}')"
                    class="p-1.5 rounded-lg text-slate-500 hover:text-red-600 hover:bg-red-900/20 transition text-xs shrink-0" title="Revoke invite">
                    <i class="fa-solid fa-xmark"></i>
                </button>
            </div>
        `).join('');
    } catch(e) {
        document.getElementById('inviteList').innerHTML =
            '<div class="px-6 py-6 text-center text-red-600 text-sm">Failed to load invites.</div>';
    }
}

async function sendInvite() {
    const email = document.getElementById('inviteEmail').value.trim();
    const role  = document.getElementById('inviteRole').value;
    if (!email) { showToast('Enter an email address.', false); return; }

    const btn = document.querySelector('button[onclick="sendInvite()"]');
    btn.disabled = true;
    btn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin mr-1.5"></i>Sending…';

    try {
        const res  = await fetch('/Team/Invite', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ email, role })
        });
        const data = await res.json();
        if (res.ok) {
            showToast(data.message || 'Invite sent!');
            document.getElementById('inviteEmail').value = '';
            if (data.acceptUrl) {
                document.getElementById('inviteNote').innerHTML =
                    `<span class="text-yellow-600">SMTP not configured — share this link manually:</span> <a href="${data.acceptUrl}" class="text-brand underline break-all">${data.acceptUrl}</a>`;
            }
            loadInvites();
        } else {
            showToast(data.error || 'Failed to send invite.', false);
        }
    } catch(e) {
        showToast('Network error. Try again.', false);
    } finally {
        btn.disabled = false;
        btn.innerHTML = '<i class="fa-solid fa-paper-plane mr-1.5"></i>Send Invite';
    }
}

async function removeMember(id, name) {
    if (!confirm(`Remove ${name} from your team?`)) return;
    try {
        const res = await fetch(`/Team/Members/${id}`, { method: 'DELETE' });
        if (res.ok) {
            document.getElementById(`member-${id}`)?.remove();
            showToast(`${name} removed from your team.`);
            loadMembers(); // refresh count
        } else {
            const d = await res.json();
            showToast(d.error || 'Could not remove member.', false);
        }
    } catch(e) {
        showToast('Network error.', false);
    }
}

async function updateRole(id, role) {
    try {
        const res = await fetch(`/Team/Members/${id}/Role`, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ role })
        });
        if (res.ok) {
            showToast('Role updated.');
        } else {
            const d = await res.json();
            showToast(d.error || 'Could not update role.', false);
            loadMembers(); // revert UI
        }
    } catch(e) {
        showToast('Network error.', false);
        loadMembers();
    }
}

async function revokeInvite(id, email) {
    if (!confirm(`Revoke invite for ${email}?`)) return;
    try {
        const res = await fetch(`/Team/Invites/${id}`, { method: 'DELETE' });
        if (res.ok) {
            document.getElementById(`invite-${id}`)?.remove();
            showToast('Invite revoked.');
            loadInvites(); // refresh count
        } else {
            const d = await res.json();
            showToast(d.error || 'Could not revoke invite.', false);
        }
    } catch(e) {
        showToast('Network error.', false);
    }
}

function esc(s) {
    if (!s) return '';
    return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

loadMembers();
loadInvites();
