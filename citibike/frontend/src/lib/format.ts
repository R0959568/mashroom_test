// Shared formatters: every table cell goes through these, raw values stay in the store.

const int = new Intl.NumberFormat("en-GB");

export const fmt = {
  int: (v: number | null | undefined) => (v == null ? "—" : int.format(v)),

  bytes: (v: number | null | undefined) => {
    if (v == null) return "—";
    if (v >= 1e9) return `${(v / 1e9).toFixed(2)} GB`;
    if (v >= 1e6) return `${(v / 1e6).toFixed(1)} MB`;
    if (v >= 1e3) return `${(v / 1e3).toFixed(0)} kB`;
    return `${v} B`;
  },

  dateTime: (iso: string | null | undefined) =>
    iso ? new Date(iso).toLocaleString("en-GB", { dateStyle: "medium", timeStyle: "short" }) : "—",

  date: (iso: string | null | undefined) =>
    iso ? new Date(iso).toLocaleDateString("en-GB", { dateStyle: "medium" }) : "—",

  duration: (seconds: number | null | undefined) => {
    if (seconds == null) return "—";
    if (seconds < 60) return `${Math.round(seconds)} s`;
    const m = Math.floor(seconds / 60);
    const s = Math.round(seconds % 60);
    return m < 60 ? `${m} m ${s} s` : `${Math.floor(m / 60)} h ${m % 60} m`;
  },

  text: (v: string | null | undefined) => (v && v.length > 0 ? v : "—"),
};
