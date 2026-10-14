import { useMushroomStore } from "../stores/mushroomStore";
import { Card } from "./Card";
import styles from "./MushroomResult.module.css";

export function MushroomResult() {
  const prediction = useMushroomStore((state) => state.prediction);
  if (!prediction) return null;

  return (
    <Card title="Model result">
      <div className={styles.result} aria-live="polite">
        <strong className={prediction.predictedClass === "poisonous" ? styles.poisonous : styles.edible}>
          Dataset label: {prediction.predictedClass}
        </strong>
        <p>Estimated poisonous-class probability: {(prediction.poisonousProbability * 100).toFixed(1)}%</p>
        <small>
          {prediction.modelName}; classification threshold {(prediction.threshold * 100).toFixed(0)}%.
        </small>
      </div>
      <p className={styles.warning} role="note">{prediction.warning}</p>
    </Card>
  );
}
