import { create } from "zustand";
import type { IngestRun, IngestRunDetail, NewFiles } from "../types/ingest";

interface IngestState {
  sinceInput: string;                 // YYYY-MM-DD from the date input; "" = since the last successful run
  preview: NewFiles | null;
  previewLoading: boolean;
  runs: IngestRun[];
  runsLoading: boolean;
  selectedRun: IngestRunDetail | null;
  starting: boolean;
  watchingRunId: number | null;       // run currently being polled, so two loops never run at once
  error: string | null;
  setSinceInput: (v: string) => void;
  setPreview: (p: NewFiles | null) => void;
  setPreviewLoading: (v: boolean) => void;
  setRuns: (r: IngestRun[]) => void;
  setRunsLoading: (v: boolean) => void;
  setSelectedRun: (r: IngestRunDetail | null) => void;
  setStarting: (v: boolean) => void;
  setWatchingRunId: (id: number | null) => void;
  setError: (e: string | null) => void;
}

// State and plain setters only; every rule lives in facade/ingestFacade.ts.
export const useIngestStore = create<IngestState>()((set) => ({
  sinceInput: "",
  preview: null,
  previewLoading: false,
  runs: [],
  runsLoading: false,
  selectedRun: null,
  starting: false,
  watchingRunId: null,
  error: null,
  setSinceInput: (sinceInput) => set({ sinceInput }),
  setPreview: (preview) => set({ preview }),
  setPreviewLoading: (previewLoading) => set({ previewLoading }),
  setRuns: (runs) => set({ runs }),
  setRunsLoading: (runsLoading) => set({ runsLoading }),
  setSelectedRun: (selectedRun) => set({ selectedRun }),
  setStarting: (starting) => set({ starting }),
  setWatchingRunId: (watchingRunId) => set({ watchingRunId }),
  setError: (error) => set({ error }),
}));
