import { mushroomService } from "../services/mushroomService";
import { useMushroomStore } from "../stores/mushroomStore";
import type { MushroomFeatureValues } from "../types/mushroom";

const message = (error: unknown) => (error instanceof Error ? error.message : String(error));

export const mushroomFacade = {
  loadFeatures: async () => {
    const state = useMushroomStore.getState();
    if (state.catalogLoaded || state.loading) return;
    state.setLoading(true);
    state.setError(null);
    try {
      state.setCatalog(await mushroomService.getFeatures());
    } catch (error) {
      state.setError(
        `The model API is unavailable (${message(error)}). The saved model's feature form is shown, but predictions need the backend and model service.`,
      );
    } finally {
      state.setLoading(false);
    }
  },

  setValue: (name: string, value: number | string | null) => {
    useMushroomStore.getState().setValue(name, value);
    useMushroomStore.getState().setError(null);
  },

  predict: async (values: MushroomFeatureValues) => {
    const state = useMushroomStore.getState();
    if (state.predicting || !state.catalog) return;
    const missingNumbers = state.catalog.features
      .filter((feature) => feature.kind === "numeric" && typeof values[feature.name] !== "number")
      .map((feature) => feature.name);
    if (missingNumbers.length > 0) {
      state.setError(`Enter a number for: ${missingNumbers.join(", ")}`);
      return;
    }

    state.setPredicting(true);
    state.setError(null);
    state.setPrediction(null);
    try {
      state.setPrediction(await mushroomService.predict(values));
    } catch (error) {
      state.setError(message(error));
    } finally {
      state.setPredicting(false);
    }
  },
};
