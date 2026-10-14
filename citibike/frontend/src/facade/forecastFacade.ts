import { forecastService } from "../services/forecastService";
import { useForecastStore } from "../stores/forecastStore";
import { ISO_DATE_RE, nextDay } from "../lib/dates";
import type { ForecastRange, StationPick } from "../types/forecast";

const SEARCH_DEBOUNCE_MS = 250;
const MAP_DEBOUNCE_MS = 300;

const message = (e: unknown) => (e instanceof Error ? e.message : String(e));

let searchTimer: ReturnType<typeof setTimeout> | null = null;
let searchSeq = 0;
let mapTimer: ReturnType<typeof setTimeout> | null = null;
let mapSeq = 0;

/**
 * "Today" is valid when it is a day of the data: the window behind the map and the features of tomorrow both need
 * the trips up to it. The last day of the data makes tomorrow the one real forecast; any earlier day has an actual.
 */
const todayProblem = (today: string, range: ForecastRange | null): string | null => {
  if (!ISO_DATE_RE.test(today)) return 'Pick the day that is "today"';
  if (range && (today < range.firstDay || today > range.lastDay)) {
    return `Today must be a day of the data, ${range.firstDay} to ${range.lastDay}; the forecast is for the day after it`;
  }
  return null;
};

// All frontend business logic for the forecast feature. Components call these; state comes back via the store.
export const forecastFacade = {
  /** The data's range, "today" defaulting to its last day, the busiest stations and the map. */
  init: async () => {
    const s = useForecastStore.getState();
    if (s.range) return;
    try {
      const range = await forecastService.getRange();
      s.setRange(range);
      if (!s.today) s.setToday(range.lastDay);
      await Promise.all([
        s.stations.length === 0 ? forecastFacade.search("") : Promise.resolve(),
        forecastFacade.loadMap(),
      ]);
    } catch (e) {
      s.setError(message(e));
    }
  },

  /** Station search, debounced; a late answer to an earlier query is dropped. */
  search: (query: string) => {
    const s = useForecastStore.getState();
    s.setQuery(query);
    if (searchTimer) clearTimeout(searchTimer);
    const seq = ++searchSeq;
    return new Promise<void>((resolve) => {
      searchTimer = setTimeout(async () => {
        s.setStationsLoading(true);
        try {
          const stations = await forecastService.listStations(query.trim());
          if (seq === searchSeq) s.setStations(stations);
        } catch (e) {
          if (seq === searchSeq) s.setError(message(e));
        } finally {
          if (seq === searchSeq) s.setStationsLoading(false);
          resolve();
        }
      }, query === "" ? 0 : SEARCH_DEBOUNCE_MS);
    });
  },

  /** The map of the chosen "today", debounced for a date that is typed; a late answer to an earlier day is dropped. */
  loadMap: () => {
    if (mapTimer) clearTimeout(mapTimer);
    const seq = ++mapSeq;
    return new Promise<void>((resolve) => {
      mapTimer = setTimeout(async () => {
        const s = useForecastStore.getState();
        if (todayProblem(s.today, s.range)) {
          resolve();
          return;
        }
        s.setMapLoading(true);
        try {
          const map = await forecastService.getMap(s.today);
          if (seq === mapSeq) s.setMap(map);
        } catch (e) {
          if (seq === mapSeq) s.setError(message(e));
        } finally {
          if (seq === mapSeq) s.setMapLoading(false);
          resolve();
        }
      }, MAP_DEBOUNCE_MS);
    });
  },

  pick: (station: StationPick) => {
    const s = useForecastStore.getState();
    s.setStation(station);
    s.setForecast(null);
  },

  /** The page's one date. Changing it drops the forecast shown (it was for another day) and refreshes the map. */
  setToday: (today: string) => {
    const s = useForecastStore.getState();
    s.setToday(today);
    s.setForecast(null);
    void forecastFacade.loadMap();
  },

  /** The last day of the data: tomorrow is then the one real forecast, the day after the data. */
  pickLatest: () => {
    const s = useForecastStore.getState();
    if (s.range) forecastFacade.setToday(s.range.lastDay);
  },

  /** From the map's popup: pick the station and forecast tomorrow in one go. */
  forecastStation: async (station: StationPick) => {
    forecastFacade.pick(station);
    await forecastFacade.forecast();
  },

  /** Tomorrow's forecast of the picked station. Validates before a call is made; the backend validates again. */
  forecast: async () => {
    const s = useForecastStore.getState();
    if (s.loading) return;
    s.setError(null);
    if (!s.station) {
      s.setError("Pick a station first: click one on the map or in the list");
      return;
    }
    const problem = todayProblem(s.today, s.range);
    if (problem) {
      s.setError(problem);
      return;
    }
    s.setLoading(true);
    s.setStartedAt(Date.now());
    try {
      s.setForecast(await forecastService.getForecast(s.station.stationKey, nextDay(s.today)));
    } catch (e) {
      s.setError(message(e));
    } finally {
      s.setLoading(false);
      s.setStartedAt(null);
    }
  },
};
