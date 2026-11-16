import { createHash } from "node:crypto";
import { setTimeout as delay } from "node:timers/promises";
import type { FastifyPluginAsync } from "fastify";
import type { ZodTypeProvider } from "fastify-type-provider-zod";
import { z } from "zod";
import type { AppDeps } from "../app.js";
import { listProjects } from "../authz/projects.js";
import { listProjectTasks } from "../authz/tasks.js";
import { PERMISSIONS } from "../authz/catalogue.js";
import { AppError, errorBody } from "../errors.js";
import { createGuards } from "../principal.js";
import { roleNames } from "../organization.js";
import { projectView } from "../project-edit-contract.js";

/** Bounded long-poll. Every check reauthenticates and re-queries the caller's current RLS scope. */
export const liveProjectRoutes: FastifyPluginAsync<AppDeps> = async (app, deps) => {
  const guards = createGuards(deps);
  const users = new Map<string, number>();
  const shutdown = new AbortController();
  let active = 0;
  const tick = deps.liveTiming?.tickMs ?? 1000;
  const wait = deps.liveTiming?.waitMs ?? 20000;
  app.addHook("preClose", async () => {
    shutdown.abort();
  });
  app.withTypeProvider<ZodTypeProvider>().get(
    "/v1/mobile/projects/live",
    {
      config: { rateLimit: { max: 120, timeWindow: "1 minute" } },
      schema: {
        tags: ["projects"],
        summary: "Wait for the current authorized project page or permission snapshot to change",
        querystring: z.object({
          since: z
            .string()
            .regex(/^[a-f0-9]{64}$/)
            .optional(),
          after: z.uuid().optional(),
          taskProjectId: z.uuid().optional(),
          taskAfter: z.uuid().optional(),
        }),
        response: {
          200: z.object({
            revision: z.string(),
            projects: z.array(projectView),
            nextCursor: z.uuid().nullable(),
            roles: z.array(z.string()),
            permissions: z.array(z.string()),
            taskPage: z
              .object({
                projectId: z.uuid(),
                nextCursor: z.uuid().nullable(),
                tasks: z.array(
                  z.object({
                    id: z.uuid(),
                    projectId: z.uuid(),
                    title: z.string(),
                    description: z.string(),
                    dueAt: z.iso.datetime().nullable(),
                    status: z.enum(["open", "in_progress", "completed", "cancelled"]),
                    priority: z.enum(["low", "normal", "high", "urgent"]),
                    version: z.number().int().positive(),
                  }),
                ),
              })
              .nullable(),
          }),
          400: errorBody,
          401: errorBody,
          403: errorBody,
          429: errorBody,
          503: errorBody,
        },
      },
    },
    async (req, reply) => {
      const first = await guards.authenticate(req);
      if (active >= 100 || (users.get(first.userId) ?? 0) >= 4)
        throw new AppError(429, "LIVE_LIMIT_REACHED", "Too many live connections. Retry shortly.");
      active++;
      users.set(first.userId, (users.get(first.userId) ?? 0) + 1);
      const disconnected = new AbortController();
      const close = () => disconnected.abort();
      reply.raw.once("close", close);
      const signal = AbortSignal.any([shutdown.signal, disconnected.signal]);
      const deadline = Date.now() + wait;
      try {
        while (true) {
          if (shutdown.signal.aborted)
            throw new AppError(503, "LIVE_UNAVAILABLE", "Live updates are restarting.");
          if (disconnected.signal.aborted) return reply;
          const by = await guards.authenticate(req);
          const page = by.permissions.has(PERMISSIONS.projectsRead)
            ? await listProjects(deps.db, by, 25, req.query.after)
            : { projects: [], nextCursor: null };
          const snapshot = {
            ...page,
            // Only a project on this currently authorized page can expose its task page.
            taskPage:
              req.query.taskProjectId && page.projects.some((row) => row.id === req.query.taskProjectId)
                ? await listProjectTasks(deps.db, by, req.query.taskProjectId, req.query.taskAfter)
                : null,
            roles: (await roleNames(deps.db, by.membershipId)).sort(),
            permissions: [...by.permissions].sort(),
          };
          const revision = createHash("sha256")
            .update(
              JSON.stringify([
                by.organizationId,
                by.userId,
                by.membershipId,
                req.query.after ?? null,
                req.query.taskProjectId ?? null,
                req.query.taskAfter ?? null,
                snapshot,
              ]),
            )
            .digest("hex");
          if (revision !== req.query.since || Date.now() >= deadline) {
            // A suspension/token revocation may commit while the page query is running.
            const latest = await guards.authenticate(req);
            if (
              latest.membershipId !== by.membershipId ||
              JSON.stringify([...latest.permissions].sort()) !== JSON.stringify([...by.permissions].sort())
            )
              continue;
            return { revision, ...snapshot };
          }
          try {
            await delay(Math.min(tick, Math.max(1, deadline - Date.now())), undefined, { signal });
          } catch {
            if (!signal.aborted) throw new Error("Live wait failed");
          }
        }
      } finally {
        reply.raw.removeListener("close", close);
        active--;
        const remaining = (users.get(first.userId) ?? 1) - 1;
        if (remaining > 0) users.set(first.userId, remaining);
        else users.delete(first.userId);
      }
    },
  );
};
