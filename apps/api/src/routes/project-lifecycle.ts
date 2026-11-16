import type { FastifyPluginAsync } from "fastify";
import type { ZodTypeProvider } from "fastify-type-provider-zod";
import { z } from "zod";
import type { AppDeps } from "../app.js";
import { errorBody } from "../errors.js";
import { createGuards } from "../principal.js";
import { PERMISSIONS } from "../authz/catalogue.js";
import { projectLifecycle } from "../authz/project-lifecycle.js";
import {
  projectCreateInput,
  projectDeleteInput,
  projectLifecycleResult,
} from "../project-lifecycle-contract.js";

export const projectLifecycleRoutes: FastifyPluginAsync<AppDeps> = async (app, deps) => {
  const typed = app.withTypeProvider<ZodTypeProvider>(),
    guards = createGuards(deps);
  const response = {
    200: projectLifecycleResult,
    400: errorBody,
    401: errorBody,
    403: errorBody,
    404: errorBody,
    409: z.union([errorBody.extend({ conflict: projectLifecycleResult }), errorBody]),
    429: errorBody,
  };
  const config = { rateLimit: { max: 30, timeWindow: "1 minute" } };
  typed.post(
    "/v1/projects",
    {
      config,
      preHandler: guards.requirePermission(PERMISSIONS.projectsCreate),
      schema: { tags: ["projects"], body: projectCreateInput, response },
    },
    async (req) => projectLifecycle(deps.db, req.principal!, req.body.id, req.body, req.id, "create"),
  );
  typed.delete(
    "/v1/projects/:id",
    {
      config,
      preHandler: [guards.requirePermission(PERMISSIONS.projectsDelete), guards.requireFresh],
      schema: { tags: ["projects"], params: z.object({ id: z.uuid() }), body: projectDeleteInput, response },
    },
    async (req, reply) => {
      const result = await projectLifecycle(
        deps.db,
        req.principal!,
        req.params.id,
        req.body,
        req.id,
        "delete",
      );
      if (result.outcome === "conflict")
        return reply.status(409).send({
          error: {
            code: "VERSION_CONFLICT",
            message: "The project changed. Review it before confirming deletion again.",
            requestId: req.id,
          },
          conflict: result,
        });
      return result;
    },
  );
};
