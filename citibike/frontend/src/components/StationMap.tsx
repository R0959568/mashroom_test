import { useEffect, useMemo, useRef } from "react";
import L from "leaflet";
import "leaflet/dist/leaflet.css";
import { forecastFacade } from "../facade/forecastFacade";
import { useForecastStore } from "../stores/forecastStore";
import { fmt } from "../lib/format";
import type { MapStation, StationMap as StationMapData } from "../types/forecast";
import styles from "./StationMap.module.css";

const NYC_CENTER: L.LatLngTuple = [40.735, -73.98];
const MIN_ZOOM = 11; // the whole network in one view; the dots are smallest here and grow when zooming in
const MAX_ZOOM = 18;
const START_ZOOM = MIN_ZOOM;
const MIN_HISTORY_DAYS = 28; // the cold-start rule of 05, the number the backend enforces too
const PX_PER_MM = 96 / 25.4; // the CSS reference pixel: 1 mm = 3,78 px
const DIAMETER_AT_MIN_ZOOM_PX = (4 / 3) * PX_PER_MM; // 1,33 mm: a third of the first version, which filled the map

interface Colors {
  scale: string[]; // few -> many departures: yellow, orange, red, bordeaux
  cold: string;
  outline: string;
  selected: string;
}

/** Colours come from the theme tokens, read once at mount: styles/theme.css stays the only place with raw values. */
const token = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

/** Every station has the same size at a zoom level: 1,33 mm when zoomed out to the whole network, larger when zooming in. */
const radiusAt = (zoom: number) => (DIAMETER_AT_MIN_ZOOM_PX / 2) * Math.pow(2, (zoom - MIN_ZOOM) * 0.5);

const isCold = (s: MapStation) => s.activeDays < MIN_HISTORY_DAYS;

/** "#rrggbb" -> [r, g, b] */
const rgb = (hex: string): [number, number, number] => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16)) as [number, number, number];

/** A colour on the scale for t in [0, 1]: linear between the stops, in RGB. */
function colorAt(scale: string[], t: number): string {
  const stops = scale.map(rgb);
  const pos = Math.min(1, Math.max(0, t)) * (stops.length - 1);
  const i = Math.min(stops.length - 2, Math.floor(pos));
  const f = pos - i;
  const mix = stops[i].map((a, k) => Math.round(a + (stops[i + 1][k] - a) * f));
  return `rgb(${mix[0]} ${mix[1]} ${mix[2]})`;
}

/**
 * The colour position of a station: the square root of its mean departures against the 95th percentile, so the
 * skewed distribution (median about 33 a day, a few stations above 500) spreads over the whole scale instead of
 * leaving everything but the giants yellow.
 */
function colorScale(stations: MapStation[]): { position: (mean: number) => number; top: number } {
  const sorted = stations.map((s) => s.meanDepartures).sort((a, b) => a - b);
  const top = sorted.length > 0 ? sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * 0.95))] : 1;
  const root = Math.sqrt(Math.max(top, 1));
  return { position: (mean) => Math.min(1, Math.sqrt(Math.max(mean, 0)) / root), top };
}

function markerStyle(s: MapStation, position: number, selected: boolean, colors: Colors): L.PathOptions {
  return {
    color: selected ? colors.selected : colors.outline,
    weight: selected ? 3 : 0.5, // a thin outline: at 5 px the dot is mostly its colour
    fillColor: isCold(s) ? colors.cold : colorAt(colors.scale, position),
    fillOpacity: isCold(s) ? 0.5 : 0.85,
  };
}

/** The popup of a station: built as DOM (textContent, never HTML from data) with the forecast button wired to the facade. */
function popupContent(s: MapStation, data: StationMapData): HTMLElement {
  const el = document.createElement("div");
  el.className = styles.popup;

  const name = document.createElement("div");
  name.className = styles.popupName;
  name.textContent = s.name;

  const mean = document.createElement("div");
  mean.className = styles.popupLine;
  mean.textContent = `${fmt.int(Math.round(s.meanDepartures))} departures per day, mean of the ${data.windowDays} days up to ${fmt.date(data.today)}`;

  const days = document.createElement("div");
  days.className = styles.popupLine;
  days.textContent = isCold(s)
    ? `Only ${s.activeDays} day${s.activeDays === 1 ? "" : "s"} with trips: too new for a forecast (the model needs ${MIN_HISTORY_DAYS})`
    : `${s.activeDays} of ${data.windowDays} days with trips · station ${s.stationKey}`;

  const button = document.createElement("button");
  button.type = "button";
  button.className = styles.popupButton;
  button.textContent = isCold(s) ? "No forecast yet" : `Forecast tomorrow, ${fmt.date(data.tomorrow)}`;
  button.disabled = isCold(s);
  button.onclick = () => void forecastFacade.forecastStation(s);

  el.append(name, mean, days, button);
  return el;
}

