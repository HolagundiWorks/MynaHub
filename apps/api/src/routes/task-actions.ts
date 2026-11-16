import type { FastifyPluginAsync } from "fastify";
import type { ZodTypeProvider } from "fastify-type-provider-zod";
import { z } from "zod";
import type { AppDeps } from "../app.js";
import { errorBody } from "../errors.js";
import { createGuards } from "../principal.js";
import { PERMISSIONS } from "../authz/catalogue.js";
import { taskAction, taskCandidates } from "../authz/task-actions.js";
import {
  taskAssignInput,
  taskDeleteInput,
  taskActionResult,
  taskCandidatePage,
} from "../task-action-contract.js";
export const taskActionRoutes: FastifyPluginAsync<AppDeps> = async (app, deps) => {
  const typed = app.withTypeProvider<ZodTypeProvider>(),
    guards = createGuards(deps);
  const errors = { 400: errorBody, 401: errorBody, 403: errorBody, 404: errorBody, 429: errorBody };
  const response = {
    200: taskActionResult,
    ...errors,
    409: z.union([errorBody.extend({ conflict: taskActionResult }), errorBody]),
  };
  const config = { rateLimit: { max: 60, timeWindow: "1 minute" } };
  const params = z.object({ projectId: z.uuid(), taskId: z.uuid() });
  const send = async (
    result: Awaited<ReturnType<typeof taskAction>>,
    reply: import("fastify").FastifyReply,
    requestId: string,
  ) =>
    result.outcome === "conflict"
      ? reply.status(409).send({
          error: {
            code: "VERSION_CONFLICT",
            message: "Review the current task before submitting a new operation.",
            requestId,
          },
          conflict: result,
        })
      : result;
  typed.get(
    "/v1/projects/:projectId/task-assignees",
    {
      config,
      preHandler: guards.requirePermission(PERMISSIONS.tasksAssign),
      schema: {
        tags: ["tasks"],
        params: z.object({ projectId: z.uuid() }),
        querystring: z.object({ after: z.uuid().optional() }),
        response: { 200: taskCandidatePage, ...errors },
      },
    },
    async (req) => taskCandidates(deps.db, req.principal!, req.params.projectId, req.query.after),
  );
  typed.put(
    "/v1/projects/:projectId/tasks/:taskId/assignees",
    {
      config,
      preHandler: guards.requirePermission(PERMISSIONS.tasksAssign),
      schema: { tags: ["tasks"], params, body: taskAssignInput, response },
    },
    async (req, reply) =>
      send(
        await taskAction(
          deps.db,
          req.principal!,
          req.params.projectId,
          req.params.taskId,
          req.body,
          req.id,
          "assign",
        ),
        reply,
        req.id,
      ),
  );
  typed.delete(
    "/v1/projects/:projectId/tasks/:taskId",
    {
      config,
      preHandler: [guards.requirePermission(PERMISSIONS.tasksDelete), guards.requireFresh],
      schema: { tags: ["tasks"], params, body: taskDeleteInput, response },
    },
    async (req, reply) =>
      send(
        await taskAction(
          deps.db,
          req.principal!,
          req.params.projectId,
          req.params.taskId,
          req.body,
          req.id,
          "delete",
        ),
        reply,
        req.id,
      ),
  );
};
