import { create } from "zustand";
import {
  DEFAULT_MUSHROOM_CATALOG,
  type MushroomFeatureCatalog,
  type MushroomFeatureValues,
  type MushroomPrediction,
} from "../types/mushroom";

interface MushroomState {
  catalog: MushroomFeatureCatalog | null;
  catalogLoaded: boolean;
  values: MushroomFeatureValues;
  prediction: MushroomPrediction | null;
  loading: boolean;
  predicting: boolean;
  error: string | null;
  setCatalog: (catalog: MushroomFeatureCatalog) => void;
  setValue: (name: string, value: number | string | null) => void;
  setPrediction: (prediction: MushroomPrediction | null) => void;
  setLoading: (loading: boolean) => void;
  setPredicting: (predicting: boolean) => void;
  setError: (error: string | null) => void;
}

export const useMushroomStore = create<MushroomState>()((set) => ({
  catalog: DEFAULT_MUSHROOM_CATALOG,
  catalogLoaded: false,
  values: Object.fromEntries(DEFAULT_MUSHROOM_CATALOG.features.map((feature) => [feature.name, null])),
  prediction: null,
  loading: false,
  predicting: false,
  error: null,
  setCatalog: (catalog) =>
    set((state) => ({
      catalog,
      catalogLoaded: true,
      values: Object.fromEntries(
        catalog.features.map((feature) => [feature.name, state.values[feature.name] ?? null]),
      ),
    })),
  setValue: (name, value) =>
    set((state) => ({ values: { ...state.values, [name]: value }, prediction: null })),
  setPrediction: (prediction) => set({ prediction }),
  setLoading: (loading) => set({ loading }),
  setPredicting: (predicting) => set({ predicting }),
  setError: (error) => set({ error }),
}));
