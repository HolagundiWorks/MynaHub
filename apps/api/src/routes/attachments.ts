import type { FastifyPluginAsync } from "fastify";
import type { ZodTypeProvider } from "fastify-type-provider-zod";
import { z } from "zod";
import type { AppDeps } from "../app.js";
import { AppError, errorBody } from "../errors.js";
import { createGuards } from "../principal.js";
import { PERMISSIONS } from "../authz/catalogue.js";
import { listAttachments, readAttachment, mutateAttachment } from "../authz/attachments.js";
import {
  attachmentUploadInput,
  attachmentDeleteInput,
  attachmentResult,
  attachmentPage,
  attachmentView,
  attachmentTypes,
  MAX_ATTACHMENT_BYTES,
  MAX_ATTACHMENT_BASE64,
} from "../attachment-contract.js";
export const attachmentRoutes: FastifyPluginAsync<AppDeps> = async (app, deps) => {
  const typed = app.withTypeProvider<ZodTypeProvider>(),
    guards = createGuards(deps);
  const errors = {
    400: errorBody,
    401: errorBody,
    403: errorBody,
    404: errorBody,
    413: errorBody,
    429: errorBody,
    503: errorBody,
  };
  const response = {
    200: attachmentResult,
    ...errors,
    409: z.union([errorBody.extend({ conflict: attachmentResult }), errorBody]),
  };
  const config = { rateLimit: { max: 20, timeWindow: "1 minute" } };
  const params = z.object({ projectId: z.uuid(), id: z.uuid() });
  let active = 0;
  const users = new Map<string, number>();
  async function transfer<T>(id: string, run: () => Promise<T>) {
    if (active >= 10 || (users.get(id) ?? 0) >= 2)
      throw new AppError(429, "FILE_TRANSFER_LIMIT", "Wait for current file transfers to finish.");
    active++;
    users.set(id, (users.get(id) ?? 0) + 1);
    try {
      return await run();
    } finally {
      active--;
      const n = (users.get(id) ?? 1) - 1;
      if (n) users.set(id, n);
      else users.delete(id);
    }
  }
  typed.get(
    "/v1/files/status",
    {
      config,
      preHandler: guards.requireMember,
      schema: {
        tags: ["files"],
        response: {
          200: z.object({
            available: z.boolean(),
            maxBytes: z.number().int(),
            contentTypes: z.array(z.enum(attachmentTypes)),
          }),
          ...errors,
        },
      },
    },
    async () => ({
      available: deps.storage
        ? await deps.storage.check().then(
            () => true,
            () => false,
          )
        : false,
      maxBytes: MAX_ATTACHMENT_BYTES,
      contentTypes: [...attachmentTypes],
    }),
  );
  typed.get(
    "/v1/projects/:projectId/files",
    {
      config: { rateLimit: { max: 120, timeWindow: "1 minute" } },
      preHandler: guards.requirePermission(PERMISSIONS.projectsRead),
      schema: {
        tags: ["files"],
        params: z.object({ projectId: z.uuid() }),
        querystring: z.object({ after: z.uuid().optional() }),
        response: { 200: attachmentPage, ...errors },
      },
    },
    async (req) => listAttachments(deps.db, req.principal!, req.params.projectId, req.query.after),
  );
  typed.get(
    "/v1/projects/:projectId/files/:id/content",
    {
      config,
      preHandler: guards.requirePermission(PERMISSIONS.projectsRead),
      schema: {
        tags: ["files"],
        params,
        response: {
          200: z.object({ file: attachmentView, contentBase64: z.string().max(MAX_ATTACHMENT_BASE64) }),
          ...errors,
        },
      },
    },
    async (req) =>
      transfer(req.principal!.userId, () =>
        readAttachment(deps.db, deps.storage, req.principal!, req.params.projectId, req.params.id),
      ),
  );
  typed.post(
    "/v1/projects/:projectId/files",
    {
      config,
      bodyLimit: 7 * 1024 * 1024,
      preHandler: guards.requirePermission(PERMISSIONS.filesWrite),
      schema: {
        tags: ["files"],
        params: z.object({ projectId: z.uuid() }),
        body: attachmentUploadInput,
        response,
      },
    },
    async (req) =>
      transfer(req.principal!.userId, () =>
        mutateAttachment(
          deps.db,
          deps.storage,
          req.principal!,
          req.params.projectId,
          req.body.id,
          req.body,
          req.id,
          "upload",
        ),
      ),
  );
  typed.delete(
    "/v1/projects/:projectId/files/:id",
    {
      config,
      preHandler: [guards.requirePermission(PERMISSIONS.filesDelete), guards.requireFresh],
      schema: { tags: ["files"], params, body: attachmentDeleteInput, response },
    },
    async (req, reply) => {
      const result = await mutateAttachment(
        deps.db,
        deps.storage,
        req.principal!,
        req.params.projectId,
        req.params.id,
        req.body,
        req.id,
        "delete",
      );
      if (result.outcome === "conflict")
        return reply.status(409).send({
          error: {
            code: "VERSION_CONFLICT",
            message: "Review the current file before confirming deletion again.",
            requestId: req.id,
          },
          conflict: result,
        });
      return result;
    },
  );
};
