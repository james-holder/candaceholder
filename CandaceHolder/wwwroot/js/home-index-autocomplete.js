// Google Places Autocomplete wiring for the address search box on Views/Home/Index.cshtml.
// Extracted 2026-07-17 from an inline <script> block. Pure JS, no server-rendered values —
// the Google Maps API key stays server-side, passed only as a query string on the external
// Maps JS SDK <script src> tag (via @ViewBag.GoogleMapsApiKey), which is unaffected by this
// move and still lives in the view.
//
// Must load BEFORE the Google Maps JS SDK <script> tag, since that SDK's callback
// (initAutocomplete, defined below) needs to already exist when the SDK finishes loading
// and invokes it.

// Coords captured when user picks from autocomplete dropdown
var _pickedLat = 0, _pickedLng = 0;

function initAutocomplete() {
    const input = document.getElementById('addressInput');
    if (!input) { document.addEventListener('DOMContentLoaded', initAutocomplete); return; }
    const ac = new google.maps.places.Autocomplete(input, { types: ['geocode'] });
    ac.addListener('place_changed', () => {
        const place = ac.getPlace();
        if (place && place.formatted_address) input.value = place.formatted_address;
        if (place && place.geometry && place.geometry.location) {
            _pickedLat = place.geometry.location.lat();
            _pickedLng = place.geometry.location.lng();
        }
    });
}

// Client-side geocode fallback (used when user types manually)
function clientGeocode(address) {
    return new Promise(resolve => {
        const geocoder = new google.maps.Geocoder();
        geocoder.geocode({ address }, (results, status) => {
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
