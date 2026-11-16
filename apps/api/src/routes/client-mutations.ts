import type { FastifyPluginAsync } from "fastify";
import type { ZodTypeProvider } from "fastify-type-provider-zod";
import { z } from "zod";
import type { AppDeps } from "../app.js";
import { errorBody } from "../errors.js";
import { createGuards } from "../principal.js";
import { PERMISSIONS } from "../authz/catalogue.js";
import { mutateClient, getProjectClient } from "../authz/client-mutations.js";
import {
  clientCreateInput,
  clientEditInput,
  clientDeleteInput,
  projectClientInput,
  projectClientView,
  clientMutationResult,
} from "../client-contract.js";
export const clientMutationRoutes: FastifyPluginAsync<AppDeps> = async (app, deps) => {
  const typed = app.withTypeProvider<ZodTypeProvider>(),
    guards = createGuards(deps);
  const errors = { 400: errorBody, 401: errorBody, 403: errorBody, 404: errorBody, 429: errorBody };
  const response = {
    200: clientMutationResult,
    ...errors,
    409: z.union([errorBody.extend({ conflict: clientMutationResult }), errorBody]),
  };
  const config = { rateLimit: { max: 30, timeWindow: "1 minute" } };
  const params = z.object({ id: z.uuid() });
  const send = async (
    result: Awaited<ReturnType<typeof mutateClient>>,
    reply: import("fastify").FastifyReply,
    requestId: string,
  ) =>
    result.outcome === "conflict"
      ? reply.status(409).send({
          error: {
            code: "VERSION_CONFLICT",
            message: "Review the current record before submitting a new operation.",
            requestId,
          },
          conflict: result,
        })
      : result;
  typed.post(
    "/v1/clients",
    {
      config,
      preHandler: guards.requirePermission(PERMISSIONS.clientsWrite),
      schema: { tags: ["clients"], body: clientCreateInput, response },
    },
    async (req, reply) =>
      send(
        await mutateClient(deps.db, req.principal!, req.body.id, req.body, req.id, "create"),
        reply,
        req.id,
      ),
  );
  typed.patch(
    "/v1/clients/:id",
    {
      config,
      preHandler: guards.requirePermission(PERMISSIONS.clientsWrite),
      schema: { tags: ["clients"], params, body: clientEditInput, response },
    },
    async (req, reply) =>
      send(
        await mutateClient(deps.db, req.principal!, req.params.id, req.body, req.id, "edit"),
        reply,
        req.id,
      ),
  );
  typed.delete(
    "/v1/clients/:id",
    {
      config,
      preHandler: [guards.requirePermission(PERMISSIONS.clientsDelete), guards.requireFresh],
      schema: { tags: ["clients"], params, body: clientDeleteInput, response },
    },
    async (req, reply) =>
      send(
        await mutateClient(deps.db, req.principal!, req.params.id, req.body, req.id, "delete"),
        reply,
        req.id,
      ),
  );
  typed.get(
    "/v1/projects/:id/client",
    {
      preHandler: guards.requirePermission(PERMISSIONS.clientsWrite),
      schema: { tags: ["clients"], params, response: { 200: projectClientView, ...errors } },
    },
    async (req) => getProjectClient(deps.db, req.principal!, req.params.id),
  );
  typed.put(
    "/v1/projects/:id/client",
    {
      config,
      preHandler: guards.requirePermission(PERMISSIONS.clientsWrite),
      schema: { tags: ["clients"], params, body: projectClientInput, response },
    },
    async (req, reply) =>
      send(
        await mutateClient(deps.db, req.principal!, req.params.id, req.body, req.id, "link"),
        reply,
        req.id,
      ),
  );
};
