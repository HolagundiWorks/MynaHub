import type { FastifyPluginAsync } from "fastify";
import type { ZodTypeProvider } from "fastify-type-provider-zod";
import { z } from "zod";
import type { AppDeps } from "../app.js";
import { errorBody } from "../errors.js";
import { PERMISSIONS } from "../authz/catalogue.js";
import { createGuards } from "../principal.js";
import { listClients, getClient } from "../authz/clients.js";
import { clientView, clientPage } from "../client-contract.js";
export const clientRoutes: FastifyPluginAsync<AppDeps> = async (app, deps) => {
  const typed = app.withTypeProvider<ZodTypeProvider>(),
    guards = createGuards(deps);
  const response = { 400: errorBody, 401: errorBody, 403: errorBody, 404: errorBody };
  typed.get(
    "/v1/clients",
    {
      preHandler: guards.requirePermission(PERMISSIONS.clientsRead),
      schema: {
        tags: ["clients"],
        querystring: z.object({ after: z.uuid().optional() }),
        response: { 200: clientPage, ...response },
      },
    },
    async (req) => listClients(deps.db, req.principal!, req.query.after),
  );
  typed.get(
    "/v1/clients/:id",
    {
      preHandler: guards.requirePermission(PERMISSIONS.clientsRead),
      schema: {
        tags: ["clients"],
        params: z.object({ id: z.uuid() }),
        response: { 200: clientView, ...response },
      },
    },
    async (req) => getClient(deps.db, req.principal!, req.params.id),
  );
};
