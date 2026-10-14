import { api } from "./api";
import type { Forecast, ForecastRange, Station, StationMap } from "../types/forecast";

// Connection layer only: one call per endpoint, no conditions, no orchestration.
export const forecastService = {
  listStations: (q: string, limit = 12) =>
    api.get<Station[]>("/forecast/stations", { params: { q, limit } }).then((r) => r.data),

  getRange: () => api.get<ForecastRange>("/forecast/range").then((r) => r.data),

  /** Every station active in the 28 days up to "today", with its mean departures: the map. */
  getMap: (today: string) => api.get<StationMap>("/forecast/map", { params: { today } }).then((r) => r.data),

  // With both serverless endpoints cold the first call takes up to about 40 s (36 s measured): a longer timeout than the rest of the app.
  getForecast: (station: string, day: string) =>
    api.get<Forecast>("/forecast", { params: { station, day }, timeout: 90_000 }).then((r) => r.data),
};
