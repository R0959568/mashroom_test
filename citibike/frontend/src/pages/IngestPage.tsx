import { useEffect } from "react";
import { ingestFacade } from "../facade/ingestFacade";
import { FilesPreview } from "../components/FilesPreview";
import { IngestControls } from "../components/IngestControls";
import { RunDetail } from "../components/RunDetail";
import { RunsTable } from "../components/RunsTable";

export function IngestPage() {
  useEffect(() => {
    void ingestFacade.loadRuns();
  }, []);

  return (
    <>
      <h1>Citi Bike ingest</h1>
      <IngestControls />
      <FilesPreview />
      <RunDetail />
      <RunsTable />
    </>
  );
}
