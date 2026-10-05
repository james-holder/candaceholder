// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

// ─── State ───────────────────────────────────────────────────
    let scanMode       = 'neighborhood';  // 'neighborhood' | 'single'
    let allProperties  = [];
    let allHailEvents  = [];  // hail events returned by last scan
    let currentFilter  = 'all';
    let currentSort    = 'risk';
    let currentAddress = '';
    let currentLat     = 0;
    let currentLng     = 0;
    let leafletMap          = null;
    let mapMarkers          = [];
    let hailOverlayLayer    = null;
    let hailOverlayVisible  = true;

    // ─── Scan mode toggle ─────────────────────────────────────────
    function switchScanMode(mode) {
        scanMode = mode;
        const isNeighborhood = mode === 'neighborhood';

        const btnN = document.getElementById('modeNeighborhood');
        const btnS = document.getElementById('modeSingle');
        const radiusWrapper = document.getElementById('radiusWrapper');
        const btnText = document.getElementById('btnText');

        if (isNeighborhood) {
            btnN.className = 'flex items-center gap-1.5 px-4 py-2 rounded-lg text-xs font-semibold transition-all bg-brand text-white shadow';
            btnS.className = 'flex items-center gap-1.5 px-4 py-2 rounded-lg text-xs font-semibold transition-all text-slate-400 hover:text-slate-200';
            radiusWrapper.classList.remove('hidden');
            btnText.innerHTML = '<i class="fa-solid fa-radar mr-1"></i>Scan Neighborhood';
        } else {
            btnS.className = 'flex items-center gap-1.5 px-4 py-2 rounded-lg text-xs font-semibold transition-all bg-brand text-white shadow';
            btnN.className = 'flex items-center gap-1.5 px-4 py-2 rounded-lg text-xs font-semibold transition-all text-slate-400 hover:text-slate-200';
            radiusWrapper.classList.add('hidden');
            btnText.innerHTML = '<i class="fa-solid fa-location-crosshairs mr-1"></i>Look Up Address';
        }
    }

    // ─── Scan ─────────────────────────────────────────────────────
    async function runScan() {
        const address = document.getElementById('addressInput').value.trim();

        if (!address) {
            showError('Please enter a neighborhood address.');
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
                    setLoading(false);
                    return;
                }
                lat = geo.lat;
                lng = geo.lng;
            }
            currentLat = lat;
            currentLng = lng;

            const url = scanMode === 'single'
                ? `/RoofHealth/SingleAddress?address=${encodeURIComponent(address)}&lat=${lat}&lng=${lng}`
                : `/RoofHealth/Neighborhood?address=${encodeURIComponent(address)}&radius=${document.getElementById('radiusSelect').value}&lat=${lat}&lng=${lng}`;
            const resp = await fetch(url);

            if (!resp.ok) {
                if (resp.status === 402) {
                    window.location.href = '/Billing/Upgrade';
                    return;
                }
                const err = await resp.json().catch(() => ({ error: 'Server error' }));
                throw new Error(err.message || err.error || `HTTP ${resp.status}`);
            }

            const data = await resp.json();
            currentAddress = address;
            allProperties  = data.properties  || [];
            allHailEvents  = data.hailEvents   || [];


            renderResults(data);
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
    function renderResults(data) {
        document.getElementById('centerLabel').textContent = data.centerAddress || currentAddress;

        // Update summary counts
        const high   = allProperties.filter(p => p.riskLevel === 'High').length;
        const medium = allProperties.filter(p => p.riskLevel === 'Medium').length;
        const low    = allProperties.filter(p => p.riskLevel === 'Low').length;
        document.getElementById('countTotal').textContent  = allProperties.length;
        document.getElementById('countHigh').textContent   = high;
        document.getElementById('countMedium').textContent = medium;
        document.getElementById('countLow').textContent    = low;

        // Show NOAA event count badge
        const noaaBadge = document.getElementById('noaaBadge');
        if (noaaBadge) {
            const eventCount = data.hailEventCount || 0;
            if (eventCount > 0) {
                noaaBadge.textContent = `NOAA: ${eventCount} hail event${eventCount !== 1 ? 's' : ''} found`;
                noaaBadge.className = 'summary-badge flex items-center gap-1.5 px-3 py-1.5 rounded-full bg-sky-500/10 border border-sky-500/30 text-sm text-sky-300';
            } else {
                noaaBadge.textContent = 'NOAA: no events in range — estimates shown';
                noaaBadge.className = 'summary-badge flex items-center gap-1.5 px-3 py-1.5 rounded-full bg-slate-700 border border-slate-600 text-sm text-slate-400';
            }
        }

        switchMainTab('neighborhood');
        document.getElementById('results').classList.remove('hidden');
        document.getElementById('results').scrollIntoView({ behavior: 'smooth', block: 'start' });

        // Show toolbar buttons after first scan
        document.getElementById('selectAllBtn').classList.remove('hidden');
        document.getElementById('saveSelectedBtn').classList.remove('hidden');

        // Init / re-init map
        initMap(data.lat, data.lng);
        renderHailOverlay(data.hailEvents || []);

        // Apply current filter + sort and render cards
        applyFilterAndSort();
    }

    // ─── Map ──────────────────────────────────────────────────────
    function initMap(lat, lng) {
        if (leafletMap) {
            leafletMap.remove();
            leafletMap = null;
        }
        mapMarkers = [];

        leafletMap = L.map('map', { center: [lat, lng], zoom: 15, zoomControl: true });

        const tileSatelliteImg = L.tileLayer(
            'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
            { attribution: 'Tiles &copy; Esri', maxZoom: 19 }
        );
        // World_Imagery has no place names — layer Esri's Boundaries_and_Places
        // reference tiles (transparent, city/place labels only) on top.
        const tileSatelliteLabels = L.tileLayer(
            'https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}',
            { attribution: 'Labels &copy; Esri', maxZoom: 19 }
        );
        const tileSatellite = L.layerGroup([tileSatelliteImg, tileSatelliteLabels]);
        const tileStreet = L.tileLayer(
            'https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png',
            { attribution: '&copy; CARTO', maxZoom: 19 }
        );
        tileSatellite.addTo(leafletMap);

        // Layer toggle button (top-right)
        let _satellite = true;
        const toggleCtrl = L.control({ position: 'topright' });
        toggleCtrl.onAdd = () => {
            const div = L.DomUtil.create('div');
            div.innerHTML = `<button id="mapLayerBtn" title="Switch map style"
                style="background:#1e293b;border:1px solid #475569;color:#cbd5e1;
                       padding:5px 10px;border-radius:8px;font-size:11px;font-weight:600;
                       cursor:pointer;display:flex;align-items:center;gap:5px;box-shadow:0 2px 6px rgba(0,0,0,0.4)">
                <i class="fa-solid fa-map"></i>&nbsp;Street
            </button>`;
            L.DomEvent.disableClickPropagation(div);
            div.querySelector('button').addEventListener('click', () => {
                _satellite = !_satellite;
                if (_satellite) {
                    tileStreet.remove(); tileSatellite.addTo(leafletMap);
                    div.querySelector('button').innerHTML = '<i class="fa-solid fa-map"></i>&nbsp;Street';
                } else {
                    tileSatellite.remove(); tileStreet.addTo(leafletMap);
                    div.querySelector('button').innerHTML = '<i class="fa-solid fa-satellite"></i>&nbsp;Satellite';
                }
            });
            return div;
        };
        toggleCtrl.addTo(leafletMap);

        // Hail overlay toggle (stacks below layer toggle)
        hailOverlayVisible = true;
        const hailCtrl = L.control({ position: 'topright' });
        hailCtrl.onAdd = () => {
            const div = L.DomUtil.create('div');
            div.innerHTML = `<button id="hailLayerBtn" title="Toggle hail event circles"
                style="background:#1e293b;border:1px solid #475569;color:#cbd5e1;
                       padding:5px 10px;border-radius:8px;font-size:11px;font-weight:600;
                       cursor:pointer;display:flex;align-items:center;gap:5px;
                       box-shadow:0 2px 6px rgba(0,0,0,0.4);margin-top:4px;white-space:nowrap">
                <i class="fa-solid fa-cloud-bolt" style="color:#f97316"></i>&nbsp;Hail ON
            </button>`;
            L.DomEvent.disableClickPropagation(div);
            div.querySelector('button').addEventListener('click', () => {
                hailOverlayVisible = !hailOverlayVisible;
                if (hailOverlayLayer) {
                    if (hailOverlayVisible) hailOverlayLayer.addTo(leafletMap);
                    else leafletMap.removeLayer(hailOverlayLayer);
                }
                const btn = div.querySelector('button');
                btn.innerHTML = hailOverlayVisible
                    ? '<i class="fa-solid fa-cloud-bolt" style="color:#f97316"></i>&nbsp;Hail ON'
                    : '<i class="fa-solid fa-cloud-bolt" style="color:#94a3b8"></i>&nbsp;Hail OFF';
                btn.style.opacity = hailOverlayVisible ? '1' : '0.55';
            });
            return div;
        };
        hailCtrl.addTo(leafletMap);

        // Center marker (star)
        const centerIcon = L.divIcon({
            html: `<div style="width:16px;height:16px;background:#f97316;border:3px solid #fff;border-radius:50%;box-shadow:0 0 8px rgba(249,115,22,0.8)"></div>`,
            iconSize: [16, 16],
            iconAnchor: [8, 8],
            className: ''
        });
        L.marker([lat, lng], { icon: centerIcon })
            .addTo(leafletMap)
            .bindPopup('<b style="color:#0f172a">📍 Search Center</b>');

        // Property markers added by applyFilterAndSort
    }

    function addMarkersToMap(properties) {
        // Clear old markers
        mapMarkers.forEach(m => leafletMap.removeLayer(m));
        mapMarkers = [];

        properties.forEach((p, idx) => {
            const color = riskColor(p.riskLevel);
            const icon = L.divIcon({
                html: `<div style="width:14px;height:14px;background:${color};border:2px solid rgba(255,255,255,0.7);
                              border-radius:50%;box-shadow:0 0 6px ${color}88;cursor:pointer"></div>`,
                iconSize: [14, 14],
                iconAnchor: [7, 7],
                className: ''
            });

            const m = L.marker([p.lat, p.lng], { icon })
                .addTo(leafletMap)
                .bindPopup(buildPopupHtml(p));

            m.on('click', () => {
                highlightCard(idx);
            });

            mapMarkers.push(m);
        });
    }

    // ── Hail event overlay ────────────────────────────────────────
    function renderHailOverlay(events) {
        if (!leafletMap) return;

        // Clear any previous layer
        if (hailOverlayLayer) {
            leafletMap.removeLayer(hailOverlayLayer);
            hailOverlayLayer = null;
        }
        if (!events || events.length === 0) return;

        hailOverlayLayer = L.layerGroup();
        events.forEach(function(e) {
            var color  = hailOverlayColor(e.sizeInches);
            var radius = Math.max(80, Math.min(600, e.sizeInches * 180)); // metres, scaled by size
            L.circle([e.lat, e.lng], {
                radius:      radius,
                color:       color,
                fillColor:   color,
                fillOpacity: 0.18,
                weight:      1.5,
                opacity:     0.65
            })
            .bindTooltip(
                '<div style="font-family:system-ui;color:#f1f5f9;min-width:110px">' +
                '<b>' + e.sizeInches.toFixed(2) + '" hail</b>' +
                '<br><span style="color:#94a3b8;font-size:11px">' + (e.date || '') + '</span>' +
                '<br><span style="color:#64748b;font-size:10px">' + (e.source || '').toUpperCase() + '</span>' +
                '</div>',
                { direction: 'top', sticky: true }
            )
            .addTo(hailOverlayLayer);
        });

        if (hailOverlayVisible) hailOverlayLayer.addTo(leafletMap);
    }

    function hailOverlayColor(size) {
        if (size >= 2.00) return '#ef4444'; // red    — golf ball+
        if (size >= 1.50) return '#f97316'; // orange — ping pong+
        if (size >= 1.00) return '#f59e0b'; // amber  — quarter+
        if (size >= 0.75) return '#eab308'; // yellow — penny+
        return '#84cc16';                   // lime   — pea
    }

    function buildPopupHtml(p) {
        const c = riskColor(p.riskLevel);
        let claimHtml = '';
        const _cwPopup = getClaimWindow(p.lastStormDate, p.address);
        if (_cwPopup && !_cwPopup.expired) {
            const leftLabel  = ` (~${_cwPopup.daysLeft}d left)`;
            const daysSince  = _cwPopup.totalDays - _cwPopup.daysLeft;
            if (daysSince <= 365) {
                claimHtml = `<div style="margin-top:5px;padding:3px 8px;border-radius:6px;background:#7c2d1222;border:1px solid #f9731644;font-size:11px;font-weight:700;color:#fb923c">🔥 Hot Lead — File Now!${leftLabel}</div>`;
            } else {
                claimHtml = `<div style="margin-top:5px;padding:3px 8px;border-radius:6px;background:#45350022;border:1px solid #ca8a0444;font-size:11px;font-weight:700;color:#fbbf24">⏳ Still Fileable${leftLabel}</div>`;
            }
        }
        return `
            <div style="min-width:180px;font-family:system-ui,sans-serif;color:#0f172a">
                <div style="font-weight:700;font-size:13px;margin-bottom:4px">${p.address}</div>
                <div style="display:inline-block;padding:2px 8px;border-radius:9999px;background:${c}22;
                            color:${c};font-size:11px;font-weight:600;border:1px solid ${c}44;margin-bottom:6px">
                    ${p.riskLevel} Risk
                </div>
                <div style="font-size:11px;color:#475569;line-height:1.6">
                    <b>Hail:</b> ${p.hailSize}<br>
                    <b>Storm:</b> ${p.lastStormDate}
                </div>
                ${claimHtml}
            </div>`;
    }

    // ─── Cards ────────────────────────────────────────────────────
    function applyFilterAndSort() {
        let props = [...allProperties];

        // Filter
        if (currentFilter !== 'all') {
            props = props.filter(p => p.riskLevel === currentFilter);
        }

        // Sort
        props.sort((a, b) => {
            if (currentSort === 'risk') {
                const order = { High: 0, Medium: 1, Low: 2 };
                return (order[a.riskLevel] ?? 3) - (order[b.riskLevel] ?? 3);
            }
            if (currentSort === 'date') {
                return new Date(b.lastStormDate) - new Date(a.lastStormDate);
            }
            return a.address.localeCompare(b.address);
        });

        renderCards(props);
        if (leafletMap) addMarkersToMap(props);
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
                        ? 'No address data found in OpenStreetMap for this area. Try a different address or a larger radius.'
                        : 'No properties match this filter.'}</p>
                </div>`;
            updateSelectionUI();
            return;
        }

        list.innerHTML = props.map((p, idx) => buildCardHtml(p, idx)).join('');

        // Attach click: pan map + toggle card details
        list.querySelectorAll('.prop-card').forEach((card, idx) => {
            card.addEventListener('click', () => {
                if (mapMarkers[idx]) {
                    leafletMap.setView([props[idx].lat, props[idx].lng], 16, { animate: true });
                    mapMarkers[idx].openPopup();
                }
                highlightCard(idx);

                // Toggle collapse
                const details  = card.querySelector('.card-details');
                const chevron  = card.querySelector('.card-chevron');
                const expanded = !details.classList.contains('hidden');
                details.classList.toggle('hidden', expanded);
                if (chevron) chevron.style.transform = expanded ? '' : 'rotate(180deg)';
            });
        });

        updateSelectionUI();
    }

    function buildCardHtml(p, idx) {
        const riskClass = {
            High:   'badge-high',
            Medium: 'badge-medium',
            Low:    'badge-low'
        }[p.riskLevel] || 'badge-low';

        const riskIcon = {
            High:   'fa-circle-exclamation',
            Medium: 'fa-circle-minus',
            Low:    'fa-circle-check'
        }[p.riskLevel] || 'fa-circle';

        const sourceBadge = (p.dataSource || '').includes('lsr')
            ? '<span class="source-badge source-badge--lsr">LSR+NOAA</span>'
            : (p.dataSource === 'noaa'
                ? '<span class="source-badge source-badge--noaa">NOAA</span>'
                : '<span class="source-badge source-badge--est">EST</span>');

        // ── Claim window badge — driven entirely by claim-window.js (state-specific SOL) ──
        let claimBadgeHtml = '';
        const _cw = getClaimWindow(p.lastStormDate, p.address);
        if (_cw && !_cw.expired) {
            const leftLabel  = `~${_cw.daysLeft}d left to file`;
            const stateNote  = `${_cw.state} — ${_cw.years}-yr window`;
            const daysSince  = _cw.totalDays - _cw.daysLeft;
            if (daysSince <= 365) {
                claimBadgeHtml = `
                <div class="col-span-2 rounded-lg px-3 py-2 claim-hot">
                    <div class="flex items-center gap-2">
                        <i class="fa-solid fa-fire text-orange-400"></i>
                        <span class="font-bold text-orange-300 text-xs">Hot Lead — File Now!</span>
                        <span class="ml-auto text-orange-400/70 text-xs">${leftLabel}</span>
                    </div>
                    <p class="text-orange-400/50 text-xs mt-1" style="font-size:10px">${stateNote} — consult your adjuster.</p>
                </div>`;
            } else {
                claimBadgeHtml = `
                <div class="col-span-2 rounded-lg px-3 py-2 claim-fileable">
                    <div class="flex items-center gap-2">
                        <i class="fa-solid fa-clock text-yellow-400"></i>
                        <span class="font-bold text-yellow-200 text-xs">Still Fileable</span>
                        <span class="ml-auto text-yellow-400/70 text-xs">${leftLabel}</span>
                    </div>
                    <p class="text-yellow-400/50 text-xs mt-1" style="font-size:10px">${stateNote} — consult your adjuster.</p>
                </div>`;
            }
        }

        return `
        <div class="prop-card bg-slate-800/70 border border-slate-700/60 rounded-2xl p-4 mb-3 cursor-pointer"
             data-idx="${idx}">
            <div class="flex items-start gap-3">
                <div class="pt-0.5" onclick="event.stopPropagation()">
                    <input type="checkbox" class="lead-check" data-idx="${idx}"
                           onchange="onCardCheckChange(this)" title="Select for saving" />
                </div>
                <div class="flex-1 min-w-0">
                    <div class="flex items-center justify-between gap-2">
                        <div class="flex-1 min-w-0">
                            <p class="font-bold text-white text-sm leading-tight truncate">${escapeHtml(p.address)}</p>
                        </div>
                        <div class="flex items-center gap-2 shrink-0">
                            <span class="${riskClass} flex items-center gap-1 px-2.5 py-1 rounded-full text-xs font-bold whitespace-nowrap">
                                <i class="fa-solid ${riskIcon}"></i>${p.riskLevel} Risk
                            </span>
                            <i class="fa-solid fa-chevron-down card-chevron text-slate-500 text-xs transition-transform duration-200"></i>
                        </div>
                    </div>
                </div>
            </div>

            <div class="card-details hidden grid grid-cols-2 gap-2 text-xs ml-7 mt-3">
                ${claimBadgeHtml}
                <div class="bg-slate-900/50 rounded-lg px-3 py-2">
                    <p class="text-slate-500 mb-0.5">Hail Size</p>
                    <p class="text-slate-200 font-semibold"><i class="fa-solid fa-cloud-showers-heavy text-brand mr-1"></i>${escapeHtml(p.hailSize)}</p>
                    <p class="text-slate-400 text-xs mt-0.5">${hailComparison(p.hailSize)}</p>
                </div>
                <div class="bg-slate-900/50 rounded-lg px-3 py-2">
                    <p class="text-slate-500 mb-0.5">Last Storm ${sourceBadge}</p>
                    <p class="text-slate-200 font-semibold"><i class="fa-solid fa-calendar-days text-brand mr-1"></i>${escapeHtml(p.lastStormDate)}</p>
                </div>
                <div class="bg-slate-900/50 rounded-lg px-3 py-2">
                    <p class="text-slate-500 mb-0.5">Nearby Events</p>
                    <p class="text-slate-200 font-semibold"><i class="fa-solid fa-cloud-bolt text-brand mr-1"></i>${(function(){
                        const n = nearbyHailCount(p.lat, p.lng, 10);
                        return n > 0 ? `${n} hail event${n !== 1 ? 's' : ''} (10 mi)` : 'None recorded';
                    })()}</p>
                </div>
                <div class="bg-slate-900/50 rounded-lg px-3 py-2">
                    <p class="text-slate-500 mb-0.5">Year Built</p>
                    <p class="text-slate-200 font-semibold"><i class="fa-solid fa-house text-brand mr-1"></i><span class="text-slate-500 italic text-xs">Save to look up</span></p>
                </div>
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

    // ─── Filter buttons ───────────────────────────────────────────
    document.querySelectorAll('.filter-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.filter-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            currentFilter = btn.dataset.filter;
            applyFilterAndSort();
        });
    });

    // ─── Sort select ──────────────────────────────────────────────
    document.getElementById('sortSelect').addEventListener('change', e => {
        currentSort = e.target.value;
        applyFilterAndSort();
    });

    // ─── Export / Print ───────────────────────────────────────────
    function exportCSV() {
        if (!currentAddress) { alert('Run a scan first.'); return; }
        const radius = document.getElementById('radiusSelect').value;
        window.location.href = `/RoofHealth/Export?address=${encodeURIComponent(currentAddress)}&radius=${radius}&lat=${currentLat}&lng=${currentLng}`;
    }

    function printList() {
        if (!allProperties.length) { alert('Run a scan first.'); return; }
        const rows = allProperties.map(p =>
            `<tr>
                <td style="padding:6px 10px;border-bottom:1px solid #e2e8f0">${escapeHtml(p.address)}</td>
                <td style="padding:6px 10px;border-bottom:1px solid #e2e8f0;color:${p.riskLevel==='High'?'#dc2626':p.riskLevel==='Medium'?'#ea580c':'#16a34a'};font-weight:600">${p.riskLevel}</td>
                <td style="padding:6px 10px;border-bottom:1px solid #e2e8f0">${p.hailSize}</td>
                <td style="padding:6px 10px;border-bottom:1px solid #e2e8f0">${p.lastStormDate}</td>
                <td style="padding:6px 10px;border-bottom:1px solid #e2e8f0">${p.dataSource}</td>
            </tr>`
        ).join('');

        const html = [
            '<!DOCTYPE html><html><head><title>StormLead Pro - Report</title>',
            '<style>',
            'body{font-family:system-ui,sans-serif;padding:24px;color:#0f172a}',
            'h2{font-size:18px;margin-bottom:8px}',
            'p{font-size:12px;color:#64748b;margin-bottom:16px}',
            'table{width:100%;border-collapse:collapse}',
            'th{text-align:left;padding:8px 10px;background:#0f172a;color:#fff;font-size:12px}',
            'td{font-size:12px;padding:6px 10px;border-bottom:1px solid #e2e8f0}',
            '<\/style><\/head><body>',
            '<h2>StormLead Pro - Neighborhood Report</h2>',
            '<p>Address: <b>' + escapeHtml(currentAddress) + '</b> | Generated: ' + new Date().toLocaleString() + ' | Total: ' + allProperties.length + '</p>',
            '<table><thead><tr>',
            '<th>Address</th><th>Risk</th><th>Hail Size</th><th>Last Storm</th><th>Data Source</th>',
            '</tr></thead><tbody>' + rows + '</tbody></table>',
            '<\/body><\/html>'
        ].join('');

        const blob = new Blob([html], { type: 'text/html' });
        const url  = URL.createObjectURL(blob);
        const win  = window.open(url, '_blank');
        setTimeout(() => { if (win) { win.print(); URL.revokeObjectURL(url); } }, 800);
    }

    // ─── Helpers ──────────────────────────────────────────────────
    // ─── Haversine distance (miles) ───────────────────────────────
    function haversineM(lat1, lng1, lat2, lng2) {
        const R = 3958.8; // Earth radius in miles
        const dLat = (lat2 - lat1) * Math.PI / 180;
        const dLng = (lng2 - lng1) * Math.PI / 180;
        const a = Math.sin(dLat/2)**2 +
                  Math.cos(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180) *
                  Math.sin(dLng/2)**2;
        return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
    }

    // Count hail events within radiusMi of a lat/lng
    function nearbyHailCount(lat, lng, radiusMi) {
        if (!allHailEvents || !allHailEvents.length) return 0;
        return allHailEvents.filter(e => haversineM(lat, lng, e.lat, e.lng) <= radiusMi).length;
    }

    function hailComparison(sizeStr) {
        const m = (sizeStr || '').match(/[\d.]+/);
        if (!m) return '';
        const s = parseFloat(m[0]);
        if (s >= 4.00) return '🥎 Softball';
        if (s >= 2.75) return '⚾ Baseball';
        if (s >= 2.50) return '🎾 Tennis Ball';
        if (s >= 2.00) return '🥚 Hen Egg';
        if (s >= 1.75) return '⛳ Golf Ball';
        if (s >= 1.50) return '🏓 Ping Pong';
        if (s >= 1.25) return '🪙 Half Dollar';
        if (s >= 1.00) return '🪙 Quarter';
        if (s >= 0.88) return '🪙 Nickel';
        if (s >= 0.75) return '🪙 Penny';
        if (s >= 0.50) return '🔵 Marble';
        return '🟡 Pea';
    }

    function riskColor(level) {
        return level === 'High' ? '#ef4444' : level === 'Medium' ? '#f97316' : '#22c55e';
    }

    function escapeHtml(str) {
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    const _scanSteps = [
        { icon: 'fa-map-pin',            label: 'Locating address…'       },
        { icon: 'fa-satellite-dish',      label: 'Fetching storm data…'    },
        { icon: 'fa-cloud-bolt',          label: 'Analyzing hail events…'  },
        { icon: 'fa-house-chimney-crack', label: 'Scoring properties…'     },
        { icon: 'fa-check',              label: 'Almost done…'            },
    ];
    let _stepTimers = [];

    function setLoading(on) {
        const btn  = document.getElementById('scanBtn');
        const text = document.getElementById('btnText');

        btn.disabled = on;
        _stepTimers.forEach(t => clearTimeout(t));
        _stepTimers = [];

        if (on) {
            // Show steps at 0s, 2s, 4s, 6s, 8s
            _scanSteps.forEach((step, i) => {
                const t = setTimeout(() => {
                    text.innerHTML =
                        `<i class="fa-solid ${step.icon} mr-1.5"></i>${step.label}`;
                }, i * 2000);
                _stepTimers.push(t);
            });
        } else {
            text.innerHTML = scanMode === 'single'
                ? '<i class="fa-solid fa-location-crosshairs mr-1"></i>Look Up Address'
                : '<i class="fa-solid fa-radar mr-1"></i>Scan Neighborhood';
        }
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
        const allChecked = isAllSelected();
        document.getElementById('selectAllLabel').textContent = allChecked ? 'Deselect All' : 'Select All';
        const saveBtn = document.getElementById('saveSelectedBtn');
        saveBtn.style.opacity = count > 0 ? '1' : '0.5';
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
            else               { selectedIndices.delete(idx); card.classList.remove('selected'); }
        });
        updateSelectionUI();
    }

    // ── Save Selected ─────────────────────────────────────────────
    async function saveSelected() {
        if (selectedIndices.size === 0) { showToast('No properties selected.', false); return; }

        // Re-derive the current sorted/filtered list to map indices correctly
        let props = [...allProperties];
        if (currentFilter !== 'all') props = props.filter(p => p.riskLevel === currentFilter);
        props.sort((a, b) => {
            if (currentSort === 'risk') { const o = {High:0,Medium:1,Low:2}; return (o[a.riskLevel]??3)-(o[b.riskLevel]??3); }
            if (currentSort === 'date') return new Date(b.lastStormDate)-new Date(a.lastStormDate);
            return a.address.localeCompare(b.address);
        });

        const selected = [...selectedIndices].map(i => props[i]).filter(Boolean);
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
            showToast(msg, true);
            // Deselect all and refresh table
            if (isAllSelected()) toggleSelectAll(); else {
                document.querySelectorAll('.lead-check').forEach(cb => { cb.checked = false; cb.closest('.prop-card').classList.remove('selected'); });
                selectedIndices.clear(); updateSelectionUI();
            }
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
    const isNeighborhood = tab === 'neighborhood';

    // Toggle tab button styles
    const btnN = document.getElementById('tabBtnNeighborhood');
    const btnS = document.getElementById('tabBtnStorm');
    if (btnN) {
        btnN.classList.toggle('border-brand', isNeighborhood);
        btnN.classList.toggle('text-white',   isNeighborhood);
        btnN.classList.toggle('border-transparent', !isNeighborhood);
        btnN.classList.toggle('text-slate-400',     !isNeighborhood);
    }
    if (btnS) {
        btnS.classList.toggle('border-brand', !isNeighborhood);
        btnS.classList.toggle('text-white',   !isNeighborhood);
        btnS.classList.toggle('border-transparent', isNeighborhood);
        btnS.classList.toggle('text-slate-400',     isNeighborhood);
    }

    // Show / hide panels
    const neighborhoodPanel = document.getElementById('neighborhoodPanel');
    const stormEl           = document.getElementById('stormExplorer');

    if (isNeighborhood) {
        if (neighborhoodPanel) neighborhoodPanel.classList.remove('hidden');
        if (stormEl)           stormEl.classList.add('hidden');
    } else {
        if (neighborhoodPanel) neighborhoodPanel.classList.add('hidden');
        if (stormEl)           stormEl.classList.remove('hidden');
        // Init Storm Explorer map (idempotent -- builds the map only once)
        if (typeof initStormExplorer === 'function') {
            initStormExplorer(currentLat || 32.78, currentLng || -96.80);
        }
    }
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
