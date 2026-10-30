import { createRealtime, rawSql } from "@neon/realtime/server";

function databaseNameFromUrl(connectionString: string): string {
  const normalized = connectionString.replace(/^postgres:\/\//, "postgresql://");
  const u = new URL(normalized);
  const name = u.pathname.replace(/^\//, "").split("/")[0];
  return name || "neondb";
}

/** Neon console often gives https://; the Realtime SDK expects wss://. */
export function realtimeWebSocketUrl(url: string): string {
  const trimmed = url.trim();
  if (trimmed.startsWith("wss://") || trimmed.startsWith("ws://")) return trimmed;
  if (trimmed.startsWith("https://")) return "wss://" + trimmed.slice("https://".length);
  if (trimmed.startsWith("http://")) return "ws://" + trimmed.slice("http://".length);
  return trimmed;
}

/** Subscribe to `select 1` through Neon Realtime (server-side SDK). */
export async function verifyRealtime(
  databaseUrl: string,
  url: string,
  secret: string,
): Promise<void> {
  const db = databaseNameFromUrl(databaseUrl);
  const wsUrl = realtimeWebSocketUrl(url);
  const realtime = createRealtime({ secret, db, url: wsUrl, logLevel: "warn" });
  const subscription = await realtime.subscribe(rawSql<{ ok: number }>("select 1 as ok"));

  await new Promise<void>((resolve, reject) => {
    const timer = setTimeout(() => {
      subscription.unsubscribe();
      realtime.close();
      reject(new Error("Realtime subscription timed out after 20s"));
    }, 20_000);

    const stop = subscription.onChange(({ data, status, error }) => {
      if (error) {
        clearTimeout(timer);
        stop();
        subscription.unsubscribe();
        realtime.close();
        reject(error);
        return;
      }
      if (status === "live" && data?.[0]?.ok === 1) {
        clearTimeout(timer);
        stop();
        subscription.unsubscribe();
        realtime.close();
        console.log("[realtime] ok — live query returned 1");
        resolve();
      }
    });
  });
}
