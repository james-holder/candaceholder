// Address search + results list for Views/Home/Index.cshtml.
// Results come from /Properties/Neighborhood, /Properties/SingleAddress, or
// (via area-map.js) /Properties/Area — all return the same shape:
//   { centerAddress, lat, lng, count, properties: [{ address, lat, lng }] }

// ─── State ───────────────────────────────────────────────────
    let scanMode       = 'neighborhood';  // 'neighborhood' | 'single'
    let allProperties  = [];
    let visibleProps   = [];              // allProperties after filter + sort (what the cards show)
    let currentFilter  = '';
    let currentSort    = 'distance';
    let currentAddress = '';
    let currentLat     = 0;
    let currentLng     = 0;
    let leafletMap     = null;
    let mapMarkers     = [];

    // ─── Scan mode toggle ─────────────────────────────────────────
    function switchScanMode(mode) {
        scanMode = mode;
        const isNeighborhood = mode === 'neighborhood';

        const btnN = document.getElementById('modeNeighborhood');
        const btnS = document.getElementById('modeSingle');
        const on   = 'flex items-center gap-1.5 px-4 py-2 rounded-lg text-xs font-semibold transition-all bg-brand text-white shadow';
        const off  = 'flex items-center gap-1.5 px-4 py-2 rounded-lg text-xs font-semibold transition-all text-slate-400 hover:text-slate-200';
        btnN.className = isNeighborhood ? on : off;
        btnS.className = isNeighborhood ? off : on;
        document.getElementById('radiusWrapper').classList.toggle('hidden', !isNeighborhood);
        setLoading(false);
    }

    // ─── Scan ─────────────────────────────────────────────────────
    async function runScan() {
        const address = document.getElementById('addressInput').value.trim();
        if (!address) {
            showError('Please enter an address.');
            return;
        }

        hideError();
        setLoading(true);

        try {
            // Use coords from autocomplete pick; fall back to client-side geocoder
            let lat = _pickedLat, lng = _pickedLng;
            if (!lat || !lng) {
                const geo = await clientGeocode(address);
                if (!geo) {
                    showError('Could not locate that address. Try selecting a suggestion from the dropdown.');
                    return;
                }
                lat = geo.lat;
                lng = geo.lng;
            }

            const url = scanMode === 'single'
                ? `/Properties/SingleAddress?address=${encodeURIComponent(address)}&lat=${lat}&lng=${lng}`
                : `/Properties/Neighborhood?address=${encodeURIComponent(address)}&radius=${document.getElementById('radiusSelect').value}&lat=${lat}&lng=${lng}`;
            const resp = await fetch(url);
            if (!resp.ok) {
                const err = await resp.json().catch(() => ({ error: 'Server error' }));
                throw new Error(err.message || err.error || `HTTP ${resp.status}`);
            }

            showResults(await resp.json(), address);
        } catch (e) {
            showError(e.message || 'Failed to fetch results. Please try again.');
        } finally {
            setLoading(false);
        }
    }

    // Allow Enter key to trigger scan; reset coords if user edits the field manually
    document.getElementById('addressInput').addEventListener('keydown', e => {
        if (e.key === 'Enter') runScan();
        else { _pickedLat = 0; _pickedLng = 0; }
    });

    // ─── Render Results ───────────────────────────────────────────
    // Entry point for every result source (address search and map area).
    function showResults(data, label) {
        currentAddress = label || data.centerAddress || '';
        currentLat     = data.lat;
        currentLng     = data.lng;
        allProperties  = (data.properties || []).map(p => ({
            ...p,
            distance: haversineMi(data.lat, data.lng, p.lat, p.lng)
        }));

        document.getElementById('centerLabel').textContent = data.centerAddress || currentAddress;
        document.getElementById('countTotal').textContent  = allProperties.length;

        const results = document.getElementById('results');
        results.classList.remove('hidden');
        results.scrollIntoView({ behavior: 'smooth', block: 'start' });

        initMap(data.lat, data.lng);
        applyFilterAndSort();
    }

    // ─── Map ──────────────────────────────────────────────────────
    function initMap(lat, lng) {
        if (leafletMap) {
            leafletMap.remove();
            leafletMap = null;
        }
        mapMarkers = [];

        leafletMap = L.map('map', { center: [lat, lng], zoom: 16, zoomControl: true });
        addBasemapWithToggle(leafletMap);

        const centerIcon = L.divIcon({
            html: `<div style="width:16px;height:16px;background:#f97316;border:3px solid #fff;border-radius:50%;box-shadow:0 0 8px rgba(249,115,22,0.8)"></div>`,
            iconSize: [16, 16],
            iconAnchor: [8, 8],
            className: ''
        });
        L.marker([lat, lng], { icon: centerIcon })
            .addTo(leafletMap)
            .bindPopup('<b style="color:#0f172a">Search center</b>');
    }

    // Satellite (Esri) by default, with a button to flip to a street map.
    // Shared with area-map.js.
    function addBasemapWithToggle(map) {
        const satellite = L.layerGroup([
            L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
                { attribution: 'Tiles &copy; Esri', maxZoom: 19 }),
            // World_Imagery has no place names — layer Esri's reference labels on top.
            L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}',
                { attribution: 'Labels &copy; Esri', maxZoom: 19 })
        ]);
        const mtKey  = window.MAPTILER_KEY || '';
        const street = mtKey
            ? L.tileLayer('https://api.maptiler.com/maps/streets-v2-dark/{z}/{x}/{y}.png?key=' + mtKey,
                { attribution: '&copy; MapTiler &copy; OpenStreetMap contributors', maxZoom: 20, tileSize: 512, zoomOffset: -1 })
            : L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png',
                { attribution: '&copy; OpenStreetMap contributors &copy; CARTO', maxZoom: 19 });
        satellite.addTo(map);

        let isSatellite = true;
        const ctrl = L.control({ position: 'topright' });
        ctrl.onAdd = () => {
            const div = L.DomUtil.create('div');
            div.innerHTML = `<button title="Switch map style"
                style="background:#1e293b;border:1px solid #475569;color:#cbd5e1;
                       padding:5px 10px;border-radius:8px;font-size:11px;font-weight:600;
                       cursor:pointer;display:flex;align-items:center;gap:5px;box-shadow:0 2px 6px rgba(0,0,0,0.4)">
                <i class="fa-solid fa-map"></i>&nbsp;Street
            </button>`;
            L.DomEvent.disableClickPropagation(div);
            const btn = div.querySelector('button');
            btn.addEventListener('click', () => {
                isSatellite = !isSatellite;
                if (isSatellite) {
                    street.remove(); satellite.addTo(map);
                    btn.innerHTML = '<i class="fa-solid fa-map"></i>&nbsp;Street';
                } else {
                    satellite.remove(); street.addTo(map);
                    btn.innerHTML = '<i class="fa-solid fa-satellite"></i>&nbsp;Satellite';
                }
            });
            return div;
        };
        ctrl.addTo(map);
    }

    function addMarkersToMap(properties) {
        mapMarkers.forEach(m => leafletMap.removeLayer(m));
        mapMarkers = [];

        const icon = L.divIcon({
            html: `<div style="width:12px;height:12px;background:#38bdf8;border:2px solid rgba(255,255,255,0.8);
                          border-radius:50%;box-shadow:0 0 6px #38bdf888;cursor:pointer"></div>`,
            iconSize: [12, 12],
            iconAnchor: [6, 6],
            className: ''
        });

        properties.forEach((p, idx) => {
            const m = L.marker([p.lat, p.lng], { icon })
                .addTo(leafletMap)
                .bindPopup(`<div style="font-family:system-ui,sans-serif;color:#0f172a;font-weight:600;font-size:13px">${escapeHtml(p.address)}</div>`);
            m.on('click', () => highlightCard(idx));
            mapMarkers.push(m);
        });
    }

    // ─── Cards ────────────────────────────────────────────────────
    function applyFilterAndSort() {
        const q = currentFilter.toLowerCase();
        visibleProps = allProperties.filter(p => !q || p.address.toLowerCase().includes(q));
        visibleProps.sort((a, b) => currentSort === 'address'
            ? a.address.localeCompare(b.address, undefined, { numeric: true })
            : a.distance - b.distance);

        renderCards(visibleProps);
        if (leafletMap) addMarkersToMap(visibleProps);
    }

    function renderCards(props) {
        // Clear selections whenever cards re-render (filter/sort)
        selectedIndices.clear();

        const list = document.getElementById('cardList');
        if (props.length === 0) {
            const noData = allProperties.length === 0;
            list.innerHTML = `
                <div class="flex flex-col items-center justify-center h-48 text-slate-500 gap-3 px-4 text-center">
                    <i class="fa-solid fa-${noData ? 'map-location-dot' : 'magnifying-glass'} text-3xl"></i>
                    <p class="text-sm">${noData
                        ? 'No addresses found for this area. Try a larger radius or a different spot.'
                        : 'No addresses match this filter.'}</p>
                </div>`;
            updateSelectionUI();
            return;
        }

        list.innerHTML = props.map((p, idx) => buildCardHtml(p, idx)).join('');

        // Click a card: pan the map to it
        list.querySelectorAll('.prop-card').forEach((card, idx) => {
            card.addEventListener('click', () => {
                if (mapMarkers[idx]) {
                    leafletMap.setView([props[idx].lat, props[idx].lng], 18, { animate: true });
                    mapMarkers[idx].openPopup();
                }
                highlightCard(idx);
            });
        });

        updateSelectionUI();
    }

    function buildCardHtml(p, idx) {
        const dist = p.distance < 0.1
            ? `${Math.round(p.distance * 5280)} ft`
            : `${p.distance.toFixed(2)} mi`;
        return `
        <div class="prop-card bg-slate-800/70 border border-slate-700/60 rounded-2xl px-4 py-3 mb-2 cursor-pointer"
             data-idx="${idx}">
            <div class="flex items-center gap-3">
                <div onclick="event.stopPropagation()">
                    <input type="checkbox" class="lead-check" data-idx="${idx}"
                           onchange="onCardCheckChange(this)" title="Select for saving" />
                </div>
                <p class="flex-1 min-w-0 font-semibold text-white text-sm leading-tight truncate">${escapeHtml(p.address)}</p>
                <span class="text-xs text-slate-500 shrink-0">${dist}</span>
            </div>
        </div>`;
    }

    function highlightCard(idx) {
        document.querySelectorAll('.prop-card').forEach(c => c.classList.remove('highlighted'));
        const card = document.querySelector(`.prop-card[data-idx="${idx}"]`);
        if (card) {
            card.classList.add('highlighted');
            card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        }
    }

    // ─── Filter + sort controls ───────────────────────────────────
    document.getElementById('filterInput').addEventListener('input', e => {
        currentFilter = e.target.value.trim();
        applyFilterAndSort();
    });
    document.getElementById('sortSelect').addEventListener('change', e => {
        currentSort = e.target.value;
        applyFilterAndSort();
    });

    // ─── Export (client-side — works for every result source) ─────
    function exportCSV() {
        if (!visibleProps.length) { showToast('Nothing to export yet.', false); return; }
        const esc  = v => `"${String(v).replace(/"/g, '""')}"`;
        const rows = ['Address,Latitude,Longitude', ...visibleProps.map(p => `${esc(p.address)},${p.lat},${p.lng}`)];
        const blob = new Blob([rows.join('\r\n')], { type: 'text/csv' });
        const a    = document.createElement('a');
        a.href     = URL.createObjectURL(blob);
        a.download = `Addresses_${new Date().toISOString().slice(0, 10)}.csv`;
        a.click();
        URL.revokeObjectURL(a.href);
    }

    // ─── Helpers ──────────────────────────────────────────────────
    function haversineMi(lat1, lng1, lat2, lng2) {
        const R = 3958.8; // Earth radius in miles
        const dLat = (lat2 - lat1) * Math.PI / 180;
        const dLng = (lng2 - lng1) * Math.PI / 180;
        const a = Math.sin(dLat/2)**2 +
                  Math.cos(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180) *
                  Math.sin(dLng/2)**2;
        return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
    }

    function escapeHtml(str) {
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function setLoading(on) {
        document.getElementById('scanBtn').disabled = on;
        document.getElementById('btnText').innerHTML = on
            ? '<i class="fa-solid fa-circle-notch fa-spin mr-1.5"></i>Finding addresses…'
            : scanMode === 'single'
                ? '<i class="fa-solid fa-location-crosshairs mr-1"></i>Look Up Address'
                : '<i class="fa-solid fa-magnifying-glass mr-1"></i>Find Addresses';
    }

    function showError(msg) {
        document.getElementById('errorText').textContent = msg;
        document.getElementById('errorMsg').classList.remove('hidden');
    }

    function hideError() {
        document.getElementById('errorMsg').classList.add('hidden');
    }

    // ─── Checkbox / selection ─────────────────────────────────────
    let selectedIndices = new Set();

    function onCardCheckChange(cb) {
        const idx  = parseInt(cb.dataset.idx, 10);
        const card = cb.closest('.prop-card');
        if (cb.checked) { selectedIndices.add(idx); card.classList.add('selected'); }
        else            { selectedIndices.delete(idx); card.classList.remove('selected'); }
        updateSelectionUI();
    }

    function updateSelectionUI() {
        const count = selectedIndices.size;
        document.getElementById('selectedCount').textContent = count;
        document.getElementById('selectAllLabel').textContent = isAllSelected() ? 'Deselect All' : 'Select All';
        document.getElementById('saveSelectedBtn').style.opacity = count > 0 ? '1' : '0.5';
    }

    function isAllSelected() {
        const boxes = document.querySelectorAll('.lead-check');
        return boxes.length > 0 && [...boxes].every(cb => cb.checked);
    }

    function toggleSelectAll() {
        const allSelected = isAllSelected();
        document.querySelectorAll('.lead-check').forEach(cb => {
            cb.checked = !allSelected;
            const idx  = parseInt(cb.dataset.idx, 10);
            const card = cb.closest('.prop-card');
            if (!allSelected) { selectedIndices.add(idx); card.classList.add('selected'); }
            else              { selectedIndices.delete(idx); card.classList.remove('selected'); }
        });
        updateSelectionUI();
    }

    // ── Save Selected ─────────────────────────────────────────────
    async function saveSelected() {
        if (selectedIndices.size === 0) { showToast('No addresses selected.', false); return; }

        const selected = [...selectedIndices].map(i => visibleProps[i]).filter(Boolean)
            .map(p => ({ address: p.address, lat: p.lat, lng: p.lng }));
        if (!selected.length) return;

        const btn = document.getElementById('saveSelectedBtn');
        btn.disabled = true;
        try {
            const resp = await fetch('/Leads/Save', {
                method:  'POST',
                headers: { 'Content-Type': 'application/json' },
                body:    JSON.stringify({ sourceAddress: currentAddress, properties: selected })
            });
            if (!resp.ok) {
                const err = await resp.json().catch(() => ({ error: 'Save failed' }));
                throw new Error(err.error || `HTTP ${resp.status}`);
            }
            const r = await resp.json();
            const msg = r.saved && r.updated ? `Saved ${r.saved} new, updated ${r.updated}`
                      : r.saved   ? `${r.saved} lead${r.saved!==1?'s':''} saved`
                      : `${r.updated} lead${r.updated!==1?'s':''} updated`;
            showToast(msg + ' — skip trace them in Saved Leads', true);
            document.querySelectorAll('.lead-check').forEach(cb => { cb.checked = false; cb.closest('.prop-card').classList.remove('selected'); });
            selectedIndices.clear();
            updateSelectionUI();
        } catch (e) {
            showToast(e.message || 'Save failed.', false);
        } finally {
            btn.disabled = false;
        }
    }

    // ── Toast ─────────────────────────────────────────────────────────
    let _toastTimer = null;
    function showToast(msg, success) {
        const toast = document.getElementById('toast');
        if (_toastTimer) clearTimeout(_toastTimer);
        toast.className = success ? 'success' : 'error';
        document.getElementById('toastIcon').className = 'fa-solid ' + (success ? 'fa-circle-check' : 'fa-circle-xmark');
        document.getElementById('toastMsg').textContent = msg;
        toast.offsetHeight; // force reflow
        toast.classList.add('show');
        _toastTimer = setTimeout(() => toast.classList.remove('show'), 3500);
    }

// ── Tab switching ─────────────────────────────────────────────
function switchMainTab(tab) {
    const isSearch = tab === 'search';

    [['tabBtnSearch', isSearch], ['tabBtnMap', !isSearch]].forEach(([id, active]) => {
        const btn = document.getElementById(id);
        btn.classList.toggle('border-brand',       active);
        btn.classList.toggle('text-white',         active);
        btn.classList.toggle('border-transparent', !active);
        btn.classList.toggle('text-slate-400',     !active);
    });

    document.getElementById('searchPanel').classList.toggle('hidden', !isSearch);
    document.getElementById('mapPanel').classList.toggle('hidden', isSearch);

    // Build the area map the first time its tab opens (Leaflet can't size a hidden container)
    if (!isSearch && typeof initAreaMap === 'function')
        initAreaMap(currentLat || 32.78, currentLng || -96.80);
}

// -- Scan mode toggle wiring --
(function() {
    var btnN = document.getElementById('modeNeighborhood');
    var btnS = document.getElementById('modeSingle');
    if (btnN) btnN.addEventListener('click', function() { switchScanMode('neighborhood'); });
    if (btnS) btnS.addEventListener('click', function() { switchScanMode('single'); });
})();

// -- Mobile nav hamburger (shared across pages) --
function toggleMobileMenu() {
    const menu = document.getElementById('mobileMenu');
    const icon = document.getElementById('mobileMenuIcon');
    if (!menu) return;
    const nowHidden = menu.classList.toggle('hidden');
    icon.className = nowHidden ? 'fa-solid fa-bars text-lg' : 'fa-solid fa-xmark text-lg';
}
