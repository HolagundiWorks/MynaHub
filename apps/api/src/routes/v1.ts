import type { FastifyPluginAsync } from "fastify";
import type { ZodTypeProvider } from "fastify-type-provider-zod";
import { z } from "zod";
import type { AppDeps } from "../app.js";
import { completeBootstrap } from "../bootstrap.js";
import { errorBody } from "../errors.js";
import { acceptInvitation, createInvitation, listInvitations, revokeInvitation } from "../invitations.js";
import { listMembers, reactivateMember, suspendMember } from "../members.js";
import { getOrganization, PERMISSIONS, roleNames } from "../organization.js";
import { createGuards } from "../principal.js";

const password = z.string().min(12, "at least 12 characters").max(128);
const email = z.email().max(254);
const uuid = z.uuid();
const errors = { 400: errorBody, 401: errorBody, 403: errorBody, 404: errorBody, 409: errorBody };

export const v1Routes: FastifyPluginAsync<AppDeps> = async (app, deps) => {
  const { db, auth, config } = deps;
  const typed = app.withTypeProvider<ZodTypeProvider>();
  const guards = createGuards({ auth, db, config });
  // Public routes that create accounts are rate limited per client address; tests switch the limit off.
  const limited = {
    rateLimit: config.NODE_ENV === "test" ? false : { max: 10, timeWindow: "1 minute" },
  } as const;

  // ---- one-time setup ------------------------------------------------------------------------------------------
  typed.post(
    "/v1/bootstrap",
    {
      config: limited,
      schema: {
        tags: ["setup"],
        summary: "Create the company and its first administrator with the one-time setup token",
        body: z.object({
          setupToken: z.string().min(20).max(200),
          organizationName: z.string().trim().min(1).max(120),
          slug: z.string().regex(/^[a-z0-9][a-z0-9-]{1,58}[a-z0-9]$/),
          adminName: z.string().trim().min(1).max(120),
          adminEmail: email,
          adminPassword: password,
        }),
        response: { 201: z.object({ organizationId: z.string(), userId: z.string() }), ...errors },
      },
    },
    async (req, reply) => {
      const r = await completeBootstrap({ db, auth }, req.body, req.id);
      return reply.status(201).send({ organizationId: r.organizationId, userId: r.userId });
    },
  );

  // ---- the signed-in person ------------------------------------------------------------------------------------
  const meSchema = z.object({
    user: z.object({ id: z.string(), name: z.string(), email: z.string(), twoFactorEnabled: z.boolean() }),
    organization: z.object({ id: z.string(), name: z.string() }),
    membership: z.object({ id: z.string() }),
    roles: z.array(z.string()),
    permissions: z.array(z.string()),
  });
  typed.get(
    "/v1/me",
    { preHandler: guards.requireMember, schema: { tags: ["me"], response: { 200: meSchema, ...errors } } },
    async (req) => {
      const p = req.principal!;
      const org = (await getOrganization(db))!;
      return {
        user: { id: p.userId, name: p.name, email: p.email, twoFactorEnabled: p.twoFactorEnabled },
        organization: { id: org.id, name: org.name },
        membership: { id: p.membershipId },
        roles: await roleNames(db, p.membershipId),
        permissions: [...p.permissions].sort(),
      };
    },
  );
  typed.get(
    "/v1/me/permissions",
    {
      preHandler: guards.requireMember,
      schema: { tags: ["me"], response: { 200: z.object({ permissions: z.array(z.string()) }), ...errors } },
    },
    async (req) => ({ permissions: [...req.principal!.permissions].sort() }),
  );

  // ---- invitations (administrators) ----------------------------------------------------------------------------
  const manage = guards.requirePermission(PERMISSIONS.membersManage);
  typed.post(
    "/v1/invitations",
    {
      preHandler: manage,
      schema: {
        tags: ["invitations"],
        summary: "Invite an employee. The token is shown once; share it with the person.",
        body: z.object({ email, roleId: uuid }),
        response: {
          201: z.object({ invitationId: z.string(), token: z.string(), expiresAt: z.string() }),
          ...errors,
        },
      },
    },
    async (req, reply) => {
      const r = await createInvitation(db, req.principal!, req.body, req.id);
      return reply
        .status(201)
        .send({ invitationId: r.invitationId, token: r.token, expiresAt: r.expiresAt.toISOString() });
    },
  );
  typed.get(
    "/v1/invitations",
    {
      preHandler: manage,
      schema: {
        tags: ["invitations"],
        response: {
          200: z.array(
            z.object({
              id: z.string(),
              email: z.string(),
              role: z.string(),
              status: z.string(),
              createdAt: z.string(),
              expiresAt: z.string(),
            }),
          ),
          ...errors,
        },
      },
    },
    async (req) =>
      (await listInvitations(db, req.principal!.organizationId)).map((i) => ({
        ...i,
        createdAt: i.createdAt.toISOString(),
        expiresAt: i.expiresAt.toISOString(),
      })),
  );
  typed.post(
    "/v1/invitations/:id/revoke",
    {
      preHandler: manage,
      schema: {
        tags: ["invitations"],
        params: z.object({ id: uuid }),
        response: { 204: z.null(), ...errors },
      },
    },
    async (req, reply) => {
      await revokeInvitation(db, req.principal!, req.params.id, req.id);
      return reply.status(204).send(null);
    },
  );
  typed.post(
    "/v1/invitations/accept",
    {
      config: limited,
      schema: {
        tags: ["invitations"],
        summary: "Accept an invitation: creates the account and the membership",
        body: z.object({
          token: z.string().min(20).max(200),
          name: z.string().trim().min(1).max(120),
          password,
        }),
        response: { 201: z.object({ userId: z.string(), email: z.string() }), ...errors },
      },
    },
    async (req, reply) => {
      const r = await acceptInvitation({ db, auth }, req.body, req.id);
      return reply.status(201).send({ userId: r.userId, email: r.email });
    },
  );

  // ---- members (administrators) --------------------------------------------------------------------------------
  typed.get(
    "/v1/members",
    {
      preHandler: manage,
      schema: {
        tags: ["members"],
        response: {
          200: z.array(
            z.object({
              membershipId: z.string(),
              userId: z.string(),
              status: z.string(),
              name: z.string(),
              email: z.string(),
              roles: z.array(z.string()),
            }),
          ),
          ...errors,
        },
      },
    },
    async (req) => listMembers(db, req.principal!.organizationId),
  );
  const memberParams = z.object({ id: uuid });
  typed.post(
    "/v1/members/:id/suspend",
    {
      preHandler: [manage, guards.requireFresh],
      schema: { tags: ["members"], params: memberParams, response: { 204: z.null(), ...errors } },
    },
    async (req, reply) => {
      await suspendMember({ db, auth }, req.principal!, req.params.id, req.id);
      return reply.status(204).send(null);
    },
  );
  typed.post(
    "/v1/members/:id/reactivate",
    {
      preHandler: [manage, guards.requireFresh],
      schema: { tags: ["members"], params: memberParams, response: { 204: z.null(), ...errors } },
    },
    async (req, reply) => {
      await reactivateMember(db, req.principal!, req.params.id, req.id);
      return reply.status(204).send(null);
    },
  );
};
