// Page logic for Views/Admin/Index.cshtml — user email modal (with templates).
// No Razor dependencies — pure JS, safe as a static file.

var emailTemplates = {
    blank: { subject: '', body: '' },
    checkin: {
        subject: "Checking in",
        body: "Hi {{name}},\n\nJust wanted to check in and see how things are going. Let me know if there's anything I can help with.\n\nThanks!"
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
