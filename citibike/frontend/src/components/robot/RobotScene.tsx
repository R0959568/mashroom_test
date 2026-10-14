import { useEffect, useRef } from "react";
import { createRobotScene } from "./robotStage";
import styles from "./RobotScene.module.css";

// Loaded lazily by SidebarRobot: this module pulls in three.js, which the rest
// of the app never needs. The callbacks live in a ref so a parent re-render
// never tears the scene down.
export default function RobotScene({
  model,
  onReady,
  onError,
}: {
  model: string;
  onReady?: () => void;
  onError?: (message: string) => void;
}) {
  const host = useRef<HTMLDivElement>(null);
  const callbacks = useRef({ onReady, onError });
  callbacks.current = { onReady, onError };

  useEffect(() => {
    const el = host.current;
    if (!el) return;
    return createRobotScene(el, model, {
      onReady: () => callbacks.current.onReady?.(),
      onError: (message) => callbacks.current.onError?.(message),
    });
  }, [model]);

  return <div ref={host} className={styles.host} />;
}
