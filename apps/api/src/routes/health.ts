import type { FastifyPluginAsync } from "fastify";
import type { ZodTypeProvider } from "fastify-type-provider-zod";
import { z } from "zod";
import type { AppDeps } from "../app.js";
import { body, errorBody } from "../errors.js";
import { MOBILE_CONNECTION } from "../mobile-contract.js";

const live = z.object({ status: z.literal("ok"), service: z.literal("myna-api"), version: z.string() });
const ready = z.object({
  status: z.literal("ready"),
  database: z.literal("ok"),
  migrations: z.literal("current"),
});

export const healthRoutes: FastifyPluginAsync<AppDeps> = async (app, deps) => {
  const typed = app.withTypeProvider<ZodTypeProvider>();

  typed.get(
    "/v1/mobile/connection",
    {
      schema: {
        tags: ["mobile"],
        summary: "Discover mobile compatibility and the company authentication path",
        response: {
          200: z.object({
            service: z.literal("myna-api"),
            apiVersion: z.literal(1),
            minimumVersionCode: z.number().int(),
            authBasePath: z.string(),
            capabilities: z.array(z.string()),
          }),
        },
      },
    },
    async () => MOBILE_CONNECTION,
  );

  // Liveness: the process is up. Does not touch the database, so a database outage does not restart the container.
  typed.get("/health", { schema: { response: { 200: live }, tags: ["health"] } }, async () => ({
    status: "ok" as const,
    service: "myna-api" as const,
    version: deps.version,
  }));

  // Readiness: the database answers and has every migration this build ships.
  typed.get(
    "/health/ready",
    { schema: { response: { 200: ready, 503: errorBody }, tags: ["health"] } },
    async (req, reply) => {
      try {
        await deps.health.ping();
        const applied = await deps.health.appliedMigrations();
        if (applied < deps.expectedMigrations) {
          return reply
            .status(503)
            .send(body("MIGRATIONS_PENDING", "The database has not been migrated to this version.", req.id));
        }
      } catch (err) {
        req.log.warn({ err }, "readiness check failed");
        return reply
          .status(503)
          .send(body("DATABASE_UNAVAILABLE", "The database cannot be reached.", req.id));
      }
      return { status: "ready" as const, database: "ok" as const, migrations: "current" as const };
    },
  );
};
