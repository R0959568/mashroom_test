import { useEffect } from "react";
import { mushroomFacade } from "../facade/mushroomFacade";
import { useMushroomStore } from "../stores/mushroomStore";
import { MushroomForm } from "../components/MushroomForm";
import { MushroomResult } from "../components/MushroomResult";

export function MushroomPage() {
  const error = useMushroomStore((state) => state.error);

  useEffect(() => {
    void mushroomFacade.loadFeatures();
  }, []);

  return (
    <>
      <h1>Mushroom classifier</h1>
      <p>
        This educational model is only for dataset examples. Never use its prediction to identify or decide
        whether to eat a real mushroom.
      </p>
      {error && <p role="alert">{error}</p>}
      <MushroomForm />
      <MushroomResult />
    </>
  );
}
