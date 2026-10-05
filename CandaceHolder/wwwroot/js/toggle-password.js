// Show/hide password field toggle. Extracted 2026-07-17 — this exact function was
// duplicated verbatim inline across Views/Auth/Login.cshtml, Register.cshtml, and
// ResetPassword.cshtml.
function togglePwd(id, btn) {
    var el = document.getElementById(id);
    var icon = btn.querySelector('i');
    if (el.type === 'password') {
        el.type = 'text';
        icon.classList.remove('fa-eye');
        icon.classList.add('fa-eye-slash');
    } else {
        el.type = 'password';
        icon.classList.remove('fa-eye-slash');
        icon.classList.add('fa-eye');
    }
}
