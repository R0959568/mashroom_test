import { useIngestStore } from "../stores/ingestStore";
import { fmt } from "../lib/format";
import { Card } from "./Card";
import { StatusBadge } from "./StatusBadge";
import { Empty, Num, Table } from "./Table";
import styles from "./RunDetail.module.css";

export function RunDetail() {
  const detail = useIngestStore((s) => s.selectedRun);
  if (!detail) return null;
  const { run, files } = detail;
  const rows = files.reduce((sum, f) => sum + f.rowsWritten, 0);
  const parquet = files.reduce((sum, f) => sum + f.parquetBytes, 0);

  return (
    <Card title={`Run #${run.id}`} actions={<StatusBadge status={run.status} />}>
      <dl className={styles.facts}>
        <dt>Files modified after</dt>
        <dd>{fmt.dateTime(run.since)}</dd>
        <dt>Started</dt>
        <dd>{fmt.dateTime(run.startedAt)}</dd>
        <dt>Finished</dt>
        <dd>{fmt.dateTime(run.finishedAt)}</dd>
        <dt>Progress</dt>
        <dd>
          {run.filesDone + run.filesFailed} / {run.filesFound} files
        </dd>
        <dt>Rows written</dt>
        <dd>{fmt.int(rows)}</dd>
        <dt>Parquet written</dt>
        <dd>{fmt.bytes(parquet)}</dd>
      </dl>
      {run.error && <p className={styles.error}>{run.error}</p>}
      <Table>
        <thead>
          <tr>
            <th>File</th>
            <th>Status</th>
            <th className={styles.right}>Size</th>
            <th className={styles.right}>Months</th>
            <th className={styles.right}>Rows</th>
            <th className={styles.right}>Parquet</th>
            <th className={styles.right}>Duration</th>
            <th>Error</th>
          </tr>
        </thead>
        <tbody>
          {files.length === 0 && (
            <Empty colSpan={8}>{run.status === "running" ? "Listing the bucket…" : "No files in this run."}</Empty>
          )}
          {files.map((f) => (
            <tr key={f.id}>
              <td className={styles.mono}>{f.fileName}</td>
              <td>
                <StatusBadge status={f.status} />
              </td>
              <Num>{fmt.bytes(f.sizeBytes)}</Num>
              <Num>{fmt.int(f.monthsWritten)}</Num>
              <Num>{fmt.int(f.rowsWritten)}</Num>
              <Num>{fmt.bytes(f.parquetBytes)}</Num>
              <Num>{fmt.duration(f.durationSeconds)}</Num>
              <td className={styles.errorCell}>{fmt.text(f.error)}</td>
            </tr>
          ))}
        </tbody>
      </Table>
    </Card>
  );
}
