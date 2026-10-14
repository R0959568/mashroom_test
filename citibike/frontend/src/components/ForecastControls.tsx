import { forecastFacade } from "../facade/forecastFacade";
import { useForecastStore } from "../stores/forecastStore";
import { useWakeHint } from "../hooks/useWakeHint";
import { fmt } from "../lib/format";
import { ISO_DATE_RE, nextDay } from "../lib/dates";
import { Button } from "./Button";
import { Card } from "./Card";
import { StationMap } from "./StationMap";
import styles from "./ForecastControls.module.css";

export function ForecastControls() {
  const range = useForecastStore((s) => s.range);
  const today = useForecastStore((s) => s.today);
  const query = useForecastStore((s) => s.query);
  const stations = useForecastStore((s) => s.stations);
  const stationsLoading = useForecastStore((s) => s.stationsLoading);
  const station = useForecastStore((s) => s.station);
  const loading = useForecastStore((s) => s.loading);
  const startedAt = useForecastStore((s) => s.startedAt);
  const slow = useWakeHint(startedAt); // the skeleton below the map shows the same hint

  const isLatest = range !== null && today === range.lastDay;
  const tomorrow = ISO_DATE_RE.test(today) ? nextDay(today) : null;

  return (
    <Card title="Station and day">
      {/* The page's one date: the data is not live, so "today" is chosen; everything below forecasts the day after it. */}
      <div className={styles.day}>
        <label className={styles.label}>
          <span>Today is</span>
          <input
            type="date"
            value={today}
            min={range?.firstDay}
            max={range?.lastDay}
            onChange={(e) => forecastFacade.setToday(e.target.value)}
          />
        </label>
        <Button onClick={() => forecastFacade.pickLatest()} disabled={!range || isLatest}>
          Latest day
        </Button>
      </div>

      <div className={styles.layout}>
        <div className={styles.mapCol}>
          <StationMap />
        </div>
        <div className={styles.listCol}>
          <label className={styles.label}>
            <span>Station</span>
            <input
              type="search"
              value={query}
              placeholder="name, e.g. Central Park"
              onChange={(e) => void forecastFacade.search(e.target.value)}
              autoComplete="off"
            />
          </label>
          <small className={styles.hint}>
            {station ? `Picked: ${station.name} (${station.stationKey})` : "Busiest stations first; type to search, or pick one on the map."}
          </small>
          <ul className={styles.stations} aria-label="Matching stations">
            {stationsLoading && stations.length === 0 && <li className={styles.muted}>Searching…</li>}
            {!stationsLoading && stations.length === 0 && <li className={styles.muted}>No station matches.</li>}
            {stations.map((s) => (
              <li key={s.stationKey}>
                <button
                  type="button"
                  className={`${styles.station} ${station?.stationKey === s.stationKey ? styles.stationActive : ""}`}
                  onClick={() => forecastFacade.pick(s)}
                >
                  <span>{s.name}</span>
                  <span className={styles.muted}>{fmt.int(s.tripsAsStart)} trips</span>
                </button>
              </li>
            ))}
          </ul>
          <div className={styles.buttons}>
            <Button variant="primary" onClick={() => void forecastFacade.forecast()} disabled={loading || !station || !range}>
              {loading ? (slow ? "Waking the model up…" : "Forecasting…") : `Forecast tomorrow${tomorrow ? `, ${fmt.date(tomorrow)}` : ""}`}
            </Button>
          </div>
        </div>
      </div>
    </Card>
  );
}
