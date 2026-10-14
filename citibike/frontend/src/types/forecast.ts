// Wire shapes of the forecast DTOs (camelCase), see backend Dtos/ForecastDtos.cs.

export interface Station {
  stationKey: string;
  name: string;
  lat: number | null;
  lng: number | null;
  tripsAsStart: number;
}

/** What the page needs of a picked station, whether it came from the search list or from the map. */
export interface StationPick {
  stationKey: string;
  name: string;
  lat: number | null;
  lng: number | null;
}

export interface ForecastRange {
  firstDay: string;        // first day of the data
  lastDay: string;         // last day of the data: every day up to here has an actual next to the forecast
  tomorrow: string;        // the day after the data: a real forecast, computed from the trips up to lastDay
  lastWeatherDay: string | null;
}

/** A station on the map with its activity in the window that ends on the chosen "today". */
export interface MapStation {
  stationKey: string;
  name: string;
  lat: number;
  lng: number;
  meanDepartures: number;  // per active day in the window
  activeDays: number;      // days with trips in the window; below 28 the station is too new for a forecast
}

export interface StationMap {
  today: string;           // the last observed day of the window
  tomorrow: string;        // the day the page forecasts
  windowDays: number;      // 28, the model's own window
  stations: MapStation[];  // busiest first
}

export interface ModelInfo {
  family: string;
  horizon: string;
  validationMae: number | null;
  testMae: number | null;
}

export interface HorizonForecast {
  prediction: number;
  actual: number | null;   // null for the day after the data
  daysInHorizon: number;
  model: ModelInfo;
}

export interface ForecastInputs {
  source: "history" | "tomorrow";
  historyDays: number;
  depLag1: number | null;
  depLag7: number | null;
  depLag14: number | null;
  depLag28: number | null;
  depMean7: number | null;
  depMean4Weeks: number | null;
  depMtd: number | null;
  memberShare28: number | null;
  ebikeShare28: number | null;
  arrLag1: number | null;
  arrMean7: number | null;
  depRestOfMonthLy: number | null;
  hasLastYear: boolean;
  isoDow: number;
  holiday: boolean;
  dayOff: boolean;
  holidayLag7: boolean;
  tmaxC: number;
  tminC: number;
  prcpMm: number;
  snowMm: number;
  snowDepthMm: number;
  windMs: number;
  weatherDay: string | null;
}

export interface Forecast {
  station: Station;
  day: string;
  inputs: ForecastInputs;
  dayAhead: HorizonForecast;
  restOfMonth: HorizonForecast;
}
