export interface MushroomFeature {
  name: string;
  kind: "numeric" | "categorical";
  options: string[];
}

export interface MushroomFeatureCatalog {
  modelName: string;
  threshold: number;
  features: MushroomFeature[];
  warning: string;
}

export type MushroomFeatureValues = Record<string, number | string | null>;

export interface MushroomPrediction {
  predictedClass: "edible" | "poisonous";
  poisonousProbability: number;
  threshold: number;
  modelName: string;
  warning: string;
}

export const DEFAULT_MUSHROOM_CATALOG: MushroomFeatureCatalog = {
  modelName: "Random Forest",
  threshold: 0.5,
  warning:
    "This educational model is not reliable enough to identify real mushrooms. Never eat a mushroom based on this prediction.",
  features: [
    { name: "cap-diameter", kind: "numeric", options: [] },
    { name: "stem-height", kind: "numeric", options: [] },
    { name: "stem-width", kind: "numeric", options: [] },
    { name: "spore-print-color", kind: "categorical", options: ["g", "k", "n", "p", "r", "u", "w"] },
    { name: "gill-color", kind: "categorical", options: ["b", "e", "f", "g", "k", "n", "o", "p", "r", "u", "w", "y"] },
    { name: "habitat", kind: "categorical", options: ["d", "g", "h", "l", "m", "p", "u", "w"] },
    { name: "season", kind: "categorical", options: ["a", "s", "u", "w"] },
    { name: "ring-type", kind: "categorical", options: ["e", "f", "g", "l", "m", "p", "r", "z"] },
    { name: "cap-shape", kind: "categorical", options: ["b", "c", "f", "o", "p", "s", "x"] },
    { name: "stem-surface", kind: "categorical", options: ["f", "g", "h", "i", "k", "s", "t", "y"] },
  ],
};
