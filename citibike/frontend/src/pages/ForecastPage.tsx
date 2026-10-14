import { useEffect } from "react";
import { forecastFacade } from "../facade/forecastFacade";
import { ForecastControls } from "../components/ForecastControls";
import { ForecastResult } from "../components/ForecastResult";

export function ForecastPage() {
  useEffect(() => {
    void forecastFacade.init();
  }, []);

  return (
    <>
      <h1>Citi Bike forecast</h1>
      <ForecastControls />
      <ForecastResult />
    </>
  );
}
