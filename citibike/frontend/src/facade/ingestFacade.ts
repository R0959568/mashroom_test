import { ingestService } from "../services/ingestService";
import { useIngestStore } from "../stores/ingestStore";
import { useSessionStore } from "../stores/sessionStore";
import type { IngestRun } from "../types/ingest";

const POLL_MS = 2500;
const DATE_RE = /^\d{4}-\d{2}(-\d{2})?$/;

const message = (e: unknown) => (e instanceof Error ? e.message : String(e));
const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/** "" -> undefined (backend decides), "2026-09" -> "2026-09-01"; anything else is refused before a call is made. */
function normalizeSince(input: string): string | undefined {
  const value = input.trim();
  if (!value) return undefined;
  if (!DATE_RE.test(value)) throw new Error("Since must be a date like 2026-09-01 (or 2026-09)");
  return value.length === 7 ? `${value}-01` : value;
}

function upsertRun(run: IngestRun) {
  const { runs, setRuns } = useIngestStore.getState();
  const others = runs.filter((r) => r.id !== run.id);
  setRuns([run, ...others].sort((a, b) => b.id - a.id));
}

// All frontend business logic for the ingest feature. Components call these; state comes back via the store.
export const ingestFacade = {
  preview: async () => {
    const s = useIngestStore.getState();
    if (s.previewLoading) return;
    s.setError(null);
    s.setPreviewLoading(true);
    try {
      s.setPreview(await ingestService.listNewFiles(normalizeSince(s.sinceInput)));
    } catch (e) {
      s.setError(message(e));
    } finally {
      s.setPreviewLoading(false);
    }
  },

  loadRuns: async () => {
    const s = useIngestStore.getState();
    s.setRunsLoading(true);
    try {
      const runs = await ingestService.listRuns();
      s.setRuns(runs);
      // After a reload, pick the polling back up for a run that is still in progress.
      const running = runs.find((r) => r.status === "running");
      if (running) void ingestFacade.watchRun(running.id);
      else if (!s.selectedRun && runs.length > 0) void ingestFacade.selectRun(runs[0].id);
    } catch (e) {
      s.setError(message(e));
    } finally {
      s.setRunsLoading(false);
    }
  },

  selectRun: async (id: number) => {
    const s = useIngestStore.getState();
    try {
      s.setSelectedRun(await ingestService.getRun(id));
    } catch (e) {
      s.setError(message(e));
    }
  },

  startRun: async () => {
    const s = useIngestStore.getState();
    if (s.starting || s.watchingRunId !== null) return;
    if (!useSessionStore.getState().adminToken) {
      s.setError("Enter the admin token before starting a run");
      return;
    }
    s.setError(null);
    s.setStarting(true);
    try {
      const run = await ingestService.startRun(normalizeSince(s.sinceInput));
      upsertRun(run);
      await ingestFacade.watchRun(run.id);
    } catch (e) {
      s.setError(message(e));
    } finally {
      s.setStarting(false);
    }
  },

  /** Polls one run until it leaves "running"; progress lands in the store on every tick. */
  watchRun: async (id: number) => {
    const s = useIngestStore.getState();
    if (s.watchingRunId !== null) return;
    s.setWatchingRunId(id);
    try {
      for (;;) {
        const detail = await ingestService.getRun(id);
        s.setSelectedRun(detail);
        upsertRun(detail.run);
        if (detail.run.status !== "running") break;
        await sleep(POLL_MS);
      }
    } catch (e) {
      const status = (e as { status?: number }).status;
      s.setError(status === 404 ? `Run ${id} is gone (backend restarted?); reload the run history` : message(e));
    } finally {
      s.setWatchingRunId(null);
    }
  },
};
