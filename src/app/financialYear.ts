export type PeriodLike = { fy: string; start_date: string; end_date: string; status: string };

/** Indian financial year label from a local date. April starts the year. */
export function computerFinancialYear(now = new Date()): string {
  const y = now.getMonth() >= 3 ? now.getFullYear() : now.getFullYear() - 1;
  return `${y}-${String((y + 1) % 100).padStart(2, "0")}`;
}

export function todayIso(now = new Date()): string {
  const m = String(now.getMonth() + 1).padStart(2, "0");
  const d = String(now.getDate()).padStart(2, "0");
  return `${now.getFullYear()}-${m}-${d}`;
}

/**
 * Prefer the company period that contains today, then the latest open period.
 * `fallback` is the computer year, used when the file has no periods or the
 * caller cannot read them.
 */
export function headerFinancialYear(periods: PeriodLike[], today: string, fallback: string): string {
  const covering = periods.find((p) => p.start_date <= today && today <= p.end_date);
  if (covering) return covering.fy;
  const open = periods.filter((p) => p.status === "open").sort((a, b) => b.start_date.localeCompare(a.start_date));
  if (open.length > 0) return open[0].fy;
  return fallback;
}
