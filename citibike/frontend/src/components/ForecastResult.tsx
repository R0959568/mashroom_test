import { useEffect, useRef } from "react";
import { useForecastStore } from "../stores/forecastStore";
import { useWakeHint } from "../hooks/useWakeHint";
import { fmt } from "../lib/format";
import { addDays, ISO_DATE_RE, nextDay } from "../lib/dates";
import type { ForecastInputs, HorizonForecast } from "../types/forecast";
import { Card } from "./Card";
import { Empty, Num, Table } from "./Table";
import styles from "./ForecastResult.module.css";

const DOW = ["", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
const SKELETON_ROWS = 6;

function Horizon({ title, sub, h }: { title: string; sub: string; h: HorizonForecast }) {
  const error = h.actual !== null ? Math.round(h.prediction) - h.actual : null;
  return (
    <div className={styles.horizon}>
      <div className={styles.horizonTitle}>{title}</div>
      <div className={styles.horizonSub}>{sub}</div>
      <div className={styles.big}>{fmt.int(Math.round(h.prediction))}</div>
      <div className={styles.label}>departures expected</div>
      {h.actual !== null ? (
        <div className={styles.actual}>
          actual <strong>{fmt.int(h.actual)}</strong>
          {error !== null && (
            <span className={styles.muted}>
              {" "}
              ({error > 0 ? "+" : ""}
              {fmt.int(error)})
            </span>
          )}
        </div>
      ) : (
        <div className={styles.actual}>no actual yet: this is the forecast</div>
      )}
      <div className={styles.muted}>
        {h.model.family} · typical error on 2026: ±{h.model.testMae !== null ? fmt.int(Math.round(h.model.testMae)) : "?"} per station-day
      </div>
    </div>
  );
}

function InputsTable({ inputs }: { inputs: ForecastInputs }) {
  const rows: [string, string][] = [
    ["Yesterday", fmt.int(inputs.depLag1)],
    ["Same weekday last week", fmt.int(inputs.depLag7)],
    ["Same weekday, mean of 4 weeks", inputs.depMean4Weeks === null ? "—" : inputs.depMean4Weeks.toFixed(1)],
    ["Mean of the last 7 days", inputs.depMean7 === null ? "—" : inputs.depMean7.toFixed(1)],
    ["Arrivals yesterday / mean of 7 days", `${fmt.int(inputs.arrLag1)} / ${inputs.arrMean7 === null ? "—" : inputs.arrMean7.toFixed(1)}`],
    ["Member share / e-bike share (28 days)", `${pct(inputs.memberShare28)} / ${pct(inputs.ebikeShare28)}`],
    ["Month to date, per day", inputs.depMtd === null ? "— (first day of the month)" : inputs.depMtd.toFixed(1)],
    ["Rest of this month a year ago", inputs.hasLastYear ? fmt.int(inputs.depRestOfMonthLy) : "— (no usable year earlier)"],
    ["Days of history at this station", fmt.int(inputs.historyDays)],
    ["Calendar", `${DOW[inputs.isoDow]}${inputs.holiday ? ", holiday" : ""}${inputs.dayOff ? " (weekday off)" : ""}${inputs.holidayLag7 ? ", a week after a holiday" : ""}`],
    [
      `Weather${inputs.weatherDay ? ` (${fmt.date(inputs.weatherDay)})` : ""}`,
      `${inputs.tminC.toFixed(1)} to ${inputs.tmaxC.toFixed(1)} °C, ${inputs.prcpMm.toFixed(1)} mm rain, ${inputs.snowMm.toFixed(1)} mm snow, wind ${inputs.windMs.toFixed(1)} m/s`,
    ],
  ];
  return (
    <Table>
      <thead>
        <tr>
          <th>What the model saw</th>
          <th className={styles.numHead}>Value</th>
        </tr>
      </thead>
      <tbody>
        {rows.length === 0 && <Empty colSpan={2}>—</Empty>}
        {rows.map(([k, v]) => (
          <tr key={k}>
            <td>{k}</td>
            <Num>{v}</Num>
          </tr>
        ))}
      </tbody>
    </Table>
  );
}

const pct = (v: number | null) => (v === null ? "—" : `${Math.round(v * 100)} %`);

/** The result card's shape while the forecast loads: the same two horizons and the table, as shimmering blocks. */
function ForecastSkeleton() {
  const station = useForecastStore((s) => s.station);
  const today = useForecastStore((s) => s.today);
  const startedAt = useForecastStore((s) => s.startedAt);
  const slow = useWakeHint(startedAt);
  const tomorrow = ISO_DATE_RE.test(today) ? nextDay(today) : null;
  const title = station && tomorrow ? `${station.name}: tomorrow, ${fmt.date(tomorrow)}` : "Forecast";
  return (
    <div role="status" aria-live="polite" aria-busy="true">
      <Card title={title}>
        <p className={styles.note}>
          {slow
            ? "Waking the model up: the first call after a quiet period starts two serverless endpoints, up to about 40 s."
            : "Forecasting…"}
        </p>
        <div className={styles.horizons}>
          {["Tomorrow", "Rest of the month"].map((h) => (
            <div key={h} className={styles.horizon}>
              <div className={styles.horizonTitle}>{h}</div>
              <div className={`${styles.bone} ${styles.boneSub}`} />
              <div className={`${styles.bone} ${styles.boneBig}`} />
              <div className={styles.label}>departures expected</div>
              <div className={`${styles.bone} ${styles.boneLine}`} />
              <div className={`${styles.bone} ${styles.boneLineShort}`} />
            </div>
          ))}
        </div>
        <Table>
          <thead>
            <tr>
              <th>What the model saw</th>
              <th className={styles.numHead}>Value</th>
            </tr>
          </thead>
          <tbody>
            {Array.from({ length: SKELETON_ROWS }, (_, i) => (
              <tr key={i}>
                <td>
                  <div className={`${styles.bone} ${i % 2 ? styles.boneCellShort : styles.boneCell}`} />
                </td>
                <Num>
                  <div className={`${styles.bone} ${styles.boneNum}`} />
                </Num>
              </tr>
            ))}
          </tbody>
        </Table>
      </Card>
    </div>
  );
}

export function ForecastResult() {
  const forecast = useForecastStore((s) => s.forecast);
  const loading = useForecastStore((s) => s.loading);
  const ref = useRef<HTMLDivElement>(null);

  // The result (and the skeleton while it loads) lands below the map: bring it into view.
  useEffect(() => {
    if (loading || forecast) ref.current?.scrollIntoView({ behavior: "smooth", block: "start" });
  }, [loading, forecast]);

  if (loading) {
    return (
      <div ref={ref}>
        <ForecastSkeleton />
      </div>
    );
  }
  if (!forecast) return null;
  const days = forecast.restOfMonth.daysInHorizon;
  const restEnd = addDays(forecast.day, days - 1);
  const sourceNote =
    forecast.inputs.source === "tomorrow"
      ? "the day after the data: computed from the trips up to the last day and the last observed weather"
      : "a day inside the data: the row of the feature table, the actuals next to the forecast";
  return (
    <div ref={ref}>
      <Card title={`${forecast.station.name}: tomorrow, ${fmt.date(forecast.day)}`}>
        <p className={styles.note}>{sourceNote}.</p>
        <div className={styles.horizons}>
          <Horizon title="Tomorrow" sub={`departures on ${fmt.date(forecast.day)}`} h={forecast.dayAhead} />
          <Horizon
            title="Rest of the month"
            sub={`departures from ${fmt.date(forecast.day)} to ${fmt.date(restEnd)} (${days} day${days === 1 ? "" : "s"})`}
            h={forecast.restOfMonth}
          />
        </div>
        <InputsTable inputs={forecast.inputs} />
      </Card>
    </div>
  );
}
