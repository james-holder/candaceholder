// Page logic for Views/Company/Settings.cshtml — logo preview and accent/header color
// pickers. Extracted 2026-07-17 from an inline <script> block. Pure JS, no server-rendered
// values. Mobile menu toggle lives in the shared mobile-menu.js instead (this page's copy
// was a duplicate of what several other views also had inline).
function previewLogo(input) {
    if (!input.files || !input.files[0]) return;
    var reader = new FileReader();
    reader.onload = function(e) {
        var preview = document.getElementById('logoPreview');
        preview.innerHTML = '<img src="' + e.target.result + '" class="w-full h-full object-contain p-1" />';
    };
    reader.readAsDataURL(input.files[0]);
}

function updateColorPreview(hex) {
    document.getElementById('colorSwatch').style.backgroundColor = hex;
    document.getElementById('accentHexInput').value = hex;
}

function setColor(hex) {
    document.getElementById('accentColor').value = hex;
    updateColorPreview(hex);
}

function updateHeaderPreview(hex) {
    document.getElementById('headerSwatch').style.backgroundColor = hex;
    document.getElementById('headerHexInput').value = hex;
}

function setHeaderColor(hex) {
    document.getElementById('headerColor').value = hex;
    updateHeaderPreview(hex);
}

function onHexInput(textInput, colorInputId, previewFn) {
    var val = textInput.value.trim();
    if (!val.startsWith('#')) val = '#' + val;
    if (/^#[0-9a-fA-F]{6}$/.test(val)) {
        document.getElementById(colorInputId).value = val;
        previewFn(val);
    }
}
