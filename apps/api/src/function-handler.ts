import type { FastifyInstance, HTTPMethods } from "fastify";

const MAX_BODY = 7 * 1024 * 1024;
const HOP_HEADERS = new Set([
  "host",
  "connection",
  "keep-alive",
  "transfer-encoding",
  "upgrade",
  "proxy-authorization",
  "proxy-authenticate",
  "te",
  "trailer",
  "content-length",
]);

/** Preserve the existing API/auth contract when invoked by the Fetch-based runtime. */
export function functionHandler(app: FastifyInstance) {
  return async (request: Request): Promise<Response> => {
    let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
    try {
      const declared = request.headers.get("content-length");
      if (declared && (!/^\d+$/.test(declared) || Number(declared) > MAX_BODY)) {
        await request.body?.cancel();
        return new Response(null, { status: 413, headers: { "cache-control": "no-store" } });
      }
      const chunks: Buffer[] = [];
      let size = 0;
      reader = request.body?.getReader();
      if (reader) {
        while (true) {
          const part = await reader.read();
          if (part.done) break;
          size += part.value.byteLength;
          if (size > MAX_BODY) {
            await reader.cancel();
            return new Response(null, { status: 413, headers: { "cache-control": "no-store" } });
          }
          chunks.push(Buffer.from(part.value));
        }
      }
      const headers: Record<string, string> = {};
      const blocked = new Set(HOP_HEADERS);
      for (const name of (request.headers.get("connection") ?? "").split(","))
        blocked.add(name.trim().toLowerCase());
      request.headers.forEach((value, name) => {
        if (
          !blocked.has(name) &&
          !name.startsWith("x-forwarded-") &&
          name !== "forwarded" &&
          name !== "x-real-ip"
        )
          headers[name] = value;
      });
      const url = new URL(request.url);
      const result = await app.inject({
        method: request.method as HTTPMethods,
        // Fixed authority preserves the raw query (including repeats) and paths beginning //.
        url: `http://localhost${url.pathname}${url.search}`,
        headers,
        ...(size ? { payload: Buffer.concat(chunks, size) } : {}),
        signal: request.signal,
      });
      const output = new Headers();
      for (const [name, value] of Object.entries(result.headers)) {
        if (HOP_HEADERS.has(name) || value === undefined) continue;
        for (const item of Array.isArray(value) ? value : [value]) output.append(name, String(item));
      }
      const empty = request.method === "HEAD" || [204, 205, 304].includes(result.statusCode);
      return new Response(empty ? null : new Uint8Array(result.rawPayload), {
        status: result.statusCode,
        headers: output,
      });
    } catch {
      return new Response(null, {
        status: request.signal.aborted ? 408 : 503,
        headers: { "cache-control": "no-store" },
      });
    } finally {
      reader?.releaseLock();
    }
  };
}
