import { create } from "zustand";
import type { Forecast, ForecastRange, Station, StationMap, StationPick } from "../types/forecast";

interface ForecastState {
  range: ForecastRange | null;
  today: string;                       // the page's one date (YYYY-MM-DD): "today is" the last observed day; the forecast is for the day after
  map: StationMap | null;              // every station active in the 28 days up to today, for the map
  mapLoading: boolean;
  query: string;                       // what the user typed in the station box
  stations: Station[];                 // the matches for the query
  stationsLoading: boolean;
  station: StationPick | null;         // the picked station, from the list or the map
  forecast: Forecast | null;
  loading: boolean;
  startedAt: number | null;            // when the running request started, for the "waking up" hint
  error: string | null;
  setRange: (r: ForecastRange | null) => void;
  setToday: (d: string) => void;
  setMap: (m: StationMap | null) => void;
  setMapLoading: (v: boolean) => void;
  setQuery: (q: string) => void;
  setStations: (s: Station[]) => void;
  setStationsLoading: (v: boolean) => void;
  setStation: (s: StationPick | null) => void;
  setForecast: (f: Forecast | null) => void;
  setLoading: (v: boolean) => void;
  setStartedAt: (t: number | null) => void;
  setError: (e: string | null) => void;
}

// State and plain setters only; every rule lives in facade/forecastFacade.ts.
export const useForecastStore = create<ForecastState>()((set) => ({
  range: null,
  today: "",
  map: null,
  mapLoading: false,
  query: "",
  stations: [],
  stationsLoading: false,
  station: null,
  forecast: null,
  loading: false,
  startedAt: null,
  error: null,
  setRange: (range) => set({ range }),
  setToday: (today) => set({ today }),
  setMap: (map) => set({ map }),
  setMapLoading: (mapLoading) => set({ mapLoading }),
  setQuery: (query) => set({ query }),
  setStations: (stations) => set({ stations }),
  setStationsLoading: (stationsLoading) => set({ stationsLoading }),
  setStation: (station) => set({ station }),
  setForecast: (forecast) => set({ forecast }),
  setLoading: (loading) => set({ loading }),
  setStartedAt: (startedAt) => set({ startedAt }),
  setError: (error) => set({ error }),
}));