/** One line: the colour scale and the grey of the young stations; the details live in each station's popup. */
function Legend({ data }: { data: StationMapData | null }) {
  const top = useMemo(() => (data ? colorScale(data.stations).top : 0), [data]);
  if (!data) return null;
  const cold = data.stations.filter(isCold).length;
  return (
    <div className={styles.legend}>
      <div className={styles.scaleRow}>
        <span>few</span>
        <span className={styles.scaleBar} aria-hidden="true" />
        <span>many: {fmt.int(Math.round(top))}+ departures a day</span>
        {cold > 0 && (
          <span className={styles.scaleCold}>
            <span className={styles.dotCold} />
            too new for a forecast (fewer than {MIN_HISTORY_DAYS} days)
          </span>
        )}
      </div>
    </div>
  );
}

/**
 * All stations on an OpenStreetMap background (streets and points of interest), one canvas circle per station,
 * coloured by its mean departures (yellow = few, bordeaux = the busiest), all the same size at a zoom level.
 * Plain Leaflet inside one effect per concern: the map once, the circles per day, the ring per picked station.
 * A single shared popup follows the hovered station and stays until the next hover or a click on the map, so the
 * mouse can reach its button.
 */
export function StationMap() {
  const data = useForecastStore((s) => s.map);
  const station = useForecastStore((s) => s.station);
  const mapLoading = useForecastStore((s) => s.mapLoading);
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<L.Map | null>(null);
  const layerRef = useRef<L.LayerGroup | null>(null);
  const popupRef = useRef<L.Popup | null>(null);
  const markersRef = useRef<Map<string, { marker: L.CircleMarker; station: MapStation; position: number }>>(new Map());
  const colorsRef = useRef<Colors | null>(null);
  const selectedRef = useRef<string | null>(null);

  // The map itself, once. OpenStreetMap's standard tiles: attribution required, light use only (a course demo).
  useEffect(() => {
    if (!containerRef.current) return;
    const map = L.map(containerRef.current, {
      preferCanvas: true,
      center: NYC_CENTER,
      zoom: START_ZOOM,
      minZoom: MIN_ZOOM,
      maxZoom: MAX_ZOOM,
      zoomSnap: 0.5,
    });
    L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
      maxZoom: 19,
      attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    }).addTo(map);
    const layer = L.layerGroup().addTo(map);
    const popup = L.popup({ autoPan: false, closeButton: true, offset: [0, -6] });
    map.on("click", () => map.closePopup());
    map.on("zoomend", () => {
      const radius = radiusAt(map.getZoom());
      for (const { marker } of markersRef.current.values()) marker.setRadius(radius);
    });
    mapRef.current = map;
    layerRef.current = layer;
    popupRef.current = popup;
    colorsRef.current = {
      scale: [token("--map-scale-1"), token("--map-scale-2"), token("--map-scale-3"), token("--map-scale-4")],
      cold: token("--map-cold"),
      outline: token("--map-outline"),
      selected: token("--map-selected"),
    };
    return () => {
      map.remove();
      mapRef.current = null;
      layerRef.current = null;
      popupRef.current = null;
      markersRef.current.clear();
    };
  }, []);

  // The stations of the chosen day: rebuilt per day (2,3 k canvas circles, well under 100 ms).
  useEffect(() => {
    const map = mapRef.current;
    const layer = layerRef.current;
    const popup = popupRef.current;
    const colors = colorsRef.current;
    if (!map || !layer || !popup || !colors || !data) return;
    map.closePopup();
    layer.clearLayers();
    markersRef.current.clear();
    const { position } = colorScale(data.stations);
    const radius = radiusAt(map.getZoom());
    const selectedKey = useForecastStore.getState().station?.stationKey ?? null;
    selectedRef.current = selectedKey;
    for (const s of data.stations) {
      const t = position(s.meanDepartures);
      const marker = L.circleMarker([s.lat, s.lng], { radius, ...markerStyle(s, t, s.stationKey === selectedKey, colors) });
      marker.on("mouseover", () => {
        popup.setLatLng([s.lat, s.lng]).setContent(popupContent(s, data));
        map.openPopup(popup);
      });
      marker.on("click", (e) => {
        L.DomEvent.stopPropagation(e);
        forecastFacade.pick(s);
      });
      marker.addTo(layer);
      markersRef.current.set(s.stationKey, { marker, station: s, position: t });
    }
  }, [data]);

  // The picked station gets the ring, is drawn on top and is brought into view; the previous one loses the ring.
  useEffect(() => {
    const map = mapRef.current;
    const colors = colorsRef.current;
    if (!map || !colors) return;
    const key = station?.stationKey ?? null;
    const previous = selectedRef.current ? markersRef.current.get(selectedRef.current) : undefined;
    if (previous && selectedRef.current !== key) previous.marker.setStyle(markerStyle(previous.station, previous.position, false, colors));
    const picked = key ? markersRef.current.get(key) : undefined;
    if (picked) {
      picked.marker.setStyle(markerStyle(picked.station, picked.position, true, colors)).bringToFront();
      const at = picked.marker.getLatLng();
      if (!map.getBounds().contains(at)) map.panTo(at);
    }
    selectedRef.current = key;
  }, [station, data]);

  return (
    <div className={styles.wrap}>
      <div ref={containerRef} className={styles.map} role="region" aria-label="Map of the stations" />
      {mapLoading && <div className={styles.badge}>Loading the stations…</div>}
      <Legend data={data} />
    </div>
  );
}
