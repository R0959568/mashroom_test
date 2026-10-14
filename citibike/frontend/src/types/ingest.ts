// Wire shapes of the backend DTOs (camelCase, enums as lowercase strings).

export type SinceSource = "parameter" | "lastrun" | "default";

export interface RemoteFile {
  name: string;
  region: string;
  year: number;
  month: number | null;
  label: string;
  sizeBytes: number;
  lastModified: string;
}

export interface NewFiles {
  since: string;
  sinceSource: SinceSource;
  count: number;
  totalBytes: number;
  files: RemoteFile[];
}

export type RunStatus = "running" | "succeeded" | "failed";

export interface IngestRun {
  id: number;
  status: RunStatus;
  startedAt: string;
  finishedAt: string | null;
  since: string;
  sinceSource: SinceSource;
  trigger: string;
  filesFound: number;
  filesDone: number;
  filesFailed: number;
  error: string | null;
}

export type FileStatus = "pending" | "downloading" | "converting" | "done" | "failed";

export interface IngestFile {
  id: number;
  fileName: string;
  region: string;
  status: FileStatus;
  lastModified: string;
  sizeBytes: number;
  monthsWritten: number;
  rowsWritten: number;
  parquetBytes: number;
  durationSeconds: number | null;
  error: string | null;
  processedAt: string | null;
}

export interface IngestRunDetail {
  run: IngestRun;
  files: IngestFile[];
}
