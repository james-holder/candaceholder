// Views/Admin/Email.cshtml — provider preset buttons fill in the SMTP server
// and port, and show provider-specific notes about username/password.
document.querySelectorAll('.smtp-preset').forEach(function (btn) {
    btn.addEventListener('click', function () {
        document.getElementById('smtpHost').value = btn.dataset.host;
        document.getElementById('smtpPort').value = btn.dataset.port;
        var tip = document.getElementById('presetTip');
        tip.textContent = btn.dataset.tip;
        tip.classList.remove('hidden');
    });
});
