import { ingestFacade } from "../facade/ingestFacade";
import { useIngestStore } from "../stores/ingestStore";
import { fmt } from "../lib/format";
import { Button } from "./Button";
import { Card } from "./Card";
import { StatusBadge } from "./StatusBadge";
import { Empty, Num, Table } from "./Table";
import styles from "./RunsTable.module.css";

export function RunsTable() {
  const runs = useIngestStore((s) => s.runs);
  const runsLoading = useIngestStore((s) => s.runsLoading);
  const selectedId = useIngestStore((s) => s.selectedRun?.run.id ?? null);

  return (
    <Card
      title="Run history"
      actions={
        <Button onClick={() => void ingestFacade.loadRuns()} disabled={runsLoading}>
          {runsLoading ? "Loading…" : "Refresh"}
        </Button>
      }
    >
      <Table>
        <thead>
          <tr>
            <th>#</th>
            <th>Status</th>
            <th>Started</th>
            <th>Finished</th>
            <th>Files after</th>
            <th>Trigger</th>
            <th className={styles.right}>Found</th>
            <th className={styles.right}>Done</th>
            <th className={styles.right}>Failed</th>
          </tr>
        </thead>
        <tbody>
          {runs.length === 0 && <Empty colSpan={9}>No runs yet.</Empty>}
          {runs.map((r) => (
            <tr
              key={r.id}
              className={`${styles.row} ${r.id === selectedId ? styles.selected : ""}`}
              onClick={() => void ingestFacade.selectRun(r.id)}
            >
              <td>{r.id}</td>
              <td>
                <StatusBadge status={r.status} />
              </td>
              <td>{fmt.dateTime(r.startedAt)}</td>
              <td>{fmt.dateTime(r.finishedAt)}</td>
              <td>
                {fmt.dateTime(r.since)} <small className={styles.muted}>({r.sinceSource})</small>
              </td>
              <td>{r.trigger}</td>
              <Num>{fmt.int(r.filesFound)}</Num>
              <Num>{fmt.int(r.filesDone)}</Num>
              <Num>{fmt.int(r.filesFailed)}</Num>
            </tr>
          ))}
        </tbody>
      </Table>
    </Card>
  );
}
