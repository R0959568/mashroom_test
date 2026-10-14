import { ingestFacade } from "../facade/ingestFacade";
import { useIngestStore } from "../stores/ingestStore";
import { useSessionStore } from "../stores/sessionStore";
import { Button } from "./Button";
import { Card } from "./Card";
import styles from "./IngestControls.module.css";

export function IngestControls() {
  const sinceInput = useIngestStore((s) => s.sinceInput);
  const setSinceInput = useIngestStore((s) => s.setSinceInput);
  const previewLoading = useIngestStore((s) => s.previewLoading);
  const starting = useIngestStore((s) => s.starting);
  const watchingRunId = useIngestStore((s) => s.watchingRunId);
  const adminToken = useSessionStore((s) => s.adminToken);
  const setAdminToken = useSessionStore((s) => s.setAdminToken);
  const busy = starting || watchingRunId !== null;

  return (
    <Card title="New files in the bucket">
      <div className={styles.row}>
        <label className={styles.field}>
          <span>Files modified after</span>
          <input type="date" value={sinceInput} onChange={(e) => setSinceInput(e.target.value)} />
          <small>Leave empty to use the last successful run (what the monthly cron does).</small>
        </label>
        <label className={styles.field}>
          <span>Admin token</span>
          <input
            type="password"
            autoComplete="off"
            value={adminToken}
            onChange={(e) => setAdminToken(e.target.value)}
            placeholder="required to start a run"
          />
          <small>Kept in memory only; reads work without it.</small>
        </label>
        <div className={styles.buttons}>
          <Button onClick={() => void ingestFacade.preview()} disabled={previewLoading}>
            {previewLoading ? "Listing…" : "Preview files"}
          </Button>
          <Button variant="primary" onClick={() => void ingestFacade.startRun()} disabled={busy}>
            {busy ? "Run in progress…" : "Start ingest"}
          </Button>
        </div>
      </div>
    </Card>
  );
}
