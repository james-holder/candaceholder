// ══════════════════════════════════════════════════════════════════════════
// Storm Explorer  –  viewport-driven storm event browser
//   • Shows a Leaflet map the user can pan/zoom freely
//   • On each map move, fetches /RoofHealth/StormEvents for the visible area
//   • Renders a ranked event list on the left; the map itself shows radar-
//     derived MRMS MESH hail swaths (Phase 2) for whichever date(s) are
//     selected in that list — see syncMeshLayers()/loadMeshForDate() below.
//     (Older per-cluster dot markers + a client-side turf.js hull/buffer
//     approximation used to stand in for this; removed 2026-07-13 now that
//     real MESH swaths are available. See docs/mesh-phase2-handoff.md.)
//   • Filter controls: min hail size, lookback period, wind toggle
// ══════════════════════════════════════════════════════════════════════════
(function () {
    'use strict';

    // ── Internal state ──────────────────────────────────────────────────
    let seMap       = null;         // Leaflet map instance
    let seLoading   = false;
    let seDebounce  = null;
    let seStorms    = [];           // last fetched cluster array
    let seState     = '';           // detected 2-letter state abbr
    let seInitialized = false;

    let seSelectedDates  = null;    // Set of selected (checked/"on") date strings; null = not yet initialized
    let seKnownDates     = new Set(); // every date string ever seen this session — lets renderSeList tell
                                      // "date I've already shown before" (respect current on/off) apart from
                                      // "brand new date, e.g. revealed by zooming out" (defaults on)
    let seHoverControl   = null;    // fixed hover info panel (topright)

    // Hail Reports overlay state
    let hailReportsLayer   = null;    // Leaflet GeoJSON layer — individual LSR/SPC event dots
    let hailReportsVisible = false;
    let hailLegendControl  = null;    // L.control floating legend

    // Hail Swath Polygons overlay state
    let swathLayer   = null;          // Leaflet GeoJSON layer — size-banded convex hull polygons
    let swathVisible = false;
    let swathLegend  = null;          // L.control floating legend for swaths

    // Radar MESH — the primary storm visualization (Phase 2, true radar-derived
    // per-pixel hail size; gated server-side behind FeatureFlags:MeshSwaths —
    // see docs/mesh-phase2-handoff.md). One Leaflet layer per selected date,
    // keyed by date string, kept in sync with seSelectedDates (the date cards
    // in the left panel) by syncMeshLayers(). No client-side fallback: if the
    // flag is off or a date's swath comes back empty, that date just shows no
    // polygon — by design, until Phase 2 is verified in a real environment.
    let meshLayers = {};

    // Default selection is "every date in the period," which can mean 20-30+
    // dates at once — firing that many simultaneous /RoofHealth/MeshSwath
    // requests overwhelmed the server (each one triggers a grib download +
    // GDAL pipeline on cache miss) and came back as 502s. Queue wanted dates
    // and only let a few fetches run at a time; the rest wait their turn.
    // See PipelineGate in MeshSwathService.cs for the matching server-side cap.
    var MESH_FETCH_CONCURRENCY = 3;
    var meshFetchQueue    = [];   // date strings waiting to be fetched
    var meshFetchInFlight = new Set(); // date strings currently being fetched

    // Draw-a-rectangle bulk lead capture ("Select Area" button)
    var seAreaSelectMode = false;   // true while armed, from button click to a completed drag
    var seAreaDrawing    = false;   // true only during the actual mousedown->mouseup drag
    var seAreaStart      = null;    // L.LatLng where the drag began
    var seAreaRect       = null;    // live preview L.Rectangle while dragging

    // ── Public API ──────────────────────────────────────────────────────

    /**
     * Called by switchMainTab when Storm Explorer becomes visible.
     * Safe to call multiple times — map is only built once.
     * @param {number} defaultLat - initial map center lat
     * @param {number} defaultLng - initial map center lng
     */
    window.initStormExplorer = function (defaultLat, defaultLng) {
        if (seInitialized) {
            if (seMap) setTimeout(function () { seMap.invalidateSize(); }, 100);
            return;
        }
        seInitialized = true;
        buildSeMap(defaultLat || 32.78, defaultLng || -96.80);
        scheduleSeLoad(200);
    };

    /** Called by filter controls (onchange). */
    window.applySeFilters = function () {
        scheduleSeLoad(400);
    };

    /**
     * Toggle the Hail Reports dot overlay on/off.
     * Shows individual LSR + SPC hail event reports as coloured circles,
     * sized and coloured by hail diameter.
     */
    window.toggleHailReports = function () {
        if (!seMap) return;
        hailReportsVisible = !hailReportsVisible;

        var btn = document.getElementById('mrmsToggleBtn');
        if (btn) {
            btn.classList.toggle('border-sky-500',   hailReportsVisible);
            btn.classList.toggle('text-sky-300',     hailReportsVisible);
            btn.classList.toggle('bg-sky-500/10',    hailReportsVisible);
            btn.classList.toggle('border-slate-600', !hailReportsVisible);
            btn.classList.toggle('text-slate-400',   !hailReportsVisible);
            btn.classList.toggle('bg-slate-900',     !hailReportsVisible);
        }

        if (hailReportsVisible) {
            loadHailReportsLayer();
            addHailLegend();
        } else {
            if (hailReportsLayer) { seMap.removeLayer(hailReportsLayer); hailReportsLayer = null; }
            if (hailLegendControl) { hailLegendControl.remove(); hailLegendControl = null; }
        }
    };

    /**
     * Toggle the Hail Swath Polygon overlay on/off.
     * Shows size-banded convex-hull polygons — one ring per hail size tier per storm cluster.
     * Outer ring = pea hail (≥0.75"), inner rings progressively smaller and more intense coloured.
     */
    window.toggleHailSwaths = function () {
        if (!seMap) return;
        swathVisible = !swathVisible;

        var btn = document.getElementById('swathToggleBtn');
        if (btn) {
            btn.classList.toggle('border-orange-500',   swathVisible);
            btn.classList.toggle('text-orange-300',     swathVisible);
            btn.classList.toggle('bg-orange-500/10',    swathVisible);
            btn.classList.toggle('border-slate-600',    !swathVisible);
            btn.classList.toggle('text-slate-400',      !swathVisible);
            btn.classList.toggle('bg-slate-900',        !swathVisible);
        }

        if (swathVisible) {
            loadSwathLayer();
            addSwathLegend();
        } else {
            if (swathLayer)  { seMap.removeLayer(swathLayer);  swathLayer  = null; }
            if (swathLegend) { swathLegend.remove();           swathLegend = null; }
        }
    };

    /**
     * Arm/disarm "Select Area" — click-drag a rectangle on the map to bulk-
     * capture every address inside it as a lead. While armed, map dragging
     * is disabled so a drag draws a rectangle instead of panning; the actual
     * draw is handled by seAreaMouseDown/Move/Up (bound once in buildSeMap).
     */
    window.toggleSeAreaSelect = function () {
        if (!seMap) return;
        seAreaSelectMode = !seAreaSelectMode;

        var btn = document.getElementById('seAreaSelectBtn');
        if (btn) {
            btn.classList.toggle('border-brand',     seAreaSelectMode);
            btn.classList.toggle('text-white',       seAreaSelectMode);
            btn.classList.toggle('bg-brand/20',      seAreaSelectMode);
            btn.classList.toggle('border-slate-600', !seAreaSelectMode);
            btn.classList.toggle('text-slate-400',   !seAreaSelectMode);
            btn.classList.toggle('bg-slate-900',     !seAreaSelectMode);
        }

        seMap.dragging[seAreaSelectMode ? 'disable' : 'enable']();
        seMap.getContainer().style.cursor = seAreaSelectMode ? 'crosshair' : '';

        if (!seAreaSelectMode && seAreaRect) {
            seMap.removeLayer(seAreaRect);
            seAreaRect = null;
        }
    };

    // ── Draw-a-rectangle handlers (bound once in buildSeMap) ──────────────
    function seAreaMouseDown(e) {
        if (!seAreaSelectMode) return;
        seAreaDrawing = true;
        seAreaStart   = e.latlng;
        if (seAreaRect) { seMap.removeLayer(seAreaRect); seAreaRect = null; }
    }

    function seAreaMouseMove(e) {
        if (!seAreaDrawing || !seAreaStart) return;
        var bounds = L.latLngBounds(seAreaStart, e.latlng);
        if (seAreaRect) seAreaRect.setBounds(bounds);
        else seAreaRect = L.rectangle(bounds, { color: '#f97316', weight: 2, fillOpacity: 0.08 }).addTo(seMap);
    }

    function seAreaMouseUp(e) {
        if (!seAreaDrawing) return;
        seAreaDrawing = false;
        var start = seAreaStart;
        seAreaStart = null;
        if (!start) return;

        var bounds = L.latLngBounds(start, e.latlng);
        var sw = bounds.getSouthWest(), ne = bounds.getNorthEast();

        // A click (no real drag) collapses to a ~zero-size box — ignore it
        // and stay in select mode so the user can try dragging instead.
        if (seHaversine(sw.lat, sw.lng, ne.lat, ne.lng) < 0.02) {
            if (seAreaRect) { seMap.removeLayer(seAreaRect); seAreaRect = null; }
            return;
        }

        fetchSeAreaLeads(bounds);

        // Leaflet still fires a 'click' event right after this mouseup even
        // though map dragging is disabled (no actual pan occurred) — defer
        // turning select mode off so handleSeMapClick's own guard (checked
        // first thing below) sees seAreaSelectMode still true and bails,
        // instead of also popping up a single-address "Add to Leads" popup.
        setTimeout(function () { window.toggleSeAreaSelect(); }, 0);
    }

    function fetchSeAreaLeads(bounds) {
        var north = bounds.getNorth(), south = bounds.getSouth();
        var east  = bounds.getEast(),  west  = bounds.getWest();

        if (window.showToast) showToast('Scanning selected area…', true);

        fetch('/RoofHealth/Area?north=' + north + '&south=' + south + '&east=' + east + '&west=' + west)
            .then(function (resp) {
                if (!resp.ok) return resp.json().then(function (err) { throw new Error(err.error || 'Area scan failed.'); });
                return resp.json();
            })
            .then(function (data) {
                if (!data.properties || !data.properties.length) {
                    if (window.showToast) showToast('No addresses found in that area.', false);
                    return;
                }
                // Hand off to the existing neighborhood-scan results section
                // (renderResults/allProperties/Select All/Save Selected in
                // site.js) — it already does exactly what's needed here:
                // review a property list with risk badges, then bulk-save.
                if (typeof currentAddress !== 'undefined') currentAddress = data.centerAddress;
                if (typeof allProperties  !== 'undefined') allProperties  = data.properties || [];
                if (typeof allHailEvents  !== 'undefined') allHailEvents  = data.hailEvents  || [];
                if (typeof renderResults === 'function') renderResults(data);
            })
            .catch(function (err) {
                if (window.showToast) showToast(err.message || 'Could not scan that area.', false);
            });
    }

    /** Map a sizeBand threshold to a stroke/fill colour (red centre → yellow outer). */
    function swathColor(sizeBand) {
        if (sizeBand >= 3.0)  return '#7c3aed'; // purple  — grapefruit
        if (sizeBand >= 2.5)  return '#dc2626'; // dark red — baseball
        if (sizeBand >= 2.0)  return '#ef4444'; // red      — golf ball
        if (sizeBand >= 1.75) return '#f97316'; // red-orange
        if (sizeBand >= 1.5)  return '#fb923c'; // orange   — ping pong
        if (sizeBand >= 1.25) return '#f59e0b'; // amber    — half dollar
        if (sizeBand >= 1.0)  return '#eab308'; // yellow   — quarter
        return '#84cc16';                        // yellow-green — penny
    }

    /** Fetch banded swath polygons for the current viewport and render as filled polygons. */
    function loadSwathLayer() {
        if (!seMap) return;
        var bounds   = seMap.getBounds();
        var lookback = parseInt(document.getElementById('seLookback')?.value || '90', 10);

        fetch('/RoofHealth/HailSwathPolygons?' + new URLSearchParams({
            minLat:       bounds.getSouth().toFixed(4),
            maxLat:       bounds.getNorth().toFixed(4),
            minLng:       bounds.getWest().toFixed(4),
            maxLng:       bounds.getEast().toFixed(4),
            lookbackDays: lookback
        }))
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (geojson) {
            if (!geojson || !seMap || !swathVisible) return;

            if (swathLayer) seMap.removeLayer(swathLayer);

            swathLayer = L.geoJSON(geojson, {
                style: function (feature) {
                    var band  = (feature.properties && feature.properties.sizeBand) || 0.75;
                    var color = swathColor(band);
                    return {
                        color:       color,
                        weight:      1.5,
                        opacity:     0.75,
                        fillColor:   color,
                        fillOpacity: band >= 2.0 ? 0.28 : 0.18
                    };
                },
                onEachFeature: function (feature, layer) {
                    var p    = feature.properties || {};
                    var band = p.sizeBand || 0;
                    var coinLabel = band >= 3.0  ? 'Grapefruit+'
                                 : band >= 2.5  ? 'Baseball'
                                 : band >= 2.0  ? 'Golf Ball'
                                 : band >= 1.75 ? 'Ping Pong'
                                 : band >= 1.5  ? 'Walnut'
                                 : band >= 1.25 ? 'Half Dollar'
                                 : band >= 1.0  ? 'Quarter'
                                 : 'Penny';
                    layer.bindTooltip(
                        '<b>' + band.toFixed(2) + '&quot;+ hail swath</b> &mdash; ' + coinLabel + '<br>' +
                        (p.date || '') + '<br>' +
                        '<span style="opacity:0.7;font-size:0.85em">' + (p.reportCount || 0) + ' ground reports · max ' + (p.maxHailIn ? p.maxHailIn.toFixed(2) + '"' : '—') + '</span>',
                        { sticky: true }
                    );
                }
            }).addTo(seMap);

            console.info('[Hail Swaths] Rendered ' + (geojson.features ? geojson.features.length : 0) + ' banded polygons.');
        })
        .catch(function (err) { console.warn('[Hail Swaths]', err); });
    }

    /** Floating legend for swath bands. */
    function addSwathLegend() {
        if (!seMap || swathLegend) return;

        swathLegend = L.control({ position: 'bottomleft' });
        swathLegend.onAdd = function () {
            var div = L.DomUtil.create('div');
            div.style.cssText = [
                'background:rgba(15,20,30,0.88)',
                'border:1px solid rgba(100,116,139,0.45)',
                'border-radius:10px',
                'padding:10px 13px',
                'font-family:inherit',
                'font-size:12px',
                'line-height:1.6',
                'color:#cbd5e1',
                'min-width:170px',
                'box-shadow:0 2px 12px rgba(0,0,0,0.5)',
                'pointer-events:none'
            ].join(';');

            var rows = [
                ['#7c3aed', '≥ 3.0"', 'Grapefruit+'],
                ['#dc2626', '≥ 2.5"', 'Baseball'],
                ['#ef4444', '≥ 2.0"', 'Golf Ball'],
                ['#fb923c', '≥ 1.5"', 'Ping Pong'],
                ['#eab308', '≥ 1.0"', 'Quarter'],
                ['#84cc16', '≥ 0.75"','Penny'],
            ];

            div.innerHTML =
                '<div style="font-weight:700;font-size:11px;letter-spacing:.06em;' +
                    'text-transform:uppercase;color:#94a3b8;margin-bottom:7px">Hail Swath Size</div>' +
                rows.map(function (r) {
                    return '<div style="display:flex;align-items:center;gap:7px;margin-bottom:3px">' +
                        '<span style="display:inline-block;width:13px;height:13px;border-radius:3px;' +
                            'background:' + r[0] + ';opacity:0.85;flex-shrink:0"></span>' +
                        '<span>' + r[1] + ' <span style="opacity:0.6">(' + r[2] + ')</span></span>' +
                        '</div>';
                }).join('');

            return div;
        };
        swathLegend.addTo(seMap);
    }

    /** Fetch individual LSR/SPC hail events for the current viewport and render as dots. */
    function loadHailReportsLayer() {
        if (!seMap) return;
        var bounds   = seMap.getBounds();
        var lookback = parseInt(document.getElementById('seLookback')?.value || '90', 10);

        fetch('/RoofHealth/HailSwath?' + new URLSearchParams({
            minLat:       bounds.getSouth().toFixed(4),
            maxLat:       bounds.getNorth().toFixed(4),
            minLng:       bounds.getWest().toFixed(4),
            maxLng:       bounds.getEast().toFixed(4),
            lookbackDays: lookback
        }))
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (geojson) {
            if (!geojson || !seMap || !hailReportsVisible) return;

            var features = geojson.features;
            if (!features || features.length === 0) {
                console.info('[Hail Reports] No events found for this area/period.');
                return;
            }

            if (hailReportsLayer) seMap.removeLayer(hailReportsLayer);

            hailReportsLayer = L.geoJSON(geojson, {
                pointToLayer: function (feature, latlng) {
                    var sizeIn = (feature.properties && feature.properties.maxHailIn) || 0;
                    var color  = sizeIn >= 2.0 ? '#ef4444'
                               : sizeIn >= 1.0 ? '#f97316'
                               : '#eab308';
                    var radius = Math.max(5, Math.min(18, sizeIn * 9));
                    return L.circleMarker(latlng, {
                        radius:      radius,
                        color:       color,
                        fillColor:   color,
                        fillOpacity: 0.55,
                        weight:      1,
                        opacity:     0.9
                    });
                },
                onEachFeature: function (feature, layer) {
                    var p = feature.properties || {};
                    var srcLabel = p.source === 'lsr'      ? 'NWS Spotter'
                                 : p.source === 'SPC/NOAA' ? 'SPC Report'
                                 : (p.source || 'NOAA');
                    layer.bindTooltip(
                        '<b>' + (p.maxHailIn ? p.maxHailIn.toFixed(2) + '" hail' : '\u2014') + '</b><br>' +
                        (p.date || '') + '<br>' +
                        '<span style="opacity:0.7;font-size:0.85em">' + srcLabel + '</span>',
                        { sticky: true }
                    );
                }
            }).addTo(seMap);

            console.info('[Hail Reports] Rendered ' + features.length + ' events.');
        })
        .catch(function (err) { console.warn('[Hail Reports]', err); });
    }

    /** Create and attach the floating hail-size legend to the map. */
    function addHailLegend() {
        if (!seMap || hailLegendControl) return;

        hailLegendControl = L.control({ position: 'bottomleft' });

        hailLegendControl.onAdd = function () {
            var div = L.DomUtil.create('div');
            div.style.cssText = [
                'background:rgba(15,20,30,0.88)',
                'border:1px solid rgba(100,116,139,0.45)',
                'border-radius:10px',
                'padding:10px 13px',
                'font-family:inherit',
                'font-size:12px',
                'line-height:1.55',
                'color:#cbd5e1',
                'min-width:155px',
                'box-shadow:0 2px 12px rgba(0,0,0,0.5)',
                'pointer-events:none'
            ].join(';');

            div.innerHTML =
                '<div style="font-weight:700;font-size:11px;letter-spacing:.06em;' +
                    'text-transform:uppercase;color:#94a3b8;margin-bottom:7px">' +
                    '<i class="fa-solid fa-circle-dot" style="color:#38bdf8;margin-right:5px"></i>' +
                    'Hail Reports' +
                '</div>' +
                '<div style="display:flex;align-items:center;gap:8px;margin-bottom:5px">' +
                    '<svg width="18" height="18" viewBox="0 0 18 18">' +
                        '<circle cx="9" cy="9" r="7" fill="#ef4444" fill-opacity="0.55" stroke="#ef4444" stroke-width="1.2"/>' +
                    '</svg>' +
                    '<span><b style="color:#f87171">\u2265 2.0"</b>' +
                    ' <span style="color:#64748b;font-size:10px">golf ball</span></span>' +
                '</div>' +
                '<div style="display:flex;align-items:center;gap:8px;margin-bottom:5px">' +
                    '<svg width="14" height="14" viewBox="0 0 14 14">' +
                        '<circle cx="7" cy="7" r="5.5" fill="#f97316" fill-opacity="0.55" stroke="#f97316" stroke-width="1.2"/>' +
                    '</svg>' +
                    '<span><b style="color:#fb923c">\u2265 1.0"</b>' +
                    ' <span style="color:#64748b;font-size:10px">quarter</span></span>' +
                '</div>' +
                '<div style="display:flex;align-items:center;gap:8px">' +
                    '<svg width="10" height="10" viewBox="0 0 10 10">' +
                        '<circle cx="5" cy="5" r="3.8" fill="#eab308" fill-opacity="0.55" stroke="#eab308" stroke-width="1.2"/>' +
                    '</svg>' +
                    '<span><b style="color:#facc15">&lt; 1.0"</b>' +
                    ' <span style="color:#64748b;font-size:10px">pea / dime</span></span>' +
                '</div>' +
                '<div style="margin-top:8px;padding-top:7px;border-top:1px solid rgba(100,116,139,0.25);' +
                    'color:#475569;font-size:10px">' +
                    'NWS Spotter &amp; SPC Reports' +
                '</div>';

            return div;
        };

        hailLegendControl.addTo(seMap);
    }

    // ── Map construction ────────────────────────────────────────────────
    function buildSeMap(lat, lng) {
        seMap = L.map('seMapContainer', { center: [lat, lng], zoom: 10, zoomControl: true });

        var tileSat = L.tileLayer(
            'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
            { attribution: 'Tiles &copy; Esri', maxZoom: 19 }
        );
        // World_Imagery is unlabeled satellite/aerial imagery — Esri's
        // Boundaries_and_Places reference layer is a transparent overlay of
        // city/place names + borders meant to sit on top of it. Grouped with
        // tileSat so both add/remove together as one "Satellite" entry in
        // the layer control (this is Esri's standard hybrid-satellite combo).
        var tileSatLabels = L.tileLayer(
            'https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}',
            { attribution: 'Labels &copy; Esri', maxZoom: 19 }
        );
        var tileSatGroup = L.layerGroup([tileSat, tileSatLabels]);

        var mtKey = window.MAPTILER_KEY || '';
        var tileStreet = mtKey
            ? L.tileLayer(
                'https://api.maptiler.com/maps/dataviz-dark/{z}/{x}/{y}.png?key=' + mtKey,
                { attribution: '&copy; <a href="https://www.maptiler.com/">MapTiler</a> &copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>', maxZoom: 20, tileSize: 512, zoomOffset: -1 }
              )
            : L.tileLayer(
                'https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png',
                { attribution: '&copy; CARTO', maxZoom: 19 }
              );
        tileSatGroup.addTo(seMap);
        L.control.layers(
            { 'Dark Street': tileStreet, 'Satellite': tileSatGroup },
            {},
            { position: 'topright' }
        ).addTo(seMap);

        // Hover info panel — sits below the layer toggle, updates on swath mouseover
        seHoverControl = L.control({ position: 'topright' });
        seHoverControl.onAdd = function () {
            var div = L.DomUtil.create('div', 'se-hover-panel');
            div.style.cssText = 'display:none';
            return div;
        };
        seHoverControl.addTo(seMap);

        seMap.on('moveend', function () { scheduleSeLoad(700); });
        seMap.on('click', handleSeMapClick);
        seMap.on('mousedown', seAreaMouseDown);
        seMap.on('mousemove', seAreaMouseMove);
        seMap.on('mouseup', seAreaMouseUp);
    }

    // ── Click-to-add-lead ────────────────────────────────────────────────
    // Separate from the swath hover panel above (that's mouseover/mouseout
    // on individual swath layers; this is a map-level click listener, so
    // both work independently — clicking on top of a swath still resolves
    // the address under the cursor, not the swath itself).
    function handleSeMapClick(e) {
        // Area-select drags also end in a map 'click' event — let seAreaMouseUp
        // own that gesture instead of also popping up a single-address add.
        if (seAreaSelectMode || seAreaDrawing) return;

        var lat = e.latlng.lat, lng = e.latlng.lng;

        var popup = L.popup({ closeButton: true, maxWidth: 260 })
            .setLatLng(e.latlng)
            .setContent('<div style="font-size:13px;color:#0f172a">Looking up address&hellip;</div>')
            .openOn(seMap);

        if (!window.google || !google.maps || !google.maps.Geocoder) {
            popup.setContent('<div style="font-size:13px;color:#dc2626">Address lookup isn\'t ready yet &mdash; wait a moment and try again.</div>');
            return;
        }

        new google.maps.Geocoder().geocode({ location: { lat: lat, lng: lng } }, function (results, status) {
            // Popup may have been closed (user clicked elsewhere) before this resolves.
            if (!seMap.hasLayer(popup)) return;

            if (status !== 'OK' || !results || !results.length) {
                popup.setContent('<div style="font-size:13px;color:#dc2626">No address found at this location.</div>');
                return;
            }

            var address = results[0].formatted_address;
            popup.setContent('<div style="font-size:13px;color:#0f172a">Checking storm history&hellip;</div>');

            // Pull real risk/hail/storm-date for this point the same way the
            // neighborhood/single-address scan does — /Leads/Save only stores
            // whatever fields it's given, so without this the saved lead (and
            // its PDF report, which reads lead.RiskLevel/HailSize/LastStormDate
            // directly) shows "Unknown"/"Not recorded".
            fetch('/RoofHealth/SingleAddress?address=' + encodeURIComponent(address) +
                  '&lat=' + lat + '&lng=' + lng)
                .then(function (resp) { return resp.ok ? resp.json() : null; })
                .then(function (data) {
                    if (!seMap.hasLayer(popup)) return;

                    var record = data && data.properties && data.properties[0];
                    // Fall back to a bare record (no storm data) rather than
                    // blocking the add entirely if the lookup fails/errors.
                    if (!record) record = { address: address, lat: lat, lng: lng, riskLevel: '', hailSize: '', lastStormDate: '' };

                    var hasRisk  = !!record.riskLevel;
                    var color    = hasRisk ? riskColor(record.riskLevel) : '#94a3b8';
                    var riskText = hasRisk ? record.riskLevel : 'Unknown';
                    var hailText = record.hailSize && record.hailSize !== 'No data' ? record.hailSize : 'No data';
                    var dateText = record.lastStormDate && record.lastStormDate !== 'No data' ? record.lastStormDate : 'No data';

                    popup.setContent(
                        '<div style="font-size:13px;min-width:210px">' +
                        '<div style="font-weight:700;color:#0f172a;margin-bottom:8px;line-height:1.3">' + escapeHtml(record.address || address) + '</div>' +
                        '<div style="display:flex;align-items:center;gap:6px;margin-bottom:8px">' +
                        '<span style="width:8px;height:8px;border-radius:50%;background:' + color + ';display:inline-block"></span>' +
                        '<span style="font-weight:700;color:' + color + '">' + riskText + ' risk</span>' +
                        '<span style="color:#64748b">&middot; ' + hailText + ' &middot; ' + dateText + '</span>' +
                        '</div>' +
                        '<button id="seAddLeadBtn" type="button" ' +
                        'style="background:#f97316;color:#fff;border:none;border-radius:8px;padding:7px 12px;font-size:12px;font-weight:700;cursor:pointer;width:100%">' +
                        'Add to Leads</button>' +
                        '</div>'
                    );

                    // Scoped to THIS popup's own DOM subtree rather than a bare
                    // document.getElementById lookup — every popup reuses the
                    // same "seAddLeadBtn" id, so a global lookup risked grabbing
                    // a previous (closing/closed) popup's button on a second
                    // click, silently attaching the click handler to a dead
                    // node and leaving the visible "Add to Leads" button inert.
                    var popupEl = popup.getElement();
                    var btn = popupEl ? popupEl.querySelector('#seAddLeadBtn') : null;
                    if (!btn) return;
                    btn.onclick = function () {
                        btn.disabled    = true;
                        btn.textContent = 'Adding…';
                        fetch('/Leads/Save', {
                            method:  'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({
                                sourceAddress: 'Storm Explorer (map click)',
                                properties: [record]
                            })
                        })
                        .then(function (resp) {
                            if (!resp.ok) return resp.json().then(function (err) { throw new Error(err.error || 'Save failed'); });
                            return resp.json();
                        })
                        .then(function () {
                            if (window.showToast) showToast('Lead added: ' + (record.address || address), true);
                            seMap.closePopup();
                        })
                        .catch(function (err) {
                            if (window.showToast) showToast(err.message || 'Could not add lead.', false);
                            btn.disabled    = false;
                            btn.textContent = 'Add to Leads';
                        });
                    };
                })
                .catch(function () {
                    if (!seMap.hasLayer(popup)) return;
                    popup.setContent('<div style="font-size:13px;color:#dc2626">Could not look up storm history for this location.</div>');
                });
        });
    }

    // ── Debounced load ───────────────────────────────────────────────────
    function scheduleSeLoad(ms) {
        clearTimeout(seDebounce);
        seDebounce = setTimeout(loadSeEvents, ms);
    }

    // ── Fetch from backend ───────────────────────────────────────────────
    function loadSeEvents() {
        if (!seMap || seLoading) return;
        seLoading = true;

        var center = seMap.getCenter();
        var bounds = seMap.getBounds();
        var radiusMiles = Math.min(
            seHaversine(center.lat, center.lng, bounds.getNorth(), center.lng) * 0.85,
            200
        );

        var minHail  = parseFloat(document.getElementById('seMinHail')?.value  || '0.75');
        var lookback = parseInt(document.getElementById('seLookback')?.value   || '90', 10);
        var wind     = (document.getElementById('seIncludeWind')?.checked !== false);

        setSeLoading(true);

        var params = new URLSearchParams({
            lat:           center.lat.toFixed(6),
            lng:           center.lng.toFixed(6),
            state:         seState,
            radiusMiles:   radiusMiles.toFixed(1),
            minHailInches: minHail.toFixed(2),
            includeWind:   wind,
            lookbackDays:  lookback
        });

        fetch('/RoofHealth/StormEvents?' + params.toString())
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (data) {
                if (data.state) seState = data.state;
                seStorms = data.storms || [];
                renderSeCount(seStorms.length);
                renderSeList(seStorms);  // preserves/prunes seSelectedDates — see there
                syncMeshLayers();
            })
            .catch(function (err) {
                console.warn('[StormExplorer] fetch failed:', err);
            })
            .finally(function () {
                seLoading = false;
                setSeLoading(false);
            });
    }

    // ── Render event list — grouped by date ──────────────────────────────
    function renderSeList(storms) {
        var list = document.getElementById('seList');
        if (!list) return;

        if (!storms || storms.length === 0) {
            list.innerHTML =
                '<div class="flex flex-col items-center justify-center py-14 text-center text-slate-500 text-sm gap-3">' +
                '<i class="fa-solid fa-magnifying-glass-location text-4xl text-slate-700"></i>' +
                '<div>No storms found in this area.<br>Try wider filters or pan the map.</div>' +
                '</div>';
            return;
        }

        // Group clusters by date
        var byDate = {};
        storms.forEach(function (s) {
            var key = s.date || 'unknown';
            if (!byDate[key]) byDate[key] = [];
            byDate[key].push(s);
        });

        // Sort dates descending
        var dates = Object.keys(byDate).sort(function (a, b) { return b.localeCompare(a); });

        if (!seSelectedDates) {
            // True first load: default to every date in the selected time
            // period "on" (all storms within the Period filter show their
            // swath), not just the most recent one.
            seSelectedDates = new Set(dates);
            dates.forEach(function (d) { seKnownDates.add(d); });
        } else {
            // Re-fetch (e.g. the map was panned/zoomed, which re-runs this on
            // every moveend, or a new date is revealed by zooming out):
            //   - a date we've already shown before keeps whatever on/off
            //     state the user left it in (respects manual deselection)
            //   - a date we've never seen before this session defaults on,
            //     same as the first-load default above
            // Previously this always reset to null here, which silently
            // dropped the user's picks on every pan — invisible back when
            // dots/turf-swath didn't fully depend on selection, but MESH
            // rendering is 100% selection-driven now, so it read as storms
            // "randomly" deselecting themselves.
            var next = new Set();
            dates.forEach(function (d) {
                if (seKnownDates.has(d)) {
                    if (seSelectedDates.has(d)) next.add(d);
                } else {
                    next.add(d);
                    seKnownDates.add(d);
                }
            });
            seSelectedDates = next;
        }

        list.innerHTML = dates.map(function (date) {
            return seDateCardHtml(date, byDate[date]);
        }).join('');
    }

    function seDateCardHtml(date, clusters) {
        var maxHail    = clusters.reduce(function (m, s) { return Math.max(m, s.maxHailInches || 0); }, 0);
        var maxScore   = clusters.reduce(function (m, s) { return Math.max(m, s.score || 0); }, 0);
        var maxWind    = clusters.reduce(function (m, s) { return Math.max(m, s.maxWindMph || 0); }, 0);
        var selected   = !seSelectedDates || seSelectedDates.has(date);

        var color      = swathColor(maxHail || 0.75);
        var hailStr    = maxHail ? maxHail.toFixed(2) + '"' : '—';
        var dayStr     = seFormatDate(date);
        var scoreWidth = Math.min(Math.round(maxScore), 100);

        var tier, tierBg, tierBadge;
        if (maxScore >= 70) {
            tier = 'HOT';   tierBg = 'border-red-500/30';    tierBadge = 'bg-red-500/20 text-red-300';
        } else if (maxScore >= 40) {
            tier = 'ACTIVE'; tierBg = 'border-orange-500/30'; tierBadge = 'bg-orange-500/20 text-orange-300';
        } else {
            tier = 'MINOR';  tierBg = 'border-slate-600/40';  tierBadge = 'bg-slate-600/30 text-slate-400';
        }

        var opacity = selected ? '' : 'opacity-40';

        return '<div class="se-date-card rounded-xl border px-4 py-3.5 mb-2 cursor-pointer ' +
               'transition-all hover:brightness-110 active:scale-[0.99] ' +
               (selected ? 'bg-slate-800/60 ' : 'bg-slate-900/40 ') + tierBg + ' ' + opacity + '" ' +
               'onclick="seToggleDate(\'' + date + '\')" ' +
               'id="se-date-' + date + '">' +

               '<div class="flex items-start justify-between gap-2">' +
               '<div class="min-w-0 flex-1">' +
               '<div class="flex items-center gap-2 mb-1.5">' +
               '<span class="text-xs font-bold uppercase tracking-wider px-2 py-0.5 rounded-full ' + tierBadge + '">' + tier + '</span>' +
               '<span class="text-sm font-semibold text-white">' + dayStr + '</span>' +
               '</div>' +
               '<div class="flex items-baseline gap-3 flex-wrap">' +
               '<span class="text-2xl font-extrabold" style="color:' + color + ';line-height:1">' + hailStr + '</span>' +
               '<span class="text-xs text-slate-500">max hail</span>' +
               (maxWind > 0 ? '<span class="text-xs text-blue-400"><i class="fa-solid fa-wind mr-0.5"></i>' + maxWind.toFixed(0) + ' mph</span>' : '') +
               '</div>' +
               '</div>' +
               '<div class="flex flex-col items-end gap-1.5 flex-shrink-0">' +
               '<div class="w-5 h-5 rounded-full border-2 flex items-center justify-center transition-all ' +
               (selected ? 'border-brand bg-brand/20' : 'border-slate-600 bg-transparent') + '">' +
               (selected ? '<i class="fa-solid fa-check text-brand" style="font-size:9px"></i>' : '') +
               '</div>' +
               '<span class="text-xs text-slate-600">' + clusters.length + ' cluster' + (clusters.length !== 1 ? 's' : '') + '</span>' +
               '</div>' +
               '</div>' +

               '<div class="mt-2.5 flex items-center gap-3 text-xs text-slate-600">' +
               '<div class="flex-1 h-1.5 rounded-full bg-slate-800 overflow-hidden">' +
               '<div class="h-full rounded-full bg-gradient-to-r from-orange-500 to-red-500 transition-all" ' +
               'style="width:' + scoreWidth + '%"></div>' +
               '</div>' +
               '</div>' +

               '</div>';
    }

    /** Toggle a date's swath on/off from the left panel. */
    window.seToggleDate = function (date) {
        if (!seSelectedDates) return;
        if (seSelectedDates.has(date)) {
            seSelectedDates.delete(date);
        } else {
            seSelectedDates.add(date);
        }
        // Update card appearance without full re-render
        var storms = seStorms;
        var byDate = {};
        storms.forEach(function (s) {
            var key = s.date || 'unknown';
            if (!byDate[key]) byDate[key] = [];
            byDate[key].push(s);
        });
        var card = document.getElementById('se-date-' + date);
        if (card) {
            var newHtml = seDateCardHtml(date, byDate[date] || []);
            var tmp = document.createElement('div');
            tmp.innerHTML = newHtml;
            card.replaceWith(tmp.firstElementChild);
        }
        syncMeshLayers();
    };

    // ── Radar MESH rendering (Phase 2 — the primary storm visualization) ──
    //
    // Replaces the old always-on per-cluster dot markers + client-side
    // turf.js hull/buffer approximation. One Leaflet layer per selected date;
    // syncMeshLayers() reconciles that against seSelectedDates (driven by the
    // date cards in the left panel) every time the selection or the result
    // set changes. No fallback rendering: FeatureFlags:MeshSwaths off, or a
    // date with no MESH data, just means no polygon for that date — see
    // docs/mesh-phase2-handoff.md for the verification checklist before
    // relying on this in production.

    /**
     * Keep meshLayers in sync with seSelectedDates: remove layers for dates
     * no longer selected, fetch+add layers for newly-selected dates. Called
     * after every StormEvents load and every date-card toggle.
     */
    function syncMeshLayers() {
        if (!seMap) return;

        var wanted = seSelectedDates ? Array.from(seSelectedDates) : [];

        Object.keys(meshLayers).forEach(function (date) {
            if (wanted.indexOf(date) === -1) {
                seMap.removeLayer(meshLayers[date]);
                delete meshLayers[date];
            }
        });

        // Drop queued/no-longer-wanted dates (e.g. user deselected before
        // their turn came up).
        meshFetchQueue = meshFetchQueue.filter(function (d) { return wanted.indexOf(d) !== -1; });

        wanted.forEach(function (date) {
            if (meshLayers[date] || meshFetchInFlight.has(date) || meshFetchQueue.indexOf(date) !== -1) return;
            meshFetchQueue.push(date);
        });

        pumpMeshFetchQueue();
    }

    /** Start fetches off the queue up to MESH_FETCH_CONCURRENCY in flight. */
    function pumpMeshFetchQueue() {
        while (meshFetchInFlight.size < MESH_FETCH_CONCURRENCY && meshFetchQueue.length > 0) {
            var date = meshFetchQueue.shift();
            meshFetchInFlight.add(date);
            loadMeshForDate(date);
        }
    }

    /** Fetch + render the MESH swath for one date over the current viewport. */
    function loadMeshForDate(date) {
        if (!seMap) { meshFetchInFlight.delete(date); pumpMeshFetchQueue(); return; }
        var bounds = seMap.getBounds();

        fetch('/RoofHealth/MeshSwath?' + new URLSearchParams({
            minLat: bounds.getSouth().toFixed(4),
            maxLat: bounds.getNorth().toFixed(4),
            minLng: bounds.getWest().toFixed(4),
            maxLng: bounds.getEast().toFixed(4),
            date:   date
        }))
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (geojson) {
            // Bail if the map is gone or this date was deselected while the
            // fetch was in flight (avoids a stale layer popping back in).
            if (!geojson || !seMap || !seSelectedDates || !seSelectedDates.has(date)) return;
            if (meshLayers[date]) { seMap.removeLayer(meshLayers[date]); delete meshLayers[date]; }

            var layer = L.geoJSON(geojson, {
                style: function (feature) {
                    var band  = (feature.properties && feature.properties.sizeBand) || 0.75;
                    var color = swathColor(band);
                    return {
                        color:       color,
                        weight:      1,
                        opacity:     0.65,
                        fillColor:   color,
                        fillOpacity: band >= 2.0 ? 0.34 : 0.22
                    };
                },
                onEachFeature: function (feature, l) {
                    var p    = feature.properties || {};
                    var band = p.sizeBand || 0;
                    var color = swathColor(band);
                    var coinLabel = band >= 3.0  ? 'Grapefruit+'
                                 : band >= 2.5  ? 'Baseball'
                                 : band >= 2.0  ? 'Golf Ball'
                                 : band >= 1.75 ? 'Ping Pong'
                                 : band >= 1.5  ? 'Walnut'
                                 : band >= 1.25 ? 'Half Dollar'
                                 : band >= 1.0  ? 'Quarter'
                                 : 'Penny';

                    var panelHtml =
                        '<div style="font-size:13px;min-width:200px">' +
                        '<div style="font-size:15px;font-weight:800;color:#fff;margin-bottom:10px;border-bottom:1px solid rgba(100,116,139,0.25);padding-bottom:8px">' + seFormatDate(p.date || date) + '</div>' +
                        '<div style="display:flex;align-items:center;gap:10px">' +
                        '<div style="width:36px;height:36px;border-radius:50%;background:' + color + '22;border:1.5px solid ' + color + '55;display:flex;align-items:center;justify-content:center;flex-shrink:0">' +
                        '<i class="fa-solid fa-satellite-dish" style="color:' + color + ';font-size:14px"></i>' +
                        '</div>' +
                        '<div>' +
                        '<div style="font-size:15px;font-weight:800;color:#fff;line-height:1.2">' + band.toFixed(2) + '"+ (' + coinLabel + ')</div>' +
                        '<div style="color:#94a3b8;font-size:11px;margin-top:3px">Radar-derived (MRMS MESH)</div>' +
                        '</div>' +
                        '</div>' +
                        '</div>';

                    l.on('mouseover', function () {
                        if (!seHoverControl) return;
                        var el = seHoverControl.getContainer();
                        el.innerHTML = panelHtml;
                        el.style.display = 'block';
                    });
                    l.on('mouseout', function () {
                        if (seHoverControl) seHoverControl.getContainer().style.display = 'none';
                    });
                }
            }).addTo(seMap);

            meshLayers[date] = layer;

            var count = geojson.features ? geojson.features.length : 0;
            console.info('[Radar MESH] ' + count + ' polygon(s) for ' + date + '.');
            if (count === 0) {
                console.info('[Radar MESH] No swath for ' + date + ' — either no hail that date/area, ' +
                    'FeatureFlags:MeshSwaths is off, or the pipeline needs verifying. Check ' +
                    '/RoofHealth/MeshDebug?date=' + date + ' (see docs/mesh-phase2-handoff.md).');
            }
        })
        .catch(function (err) { console.warn('[Radar MESH]', err); })
        .finally(function () {
            meshFetchInFlight.delete(date);
            pumpMeshFetchQueue();
        });
    }

    // ── UI helpers ────────────────────────────────────────────────────────
    function setSeLoading(on) {
        var badge = document.getElementById('seLoadingBadge');
        if (!badge) return;
        if (on) badge.classList.remove('hidden');
        else    badge.classList.add('hidden');
    }

    function renderSeCount(n) {
        var el = document.getElementById('seCount');
        if (el) el.textContent = n;
    }

    function seFormatDate(ds) {
        if (!ds) return '—';
        try {
            var d = new Date(ds + 'T12:00:00Z');
            return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
        } catch (e) { return ds; }
    }

    // Haversine distance in miles (same formula as the backend)
    function seHaversine(lat1, lng1, lat2, lng2) {
        var R    = 3958.8;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLng = (lng2 - lng1) * Math.PI / 180;
        var a    = Math.sin(dLat / 2) * Math.sin(dLat / 2)
                 + Math.cos(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180)
                 * Math.sin(dLng / 2) * Math.sin(dLng / 2);
        return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
    }

})();
