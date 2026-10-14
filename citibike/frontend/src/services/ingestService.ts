import { api } from "./api";
import type { IngestRun, IngestRunDetail, NewFiles } from "../types/ingest";

// Connection layer only: one call per endpoint, no conditions, no orchestration.
export const ingestService = {
  listNewFiles: (since?: string) =>
    api.get<NewFiles>("/s3files", { params: since ? { since } : {} }).then((r) => r.data),

  startRun: (since?: string) =>
    api.post<IngestRun>("/ingest/runs", { since: since ?? null, trigger: "manual" }).then((r) => r.data),

  listRuns: (limit = 20) => api.get<IngestRun[]>("/ingest/runs", { params: { limit } }).then((r) => r.data),

  getRun: (id: number) => api.get<IngestRunDetail>(`/ingest/runs/${id}`).then((r) => r.data),
};
