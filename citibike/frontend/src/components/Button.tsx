import type { ButtonHTMLAttributes } from "react";
import styles from "./Button.module.css";

type Props = ButtonHTMLAttributes<HTMLButtonElement> & { variant?: "primary" | "secondary" };

export function Button({ variant = "secondary", className, ...rest }: Props) {
  return (
    <button
      type="button"
      className={`${styles.button} ${variant === "primary" ? styles.primary : styles.secondary} ${className ?? ""}`}
      {...rest}
    />
  );
}
