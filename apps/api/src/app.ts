import { randomUUID } from "node:crypto";
import rateLimit from "@fastify/rate-limit";
import swagger from "@fastify/swagger";
import Fastify, { type FastifyInstance } from "fastify";
import {
  hasZodFastifySchemaValidationErrors,
  jsonSchemaTransform,
  serializerCompiler,
  validatorCompiler,
} from "fastify-type-provider-zod";
import type { Auth } from "./auth.js";
import type { Config } from "./config.js";
import type { Db } from "./db/types.js";
import { AppError, body } from "./errors.js";
import type { DbHealth } from "./health.js";
import { authRoutes } from "./routes/auth-mount.js";
import { healthRoutes } from "./routes/health.js";
import { v1Routes } from "./routes/v1.js";

export interface AppDeps {
  config: Config;
  health: DbHealth;
  db: Db;
  auth: Auth;
  /** How many migrations this build expects the database to have. */
  expectedMigrations: number;
  version: string;
}

/** Builds the API. Everything it needs is passed in, so tests can run it without a network or a real database. */
export async function buildApp(deps: AppDeps): Promise<FastifyInstance> {
  const app = Fastify({
    logger: deps.config.LOG_LEVEL === "silent" ? false : { level: deps.config.LOG_LEVEL },
    // Our own request ids only: a client-supplied id would end up in the logs.
    genReqId: () => `req_${randomUUID().replaceAll("-", "").slice(0, 16)}`,
    requestIdHeader: false,
  });

  app.setValidatorCompiler(validatorCompiler);
  app.setSerializerCompiler(serializerCompiler);

  await app.register(swagger, {
    openapi: {
      openapi: "3.1.0",
      info: { title: "Myna API", version: deps.version, description: "The company-owned Myna backend." },
      servers: [{ url: deps.config.PUBLIC_API_URL }],
    },
    transform: jsonSchemaTransform,
  });

  app.addHook("onSend", async (req, reply) => {
    reply.header("x-request-id", req.id);
    reply.header("cache-control", "no-store");
  });

  // One error shape, and nothing internal in it (guidelines sections 9.3 and 9.4).
  app.setErrorHandler((err, req, reply) => {
    if (err instanceof AppError) {
      return reply.status(err.statusCode).send(body(err.code, err.message, req.id));
    }
    if (hasZodFastifySchemaValidationErrors(err)) {
      return reply.status(400).send(body("VALIDATION_FAILED", "The request was not valid.", req.id));
    }
    const status = (err as { statusCode?: number }).statusCode;
    if (typeof status === "number" && status >= 400 && status < 500) {
      return reply.status(status).send(body("BAD_REQUEST", "The request could not be processed.", req.id));
    }
    req.log.error({ err }, "unhandled error");
    return reply.status(500).send(body("INTERNAL", "Something went wrong.", req.id));
  });
  app.setNotFoundHandler((req, reply) => reply.status(404).send(body("NOT_FOUND", "No such route.", req.id)));

  await app.register(rateLimit, { global: false });
  await app.register(healthRoutes, deps);
  await app.register(authRoutes, { auth: deps.auth, config: deps.config });
  await app.register(v1Routes, deps);

  app.get("/openapi.json", { schema: { hide: true } }, async () => app.swagger());
  return app;
}
