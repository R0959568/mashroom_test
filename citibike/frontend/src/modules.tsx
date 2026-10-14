import type { ReactElement } from "react";
import { ForecastPage } from "./pages/ForecastPage";
import { IngestPage } from "./pages/IngestPage";
import { MushroomPage } from "./pages/MushroomPage";

export interface Module {
  key: string;
  label: string;
  path: string;
  element: ReactElement;
}

// One row per page; the sidebar and the router both map over this list. The app hosts every project of the
// track (the mushroom project joins later), so each label names its project.
export const MODULES: Module[] = [
  { key: "forecast", label: "Forecast Citi Bike", path: "/forecast", element: <ForecastPage /> },
  { key: "ingest", label: "Ingest Citi Bike", path: "/ingest", element: <IngestPage /> },
  { key: "mushroom", label: "Mushroom classifier", path: "/mushroom", element: <MushroomPage /> },
];
