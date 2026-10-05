// Page logic for Views/Admin/Index.cshtml — user email modal (with templates) and
// plan/trial/role access modal. Extracted 2026-07-17 from an inline <script> block.
// No Razor dependencies — pure JS, safe as a static file.

var emailTemplates = {
    blank: { subject: '', body: '' },
    feature_invite: {
        subject: "You're invited — try what's new on StormLead Pro",
        body: "Hi {{name}},\n\nWe've been rolling out some new features on StormLead Pro — an interactive Storm Explorer map (click an address or draw an area to pull real hail history straight into your leads list), self-serve plan upgrades, and a few other improvements.\n\nWould you be up for trying a few of these out and sharing any feedback? Good or bad, it all helps us make the product better.\n\nJust reply to this email whenever you get a chance.\n\nThanks,\nThe StormLead Pro team"
    },
    checkin: {
        subject: "Checking in from StormLead Pro",
        body: "Hi {{name}},\n\nJust wanted to check in and see how things are going with StormLead Pro. Let us know if there's anything we can help with, or if you've run into any issues.\n\nThanks,\nThe StormLead Pro team"
    }
};

function openEmailModal(btn) {
    var id    = btn.dataset.id;
    var email = btn.dataset.email;
    var name  = btn.dataset.name;

    var form = document.getElementById('emailModalForm');
    form.action = '/Admin/Users/' + id + '/Email';
    form.dataset.name = name || (email ? email.split('@')[0] : 'there');

    document.getElementById('emailModalTo').value = email || '';
    document.getElementById('emailModalName').textContent = '— ' + (name || email || '');
    document.getElementById('emailTemplateSelect').value = 'blank';
    applyEmailTemplate();

    document.getElementById('emailModal').classList.remove('hidden');
}

function openBroadcastEmailModal(count) {
    var form = document.getElementById('emailModalForm');
    form.action = '/Admin/EmailAll';
    form.dataset.name = 'there';

    document.getElementById('emailModalTo').value = 'All users (' + count + ' recipient' + (count === 1 ? '' : 's') + ', sent via BCC)';
    document.getElementById('emailModalName').textContent = '— All Users';
    document.getElementById('emailTemplateSelect').value = 'blank';
    applyEmailTemplate();

    document.getElementById('emailModal').classList.remove('hidden');
}

function closeEmailModal() {
    document.getElementById('emailModal').classList.add('hidden');
}

function applyEmailTemplate() {
    var key  = document.getElementById('emailTemplateSelect').value;
    var tpl  = emailTemplates[key] || emailTemplates.blank;
    var name = document.getElementById('emailModalForm').dataset.name || 'there';

    document.getElementById('emailSubject').value = tpl.subject;
    document.getElementById('emailBody').value = tpl.body.replace(/\{\{name\}\}/g, name);
}

document.getElementById('emailModal').addEventListener('click', function (e) {
    if (e.target === this) closeEmailModal();
});

function openAccessModal(btn) {
    var id        = btn.dataset.id;
    var plan      = btn.dataset.plan;
    var trialEnds = btn.dataset.trialEnds;
    var role      = btn.dataset.role;

    document.getElementById('accessPlanForm').action  = '/Admin/Users/' + id + '/Plan';
    document.getElementById('accessTrialForm').action = '/Admin/Users/' + id + '/Trial';
    document.getElementById('accessPlanSelect').value = plan || 'trial';

    var statusEl = document.getElementById('accessTrialStatus');
    if (!trialEnds) {
        statusEl.textContent = 'Currently: unlimited (never gated)';
    } else {
        var d = new Date(trialEnds);
        statusEl.textContent = (d.getTime() < Date.now())
            ? 'Currently: expired (' + d.toLocaleDateString() + ')'
            : 'Currently: ends ' + d.toLocaleDateString();
    }

    var roleForm = document.getElementById('accessRoleForm');
    if (roleForm) {
        roleForm.action = '/Admin/Users/' + id + '/Role';
        var roleStatusEl  = document.getElementById('accessRoleStatus');
        var makeAdminBtn  = roleForm.querySelector('[data-role-btn="admin"]');
        var removeAdminBtn = roleForm.querySelector('[data-role-btn="user"]');

        if (role === 'super_admin') {
            roleStatusEl.textContent = 'Super admin — managed via login, not here.';
            makeAdminBtn.classList.add('hidden');
            removeAdminBtn.classList.add('hidden');
        } else if (role === 'admin') {
            roleStatusEl.textContent = 'Currently: admin';
            makeAdminBtn.classList.add('hidden');
            removeAdminBtn.classList.remove('hidden');
        } else {
            roleStatusEl.textContent = 'Currently: regular user';
            makeAdminBtn.classList.remove('hidden');
            removeAdminBtn.classList.add('hidden');
        }
    }

    document.getElementById('accessModal').classList.remove('hidden');
}

function closeAccessModal() {
    document.getElementById('accessModal').classList.add('hidden');
}

document.getElementById('accessModal').addEventListener('click', function (e) {
    if (e.target === this) closeAccessModal();
});
