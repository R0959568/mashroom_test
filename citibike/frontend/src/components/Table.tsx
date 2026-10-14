import type { ReactNode } from "react";
import styles from "./Table.module.css";

// Plain table: the lists here are short and never sorted or filtered by the user.
export function Table({ children }: { children: ReactNode }) {
  return (
    <div className={styles.wrap}>
      <table className={styles.table}>{children}</table>
    </div>
  );
}

export function Num({ children }: { children: ReactNode }) {
  return <td className={styles.num}>{children}</td>;
}

export function Empty({ colSpan, children }: { colSpan: number; children: ReactNode }) {
  return (
    <tr>
      <td colSpan={colSpan} className={styles.empty}>
        {children}
      </td>
    </tr>
  );
}
