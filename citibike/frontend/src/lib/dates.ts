// Day arithmetic on ISO dates (YYYY-MM-DD) in UTC: no time zone or DST surprises, no library.

export const ISO_DATE_RE = /^\d{4}-\d{2}-\d{2}$/;

export const addDays = (iso: string, days: number): string => {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
};

/** The day after an ISO date: what the page forecasts when "today" is that date. */
export const nextDay = (iso: string): string => addDays(iso, 1);
