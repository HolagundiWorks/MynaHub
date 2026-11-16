import { z } from "zod";
import { taskView } from "./task-contract.js";
export const taskActionInput = z.object({
  operationId: z.uuid(),
  expectedVersion: z.number().int().min(1).max(2147483646),
});
export const taskDeleteInput = taskActionInput.extend({ confirmation: z.literal("DELETE") }).strict();
export const taskAssignInput = taskActionInput
  .extend({
    membershipIds: z
      .array(z.uuid())
      .max(20)
      .refine((ids) => new Set(ids).size === ids.length),
  })
  .strict();
export const taskActionResult = z.discriminatedUnion("outcome", [
  z.object({ outcome: z.literal("assigned"), operationId: z.uuid(), task: taskView }),
  z.object({
    outcome: z.literal("deleted"),
    operationId: z.uuid(),
    id: z.uuid(),
    projectId: z.uuid(),
    version: z.number().int().positive(),
    deletedAt: z.iso.datetime(),
  }),
  z.object({
    outcome: z.literal("conflict"),
    operationId: z.uuid(),
    expectedVersion: z.number().int(),
    current: taskView,
  }),
]);
export type TaskActionResult = z.infer<typeof taskActionResult>;
export type TaskActionInput = z.infer<typeof taskAssignInput> | z.infer<typeof taskDeleteInput>;
export const taskCandidatePage = z.object({
  members: z.array(z.object({ membershipId: z.uuid(), name: z.string() })).max(25),
  nextCursor: z.uuid().nullable(),
});
