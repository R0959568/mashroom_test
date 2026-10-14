import { lazy, Suspense, useState } from "react";
import styles from "./SidebarRobot.module.css";

// The scene (three.js, about 150 kB gzipped) loads after the page: a failed or slow load costs nothing but the robot.
const RobotScene = lazy(() => import("./RobotScene"));
const MODEL = "/models/robot.glb";

/**
 * The robot of the Bleckmann AI portal, at the bottom of the sidebar: it turns its head, torso and arms toward the
 * pointer wherever it is on the page, blinks, and drifts back to an idle scan when the pointer leaves the window.
 * Decorative only: hidden from assistive tech, never takes the pointer, fades in once the model is loaded, and
 * disappears when WebGL or the model fails.
 */
export function SidebarRobot() {
  const [ready, setReady] = useState(false);
  const [failed, setFailed] = useState(false);
  if (failed) return null;
  return (
    <div className={styles.wrap} aria-hidden="true">
      <div className={styles.glow} />
      <div className={`${styles.scene} ${ready ? styles.sceneReady : ""}`}>
        <Suspense fallback={null}>
          <RobotScene model={MODEL} onReady={() => setReady(true)} onError={() => setFailed(true)} />
        </Suspense>
      </div>
    </div>
  );
}
