import { useEffect, useState } from "react";

const WAKE_HINT_AFTER_MS = 3000;

/**
 * True once a running forecast has taken a few seconds: the serverless endpoints behind the forecast API are then
 * probably waking up (up to about 40 s), and the page says so instead of looking stuck.
 */
export function useWakeHint(startedAt: number | null): boolean {
  const [slow, setSlow] = useState(false);
  useEffect(() => {
    if (!startedAt) {
      setSlow(false);
      return;
    }
    const t = setTimeout(() => setSlow(true), WAKE_HINT_AFTER_MS);
    return () => clearTimeout(t);
  }, [startedAt]);
  return slow;
}
