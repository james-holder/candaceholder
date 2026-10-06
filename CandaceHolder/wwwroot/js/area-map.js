// "Draw on Map" tab on Views/Home/Index.cshtml — click-drag a rectangle to
// list every address inside it (/Properties/Area). Results are handed to
// site.js's showResults(), so the list/select/save flow is identical to an
// address search. Depends on site.js (addBasemapWithToggle, showResults,
// showToast, haversineMi) and home-index-autocomplete.js (clientGeocode).

(function () {
    var areaMap      = null;
    var selectMode   = false;   // true while armed, from button click to a completed drag
    var drawing      = false;   // true only during the actual mousedown → mouseup drag
    var dragStart    = null;    // L.LatLng where the drag began
    var previewRect  = null;    // live L.Rectangle while dragging

    // Idempotent — builds the map once, the first time the tab is opened.
    window.initAreaMap = function (lat, lng) {
        // Start at the top: the page may be scrolled down from search results,
        // which would leave the toolbar hidden under the sticky header.
        window.scrollTo(0, 0);

        if (areaMap) {
            areaMap.invalidateSize();
            return;
        }
        areaMap = L.map('areaMap', { center: [lat, lng], zoom: 14, zoomControl: true });
        addBasemapWithToggle(areaMap);

        // On-map "Select Area" button, so it's always visible next to the map.
        var ctrl = L.control({ position: 'topleft' });
        ctrl.onAdd = function () {
            var div = L.DomUtil.create('div');
            div.innerHTML = '<button type="button" id="mapAreaSelectBtn" class="map-area-btn">' +
                            '<i class="fa-solid fa-draw-polygon"></i>&nbsp;Select Area</button>';
            L.DomEvent.disableClickPropagation(div);
            div.querySelector('button').addEventListener('click', window.toggleAreaSelect);
            return div;
        };
        ctrl.addTo(areaMap);

        areaMap.on('mousedown', onMouseDown);
        areaMap.on('mousemove', onMouseMove);
        areaMap.on('mouseup',   onMouseUp);
    };

    window.jumpMapTo = async function () {
        var q = document.getElementById('mapJumpInput').value.trim();
        if (!q || !areaMap) return;
        var geo = await clientGeocode(q);
        if (!geo) { showToast('Could not find that place.', false); return; }
        areaMap.setView([geo.lat, geo.lng], 16);
    };

    document.getElementById('mapJumpInput').addEventListener('keydown', function (e) {
        if (e.key === 'Enter') window.jumpMapTo();
    });

    // Arm/disarm area selection. While armed, map panning is disabled so a
    // drag draws a box instead of moving the map.
    window.toggleAreaSelect = function () {
        if (!areaMap) return;
        selectMode = !selectMode;

        var btn = document.getElementById('areaSelectBtn');
        btn.classList.toggle('border-brand',     selectMode);
        btn.classList.toggle('bg-brand/20',      selectMode);
        btn.classList.toggle('border-slate-600', !selectMode);
        btn.classList.toggle('bg-slate-900',     !selectMode);

        var mapBtn = document.getElementById('mapAreaSelectBtn');
        if (mapBtn) {
            mapBtn.classList.toggle('armed', selectMode);
            mapBtn.innerHTML = selectMode
                ? '<i class="fa-solid fa-hand-pointer"></i>&nbsp;Drag a box… (click to cancel)'
                : '<i class="fa-solid fa-draw-polygon"></i>&nbsp;Select Area';
        }

        areaMap.dragging[selectMode ? 'disable' : 'enable']();
        areaMap.getContainer().style.cursor = selectMode ? 'crosshair' : '';
    };

    function onMouseDown(e) {
        if (!selectMode) return;
        drawing   = true;
        dragStart = e.latlng;
        if (previewRect) { areaMap.removeLayer(previewRect); previewRect = null; }
    }

    function onMouseMove(e) {
        if (!drawing || !dragStart) return;
        var bounds = L.latLngBounds(dragStart, e.latlng);
        if (previewRect) previewRect.setBounds(bounds);
        else previewRect = L.rectangle(bounds, { color: '#f97316', weight: 2, fillOpacity: 0.08 }).addTo(areaMap);
    }

    function onMouseUp(e) {
        if (!drawing) return;
        drawing = false;
        var start = dragStart;
        dragStart = null;
        if (!start) return;

        var bounds = L.latLngBounds(start, e.latlng);
        var sw = bounds.getSouthWest(), ne = bounds.getNorthEast();

        // A click (no real drag) collapses to a ~zero-size box — ignore it and
        // stay armed so the user can try dragging instead.
        if (haversineMi(sw.lat, sw.lng, ne.lat, ne.lng) < 0.02) {
            if (previewRect) { areaMap.removeLayer(previewRect); previewRect = null; }
            return;
        }

        window.toggleAreaSelect();
        fetchArea(bounds);
    }

    function fetchArea(bounds) {
        showToast('Finding addresses in that area…', true);

        var qs = 'north=' + bounds.getNorth() + '&south=' + bounds.getSouth() +
                 '&east='  + bounds.getEast()  + '&west='  + bounds.getWest();
        fetch('/Properties/Area?' + qs)
            .then(function (resp) {
                if (!resp.ok) return resp.json().then(function (err) { throw new Error(err.error || 'Area search failed.'); });
                return resp.json();
            })
            .then(function (data) {
                if (!data.properties || !data.properties.length) {
                    showToast('No addresses found in that area.', false);
                    return;
                }
                showResults(data, 'Selected area');
            })
            .catch(function (err) {
                showToast(err.message || 'Could not search that area.', false);
            });
    }
})();
