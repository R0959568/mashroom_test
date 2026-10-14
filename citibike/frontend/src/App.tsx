import { NavLink, Navigate, Route, Routes } from "react-router-dom";
import { MODULES } from "./modules";
import { Toast } from "./components/Toast";
import { SidebarRobot } from "./components/robot/SidebarRobot";
import styles from "./App.module.css";

export default function App() {
  return (
    <div className={styles.shell}>
      <aside className={styles.sidebar}>
        <div className={styles.brand}>
          <span className={styles.brandTitle}>Avenue 5</span>
          <span className={styles.brandSub}>the deep learning track</span>
        </div>
        <nav className={styles.nav}>
          {MODULES.map((m) => (
            <NavLink
              key={m.key}
              to={m.path}
              className={({ isActive }) => `${styles.navLink} ${isActive ? styles.navLinkActive : ""}`}
            >
              {m.label}
            </NavLink>
          ))}
        </nav>
        <SidebarRobot />
      </aside>
      <main className={styles.content}>
        <Routes>
          {MODULES.map((m) => (
            <Route key={m.key} path={m.path} element={m.element} />
          ))}
          <Route path="*" element={<Navigate to={MODULES[0].path} replace />} />
        </Routes>
      </main>
      <Toast />
    </div>
  );
}
