import { useIngestStore } from "../stores/ingestStore";
import { fmt } from "../lib/format";
import { Card } from "./Card";
import { Empty, Num, Table } from "./Table";
import styles from "./FilesPreview.module.css";

const SOURCE_LABEL = { parameter: "from the date you entered", lastrun: "start of the last successful run", default: "configured default (first backfill)" };

export function FilesPreview() {
  const preview = useIngestStore((s) => s.preview);
  if (!preview) return null;
  return (
    <Card title="Preview">
      <p className={styles.summary}>
        {preview.count} file(s), {fmt.bytes(preview.totalBytes)} to download — modified after{" "}
        <strong>{fmt.dateTime(preview.since)}</strong> ({SOURCE_LABEL[preview.sinceSource]}).
      </p>
      <Table>
        <thead>
          <tr>
            <th>File</th>
            <th>Region</th>
            <th>Covers</th>
            <th>Last modified</th>
            <th className={styles.right}>Size</th>
          </tr>
        </thead>
        <tbody>
          {preview.files.length === 0 && <Empty colSpan={5}>Nothing new since that date.</Empty>}
          {preview.files.map((f) => (
            <tr key={f.name}>
              <td className={styles.mono}>{f.name}</td>
              <td>{f.region}</td>
              <td>{f.label}</td>
              <td>{fmt.dateTime(f.lastModified)}</td>
              <Num>{fmt.bytes(f.sizeBytes)}</Num>
            </tr>
          ))}
        </tbody>
      </Table>
    </Card>
  );
}
