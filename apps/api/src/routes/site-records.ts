import type { FastifyPluginAsync } from "fastify";
import type { ZodTypeProvider } from "fastify-type-provider-zod";
import { z } from "zod";
import type { AppDeps } from "../app.js";
import { errorBody } from "../errors.js";
import { createGuards } from "../principal.js";
import { PERMISSIONS } from "../authz/catalogue.js";
import { listSiteRecords, mutateSiteRecord } from "../authz/site-records.js";
import {
  siteCreateInput,
  siteEditInput,
  siteDeleteInput,
  siteMutationResult,
  sitePage,
} from "../site-contract.js";
export const siteRecordRoutes: FastifyPluginAsync<AppDeps> = async (app, deps) => {
  const typed = app.withTypeProvider<ZodTypeProvider>(),
    guards = createGuards(deps);
  const errors = { 400: errorBody, 401: errorBody, 403: errorBody, 404: errorBody, 429: errorBody };
  const response = {
    200: siteMutationResult,
    ...errors,
    409: z.union([errorBody.extend({ conflict: siteMutationResult }), errorBody]),
  };
  const config = { rateLimit: { max: 60, timeWindow: "1 minute" } };
  const params = z.object({ projectId: z.uuid(), id: z.uuid() });
  const send = async (
    result: Awaited<ReturnType<typeof mutateSiteRecord>>,
    reply: import("fastify").FastifyReply,
    requestId: string,
  ) =>
    result.outcome === "conflict"
      ? reply.status(409).send({
          error: {
            code: "VERSION_CONFLICT",
            message: "Review the current site record before submitting a new operation.",
            requestId,
          },
          conflict: result,
        })
      : result;
  typed.get(
    "/v1/projects/:projectId/site-records",
    {
      config,
      preHandler: guards.requirePermission(PERMISSIONS.projectsRead),
      schema: {
        tags: ["site records"],
        params: z.object({ projectId: z.uuid() }),
        querystring: z.object({ after: z.uuid().optional() }),
        response: { 200: sitePage, ...errors },
      },
    },
    async (req) => listSiteRecords(deps.db, req.principal!, req.params.projectId, req.query.after),
  );
  typed.post(
    "/v1/projects/:projectId/site-records",
    {
      config,
      preHandler: guards.requirePermission(PERMISSIONS.sitesWrite),
      schema: {
        tags: ["site records"],
        params: z.object({ projectId: z.uuid() }),
        body: siteCreateInput,
        response,
      },
    },
    async (req, reply) =>
      send(
        await mutateSiteRecord(
          deps.db,
          req.principal!,
          req.params.projectId,
          req.body.id,
          req.body,
          req.id,
          "create",
        ),
        reply,
        req.id,
      ),
  );
  typed.patch(
    "/v1/projects/:projectId/site-records/:id",
    {
      config,
      preHandler: guards.requirePermission(PERMISSIONS.sitesWrite),
      schema: { tags: ["site records"], params, body: siteEditInput, response },
    },
    async (req, reply) =>
      send(
        await mutateSiteRecord(
          deps.db,
          req.principal!,
          req.params.projectId,
          req.params.id,
          req.body,
          req.id,
          "edit",
        ),
        reply,
        req.id,
      ),
  );
  typed.delete(
    "/v1/projects/:projectId/site-records/:id",
    {
      config,
      preHandler: [guards.requirePermission(PERMISSIONS.sitesDelete), guards.requireFresh],
      schema: { tags: ["site records"], params, body: siteDeleteInput, response },
    },
    async (req, reply) =>
      send(
        await mutateSiteRecord(
          deps.db,
          req.principal!,
          req.params.projectId,
          req.params.id,
          req.body,
          req.id,
          "delete",
        ),
        reply,
        req.id,
      ),
  );
};
