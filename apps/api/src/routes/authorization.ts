import type { FastifyPluginAsync } from "fastify";
import type { ZodTypeProvider } from "fastify-type-provider-zod";
import { z } from "zod";
import type { AppDeps } from "../app.js";
import { errorBody } from "../errors.js";
import { createGuards } from "../principal.js";
import { PERMISSIONS, PERMISSION_CATALOGUE } from "../authz/catalogue.js";
import { listRoles, saveRole, setMemberRoles } from "../authz/roles.js";
import { getProject, listProjects, setProjectAssignment } from "../authz/projects.js";
import { editProject } from "../authz/project-edits.js";
import { projectView, projectEditInput, projectEditResult } from "../project-edit-contract.js";

const errors = { 400: errorBody, 401: errorBody, 403: errorBody, 404: errorBody, 409: errorBody };
const id = z.uuid();
const roleInput = z.object({
  name: z.string().trim().min(1).max(80),
  description: z.string().trim().max(300).default(""),
  scope: z.enum(["organization", "assigned"]),
  permissions: z.array(z.string().max(80)).max(32),
});
const project = projectView;
export const authorizationRoutes: FastifyPluginAsync<AppDeps> = async (app, deps) => {
  const typed = app.withTypeProvider<ZodTypeProvider>();
  const guards = createGuards(deps);
  const manage = [guards.requirePermission(PERMISSIONS.rolesManage), guards.requireFresh];
  typed.get(
    "/v1/permissions",
    {
      preHandler: guards.requireMember,
      schema: {
        tags: ["permissions"],
        response: {
          200: z.array(z.object({ key: z.string(), description: z.string(), resourceType: z.string() })),
          ...errors,
        },
      },
    },
    async () => PERMISSION_CATALOGUE,
  );
  typed.get(
    "/v1/roles",
    {
      preHandler: guards.requirePermission(PERMISSIONS.rolesRead),
      schema: {
        tags: ["roles"],
        response: {
          200: z.array(
            z.object({
              id,
              organizationId: id,
              name: z.string(),
              description: z.string(),
              scope: z.enum(["organization", "assigned"]),
              isSystemRole: z.boolean(),
              permissions: z.array(z.string()),
            }),
          ),
          ...errors,
        },
      },
    },
    async (req) => listRoles(deps.db, req.principal!.organizationId),
  );
  typed.post(
    "/v1/roles",
    {
      preHandler: manage,
      schema: { tags: ["roles"], body: roleInput, response: { 201: z.object({ id }), ...errors } },
    },
    async (req, reply) => reply.status(201).send(await saveRole(deps.db, req.principal!, req.body, req.id)),
  );
  typed.patch(
    "/v1/roles/:id",
    {
      preHandler: manage,
      schema: {
        tags: ["roles"],
        params: z.object({ id }),
        body: roleInput,
        response: { 200: z.object({ id }), ...errors },
      },
    },
    async (req) => saveRole(deps.db, req.principal!, req.body, req.id, req.params.id),
  );
  typed.put(
    "/v1/members/:id/roles",
    {
      preHandler: manage,
      schema: {
        tags: ["roles"],
        params: z.object({ id }),
        body: z.object({ roleIds: z.array(id).min(1).max(12) }),
        response: { 204: z.null(), ...errors },
      },
    },
    async (req, reply) => {
      await setMemberRoles(deps.db, req.principal!, req.params.id, req.body.roleIds, req.id);
      return reply.status(204).send(null);
    },
  );
  typed.get(
    "/v1/projects",
    {
      preHandler: guards.requirePermission(PERMISSIONS.projectsRead),
      schema: {
        tags: ["projects"],
        querystring: z.object({
          limit: z.coerce.number().int().min(1).max(100).default(50),
          after: id.optional(),
        }),
        response: { 200: z.object({ projects: z.array(project), nextCursor: id.nullable() }), ...errors },
      },
    },
    async (req) => listProjects(deps.db, req.principal!, req.query.limit, req.query.after),
  );
  typed.get(
    "/v1/projects/:id",
    {
      preHandler: guards.requirePermission(PERMISSIONS.projectsRead),
      schema: { tags: ["projects"], params: z.object({ id }), response: { 200: project, ...errors } },
    },
    async (req) => getProject(deps.db, req.principal!, req.params.id),
  );
  typed.put(
    "/v1/projects/:id/assignments/:membershipId",
    {
      preHandler: guards.requirePermission(PERMISSIONS.projectsAssign),
      schema: {
        tags: ["projects"],
        params: z.object({ id, membershipId: id }),
        response: { 204: z.null(), ...errors },
      },
    },
    async (req, reply) => {
      await setProjectAssignment(
        deps.db,
        req.principal!,
        req.params.id,
        req.params.membershipId,
        false,
        req.id,
      );
      return reply.status(204).send(null);
    },
  );
  typed.patch(
    "/v1/projects/:id",
    {
      preHandler: guards.requirePermission(PERMISSIONS.projectsWrite),
      schema: {
        tags: ["projects"],
        params: z.object({ id }),
        body: projectEditInput,
        response: {
          ...errors,
          200: projectEditResult,
          409: z.union([errorBody.extend({ conflict: projectEditResult }), errorBody]),
        },
      },
    },
    async (req, reply) => {
      const result = await editProject(deps.db, req.principal!, req.params.id, req.body, req.id);
      if (result.outcome === "conflict")
        return reply.status(409).send({
          error: {
            code: "VERSION_CONFLICT",
            message: "The project changed. Review both versions before saving again.",
            requestId: req.id,
          },
          conflict: result,
        });
      return reply.send(result);
    },
  );
  typed.delete(
    "/v1/projects/:id/assignments/:membershipId",
    {
      preHandler: guards.requirePermission(PERMISSIONS.projectsAssign),
      schema: {
        tags: ["projects"],
        params: z.object({ id, membershipId: id }),
        response: { 204: z.null(), ...errors },
      },
    },
    async (req, reply) => {
      await setProjectAssignment(
        deps.db,
        req.principal!,
        req.params.id,
        req.params.membershipId,
        true,
        req.id,
      );
      return reply.status(204).send(null);
    },
  );
};
