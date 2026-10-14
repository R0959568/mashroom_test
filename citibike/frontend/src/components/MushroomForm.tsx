import { mushroomFacade } from "../facade/mushroomFacade";
import { useMushroomStore } from "../stores/mushroomStore";
import { Button } from "./Button";
import { Card } from "./Card";
import styles from "./MushroomForm.module.css";

const FEATURE_LABELS: Record<string, string> = {
  "cap-diameter": "Cap diameter (cm)",
  "stem-height": "Stem height (cm)",
  "stem-width": "Stem width (mm)",
  "spore-print-color": "Spore print color",
  "gill-color": "Gill color",
  habitat: "Habitat",
  season: "Season",
  "ring-type": "Ring type",
  "cap-shape": "Cap shape",
  "stem-surface": "Stem surface",
};

const COLOR_LABELS: Record<string, string> = {
  b: "Buff",
  e: "Red",
  g: "Gray",
  k: "Black",
  l: "Blue",
  n: "Brown",
  o: "Orange",
  p: "Pink",
  r: "Green",
  u: "Purple",
  w: "White",
  y: "Yellow",
};

const CATEGORY_LABELS: Record<string, Record<string, string>> = {
  "spore-print-color": COLOR_LABELS,
  "gill-color": { ...COLOR_LABELS, f: "None" },
  habitat: {
    d: "Woods",
    g: "Grasses",
    h: "Heaths",
    l: "Leaves",
    m: "Meadows",
    p: "Paths",
    u: "Urban",
    w: "Waste",
  },
  season: { a: "Autumn", s: "Spring", u: "Summer", w: "Winter" },
  "ring-type": {
    e: "Evanescent",
    f: "None",
    g: "Grooved",
    l: "Large",
    m: "Movable",
    p: "Pendant",
    r: "Flaring",
    s: "Sheathing",
    z: "Zoned",
  },
  "cap-shape": {
    b: "Bell-shaped",
    c: "Conical",
    f: "Flat",
    o: "Other",
    p: "Spherical",
    s: "Sunken",
    x: "Convex",
  },
  "stem-surface": {
    e: "Fleshy",
    f: "None",
    g: "Grooved",
    h: "Shiny",
    i: "Fibrous",
    k: "Silky",
    s: "Smooth",
    t: "Sticky",
    w: "Wrinkled",
    y: "Scaly",
  },
};

const featureLabel = (name: string) => FEATURE_LABELS[name] ?? name;

const categoryLabel = (featureName: string, code: string) =>
  CATEGORY_LABELS[featureName]?.[code] ?? `Code ${code}`;

export function MushroomForm() {
  const catalog = useMushroomStore((state) => state.catalog);
  const values = useMushroomStore((state) => state.values);
  const loading = useMushroomStore((state) => state.loading);
  const predicting = useMushroomStore((state) => state.predicting);

  return (
    <Card title="Mushroom features">
      {loading && <p role="status">Loading model feature options…</p>}
      {catalog && (
        <form
          className={styles.form}
          onSubmit={(event) => {
            event.preventDefault();
            void mushroomFacade.predict(values);
          }}
        >
          <p className={styles.help}>
            Choose a labeled category or enter the measurement in the units shown. Letters in parentheses
            are the original dataset codes.
          </p>
          <div className={styles.grid}>
            {catalog.features.map((feature) => (
              <label className={styles.field} key={feature.name}>
                <span>{featureLabel(feature.name)}</span>
                {feature.kind === "numeric" ? (
                  <input
                    type="number"
                    min="0"
                    step="any"
                    required
                    aria-label={featureLabel(feature.name)}
                    value={typeof values[feature.name] === "number" ? values[feature.name]! : ""}
                    onChange={(event) =>
                      mushroomFacade.setValue(
                        feature.name,
                        event.target.value === "" ? null : Number(event.target.value),
                      )
                    }
                  />
                ) : (
                  <select
                    aria-label={featureLabel(feature.name)}
                    value={typeof values[feature.name] === "string" ? values[feature.name]! : ""}
                    onChange={(event) =>
                      mushroomFacade.setValue(feature.name, event.target.value || null)
                    }
                  >
                    <option value="">Unknown / missing</option>
                    {feature.options.map((option) => (
                      <option key={option} value={option}>
                        {categoryLabel(feature.name, option)} ({option})
                      </option>
                    ))}
                  </select>
                )}
              </label>
            ))}
          </div>
          <p className={styles.source}>
            Category labels follow the{" "}
            <a href="https://archive.ics.uci.edu/dataset/848/secondary+mushroom+dataset" target="_blank" rel="noreferrer">
              UCI Secondary Mushroom dataset definitions
            </a>
            .
          </p>
          <Button variant="primary" type="submit" disabled={predicting || loading}>
            {predicting ? "Classifying…" : "Classify dataset example"}
          </Button>
        </form>
      )}
    </Card>
  );
}
