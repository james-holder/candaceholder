// Address suggestions for the search box on Views/Home/Index.cshtml, built on the
// Places API (New) AutocompleteSuggestion service. The legacy
// google.maps.places.Autocomplete widget isn't available to Google Cloud projects
// created after March 1, 2025, so this renders its own dropdown instead.
//
// Must load BEFORE the Google Maps JS SDK <script> tag, since that SDK's callback
// (initAutocomplete, defined below) needs to already exist when the SDK finishes loading.
//
// If suggestions fail (API not enabled, billing off, etc.) the search box still works:
// typing a full address and pressing Enter lets the server geocode it.

// Coords captured when the user picks a suggestion
var _pickedLat = 0, _pickedLng = 0;

async function initAutocomplete() {
    const input = document.getElementById('addressInput');
    if (!input) { document.addEventListener('DOMContentLoaded', initAutocomplete); return; }

    let places;
    try { places = await google.maps.importLibrary('places'); }
    catch (e) { console.warn('Address suggestions unavailable:', e); return; }
    if (!places.AutocompleteSuggestion) return;

    const list = document.createElement('div');
    list.className = 'address-suggestions hidden';
    input.parentElement.appendChild(list);

    // A session token groups keystrokes + the final pick into one billed session.
    let token  = new places.AutocompleteSessionToken();
    let items  = [];
    let active = -1;
    let timer  = null;
    let seq    = 0;

    const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

    function close() {
        list.classList.add('hidden');
        items = [];
        active = -1;
    }

    function render() {
        list.innerHTML = items.map((s, i) =>
            `<div class="address-suggestion${i === active ? ' active' : ''}" data-i="${i}">` +
            `<i class="fa-solid fa-location-dot"></i>${esc(s.placePrediction.text.toString())}</div>`
        ).join('');
        list.classList.toggle('hidden', items.length === 0);
    }

    async function pick(i) {
        const prediction = items[i].placePrediction;
        close();
        input.value = prediction.text.toString();
        try {
            const place = prediction.toPlace();
            await place.fetchFields({ fields: ['formattedAddress', 'location'] });
            if (place.formattedAddress) input.value = place.formattedAddress;
            if (place.location) {
                _pickedLat = place.location.lat();
                _pickedLng = place.location.lng();
            }
        } catch (e) {
            console.warn('Could not fetch place details:', e);
        }
        token = new places.AutocompleteSessionToken();
    }

    input.addEventListener('input', () => {
        clearTimeout(timer);
        const q = input.value.trim();
        if (q.length < 3) { close(); return; }
        timer = setTimeout(async () => {
            const mine = ++seq;
            try {
                const { suggestions } = await places.AutocompleteSuggestion.fetchAutocompleteSuggestions({
                    input: q, sessionToken: token, includedRegionCodes: ['us']
                });
                if (mine !== seq) return;   // a newer keystroke already fired
                items  = suggestions.filter(s => s.placePrediction);
                active = -1;
                render();
            } catch (e) {
                console.warn('Address suggestions failed:', e);
                close();
            }
        }, 250);
    });

    // mousedown (not click) so the pick happens before the input's blur closes the list
    list.addEventListener('mousedown', e => {
        const el = e.target.closest('.address-suggestion');
        if (!el) return;
        e.preventDefault();
        pick(parseInt(el.dataset.i, 10));
    });

    // Capture phase on document so arrow/Enter keys are handled here before site.js's
    // keydown handler on the input (which runs the search on Enter).
    document.addEventListener('keydown', e => {
        if (e.target !== input || list.classList.contains('hidden')) return;
        if (e.key === 'ArrowDown')      { active = Math.min(active + 1, items.length - 1); render(); }
        else if (e.key === 'ArrowUp')   { active = Math.max(active - 1, 0); render(); }
        else if (e.key === 'Escape')    { close(); }
        else if (e.key === 'Enter' && active >= 0) { pick(active); }
        else { if (e.key === 'Enter') close(); return; }
        e.preventDefault();
        e.stopImmediatePropagation();
    }, true);

    input.addEventListener('blur', () => setTimeout(close, 150));
}

// Client-side geocode, used by the map's "Go to" box. Resolves null on any failure
// (including the Maps SDK not having loaded).
function clientGeocode(address) {
    return new Promise(resolve => {
        if (!window.google || !google.maps || !google.maps.Geocoder) { resolve(null); return; }
        new google.maps.Geocoder().geocode({ address }, (results, status) => {
            if (status === 'OK' && results[0]) {
                resolve({
                    lat: results[0].geometry.location.lat(),
                    lng: results[0].geometry.location.lng()
                });
            } else {
                resolve(null);
            }
        });
    });
}
