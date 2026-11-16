import type { FastifyPluginAsync } from "fastify";
import type { ZodTypeProvider } from "fastify-type-provider-zod";
import { z } from "zod";
import type { AppDeps } from "../app.js";
import { errorBody } from "../errors.js";
import { createGuards } from "../principal.js";
import { PERMISSIONS } from "../authz/catalogue.js";
import { mutateTask } from "../authz/task-mutations.js";
import { taskCreateInput, taskEditInput, taskMutationResult } from "../task-contract.js";

export const taskRoutes: FastifyPluginAsync<AppDeps> = async (app, deps) => {
  const typed = app.withTypeProvider<ZodTypeProvider>();
  const guards = createGuards(deps);
  const response = {
    200: taskMutationResult,
    400: errorBody,
    401: errorBody,
    403: errorBody,
    404: errorBody,
    409: z.union([errorBody.extend({ conflict: taskMutationResult }), errorBody]),
    429: errorBody,
  };
  const config = { rateLimit: { max: 60, timeWindow: "1 minute" } };
  typed.post(
    "/v1/projects/:projectId/tasks",
    {
      config,
      preHandler: guards.requirePermission(PERMISSIONS.tasksWrite),
      schema: { tags: ["tasks"], params: z.object({ projectId: z.uuid() }), body: taskCreateInput, response },
    },
    async (req) =>
      mutateTask(deps.db, req.principal!, req.params.projectId, req.body.id, req.body, req.id, true),
  );
  typed.patch(
    "/v1/projects/:projectId/tasks/:taskId",
    {
      config,
      preHandler: guards.requirePermission(PERMISSIONS.tasksWrite),
      schema: {
        tags: ["tasks"],
        params: z.object({ projectId: z.uuid(), taskId: z.uuid() }),
        body: taskEditInput,
        response,
      },
    },
    async (req, reply) => {
      const result = await mutateTask(
        deps.db,
        req.principal!,
        req.params.projectId,
        req.params.taskId,
        req.body,
        req.id,
        false,
      );
      if (result.outcome === "conflict")
        return reply.status(409).send({
          error: {
            code: "VERSION_CONFLICT",
            message: "The task changed. Review both versions before saving again.",
            requestId: req.id,
          },
          conflict: result,
        });
      return result;
    },
  );
};
