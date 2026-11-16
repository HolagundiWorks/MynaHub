import { z } from "zod";
export const clientView = z.object({
  id: z.uuid(),
  name: z.string(),
  kind: z.enum(["individual", "company", "architect_firm"]),
  contactPerson: z.string(),
  email: z.string(),
  phone: z.string(),
  address: z.string(),
  correspondenceAddress: z.string(),
  preferredCommunication: z.string(),
  notes: z.string(),
  version: z.number().int().positive(),
});
export const clientPage = z.object({ clients: z.array(clientView).max(25), nextCursor: z.uuid().nullable() });

export const clientFields = clientView.omit({ id: true, version: true }).extend({
  name: z.string().trim().min(1).max(240),
  contactPerson: z.string().trim().max(240),
  email: z.string().trim().max(254),
  phone: z.string().trim().max(80),
  address: z.string().trim().max(2000),
  correspondenceAddress: z.string().trim().max(2000),
  preferredCommunication: z.string().trim().max(120),
  notes: z.string().trim().max(4000),
});
export const clientCreateInput = clientFields.extend({ id: z.uuid(), operationId: z.uuid() }).strict();
export const clientEditInput = clientFields
  .extend({ operationId: z.uuid(), expectedVersion: z.number().int().min(1).max(2147483646) })
  .strict();
export const clientDeleteInput = z
  .object({
    operationId: z.uuid(),
    expectedVersion: z.number().int().min(1).max(2147483646),
    confirmation: z.literal("DELETE"),
  })
  .strict();
export const projectClientView = z.object({
  projectId: z.uuid(),
  clientId: z.uuid().nullable(),
  clientName: z.string().nullable(),
  version: z.number().int().positive(),
});
export const projectClientInput = z
  .object({
    operationId: z.uuid(),
    expectedVersion: z.number().int().min(1).max(2147483646),
    clientId: z.uuid().nullable(),
  })
  .strict();
export const clientMutationResult = z.discriminatedUnion("outcome", [
  z.object({ outcome: z.enum(["created", "applied"]), operationId: z.uuid(), client: clientView }),
  z.object({
    outcome: z.literal("deleted"),
    operationId: z.uuid(),
    id: z.uuid(),
    version: z.number().int().positive(),
    deletedAt: z.iso.datetime(),
  }),
  z.object({
    outcome: z.literal("conflict"),
    operationId: z.uuid(),
    expectedVersion: z.number().int(),
    current: z.union([clientView, projectClientView]),
  }),
  z.object({ outcome: z.literal("linked"), operationId: z.uuid(), project: projectClientView }),
]);
export type ClientMutationResult = z.infer<typeof clientMutationResult>;
export type ClientMutationInput =
  | z.infer<typeof clientCreateInput>
  | z.infer<typeof clientEditInput>
  | z.infer<typeof clientDeleteInput>
  | z.infer<typeof projectClientInput>;
