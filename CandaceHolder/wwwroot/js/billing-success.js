// Page logic for Views/Billing/Success.cshtml — checks the Stripe checkout session
// status and toggles the loading/success/incomplete states. Extracted 2026-07-17 from
// an inline <script> block. No Razor dependencies — pure JS, safe as a static file.

var PRODUCT_LABELS = {
    starter: 'Starter ($14.99/mo)',
    pro: 'Pro ($39.99/mo, unlimited reports)',
    topup: '3-report top-up',
    pack50: '50-report pack'
};

function showState(id) {
    ['stateLoading', 'stateSuccess', 'stateIncomplete'].forEach(function (s) {
        document.getElementById(s).classList.toggle('hidden', s !== id);
    });
}

(async function checkSessionStatus() {
    var sessionId = new URLSearchParams(window.location.search).get('session_id');
    if (!sessionId) { showState('stateIncomplete'); return; }

    try {
        var resp = await fetch('/Billing/SessionStatus?session_id=' + encodeURIComponent(sessionId));
        var data = await resp.json();
        if (!resp.ok || data.status !== 'complete') {
            showState('stateIncomplete');
            return;
        }
        document.getElementById('productLabel').textContent = PRODUCT_LABELS[data.product] || 'your plan';
        showState('stateSuccess');
    } catch (e) {
        showState('stateIncomplete');
    }
})();
