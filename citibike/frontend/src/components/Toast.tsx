import { useForecastStore } from "../stores/forecastStore";
import { useIngestStore } from "../stores/ingestStore";
import styles from "./Toast.module.css";

// Pinned error toast; the facade writes the message, the user dismisses it.
export function Toast() {
  const ingestError = useIngestStore((s) => s.error);
  const setIngestError = useIngestStore((s) => s.setError);
  const forecastError = useForecastStore((s) => s.error);
  const setForecastError = useForecastStore((s) => s.setError);
  const error = forecastError ?? ingestError;
  const setError = forecastError ? setForecastError : setIngestError;
  if (!error) return null;
  return (
    <div className={styles.toast} role="alert">
      <span className={styles.message}>{error}</span>
      <button type="button" className={styles.close} onClick={() => setError(null)} aria-label="Dismiss">
        ×
      </button>
    </div>
  );
}
