import { api } from "./api";
import type { MushroomFeatureCatalog, MushroomFeatureValues, MushroomPrediction } from "../types/mushroom";

export const mushroomService = {
  getFeatures: () => api.get<MushroomFeatureCatalog>("/mushroom/features").then((response) => response.data),
  predict: (features: MushroomFeatureValues) =>
    api.post<MushroomPrediction>("/mushroom/predict", { features }).then((response) => response.data),
};
