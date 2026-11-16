import type { FastifyPluginAsync } from "fastify";
import { fromNodeHeaders } from "better-auth/node";
import type { Auth } from "../auth.js";
import type { Config } from "../config.js";
import { AUTH_BASE_PATH } from "../mobile-contract.js";

const SKIP_HEADERS = new Set([
  "set-cookie",
  "content-length",
  "transfer-encoding",
  "connection",
  "keep-alive",
]);

/**
 * Mounts Better Auth at /api/auth/* (its configured base path). Sign-in, sign-out, sessions, two-factor and password routes are Better Auth's own;
 * public sign-up is disabled in its configuration, so the only way to create an account is bootstrap or an invitation (src/identity.ts).
 */
export const authRoutes: FastifyPluginAsync<{ auth: Auth; config: Config }> = async (
  app,
  { auth, config },
) => {
  // Better Auth reads the raw body itself, and some of its calls send an empty JSON body that Fastify's strict parser would refuse.
  app.addContentTypeParser("application/json", { parseAs: "string" }, (_req, body, done) => done(null, body));

  app.route({
    method: ["GET", "POST"],
    url: `${AUTH_BASE_PATH}/*`,
    schema: { hide: true },
    handler: async (req, reply) => {
      // Build the URL from the configured public address, never from the Host header a client sent.
      const url = new URL(req.url, config.PUBLIC_API_URL);
      const body = typeof req.body === "string" && req.body.length > 0 ? req.body : undefined;
      const response = await auth.handler(
        new Request(url, {
          method: req.method,
          headers: fromNodeHeaders(req.headers),
          ...(body ? { body } : {}),
        }),
      );
      reply.status(response.status);
      response.headers.forEach((value, key) => {
        if (!SKIP_HEADERS.has(key.toLowerCase())) reply.header(key, value);
      });
      const cookies = response.headers.getSetCookie();
      if (cookies.length > 0) reply.header("set-cookie", cookies);
      const text = await response.text();
      return reply.send(text.length > 0 ? text : null);
    },
  });
};
