import type { FileStatus, RunStatus } from "../types/ingest";
import styles from "./StatusBadge.module.css";

const TONE: Record<RunStatus | FileStatus, string> = {
  running: styles.info,
  downloading: styles.info,
  converting: styles.info,
  pending: styles.muted,
  succeeded: styles.ok,
  done: styles.ok,
  failed: styles.error,
};

export function StatusBadge({ status }: { status: RunStatus | FileStatus }) {
  return <span className={`${styles.badge} ${TONE[status]}`}>{status}</span>;
}
