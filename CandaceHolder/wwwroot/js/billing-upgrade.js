// Page logic for Views/Billing/Upgrade.cshtml — Stripe embedded checkout modal.
// Extracted 2026-07-17 from an inline <script> block.
//
// Depends on window.stripe, which the view sets in a small inline bootstrap script
// (Razor-computed: @Html.Raw(stripeInitJs), a server-built Stripe(...) call or null)
// loaded just before this file — this can't be baked into a static file since it's a
// per-request, server-decided value (whether billing is configured, publishable key, etc).

var currentCheckout = null;

function getAntiForgeryToken() {
    var el = document.querySelector('input[name="__RequestVerificationToken"]');
    return el ? el.value : '';
}

function showCheckoutError(message) {
    document.getElementById('checkoutErrorText').textContent = message;
    document.getElementById('checkoutError').classList.remove('hidden');
}

function hideCheckoutError() {
    document.getElementById('checkoutError').classList.add('hidden');
}

async function startCheckout(product) {
    hideCheckoutError();

    if (!window.stripe) {
        showCheckoutError('Billing isn\'t set up yet — check back soon or contact support.');
        return;
    }

    document.getElementById('checkoutModal').classList.remove('hidden');
    document.getElementById('checkoutLoading').classList.remove('hidden');
    document.getElementById('checkout-container').innerHTML = '';
    document.body.style.overflow = 'hidden';

    try {
        currentCheckout = await window.stripe.createEmbeddedCheckoutPage({
            fetchClientSecret: async function () {
                var resp = await fetch('/Billing/Checkout', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                    body: 'product=' + encodeURIComponent(product) +
                          '&__RequestVerificationToken=' + encodeURIComponent(getAntiForgeryToken())
                });
                var data = await resp.json();
                if (!resp.ok) throw new Error(data.error || 'Could not start checkout.');
                return data.clientSecret;
            }
        });
        document.getElementById('checkoutLoading').classList.add('hidden');
        currentCheckout.mount('#checkout-container');
    } catch (e) {
        closeCheckoutModal();
        showCheckoutError(e.message || 'Could not start checkout — please try again.');
    }
}

function closeCheckoutModal() {
    if (currentCheckout) {
        currentCheckout.destroy();
        currentCheckout = null;
    }
    document.getElementById('checkoutModal').classList.add('hidden');
    document.body.style.overflow = '';
}
