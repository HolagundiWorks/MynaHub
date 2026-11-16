import { z } from "zod";
import { projectEditInput, projectView } from "./project-edit-contract.js";

export const projectCreateInput = projectEditInput
  .omit({ expectedVersion: true })
  .extend({ id: z.uuid() })
  .strict();
export const projectDeleteInput = z
  .object({
    operationId: z.uuid(),
    expectedVersion: z.number().int().min(1).max(2147483646),
    confirmation: z.literal("DELETE"),
  })
  .strict();
export const projectLifecycleResult = z.discriminatedUnion("outcome", [
  z.object({ outcome: z.literal("created"), operationId: z.uuid(), project: projectView }),
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
    current: projectView,
  }),
]);
export type ProjectCreateInput = z.infer<typeof projectCreateInput>;
export type ProjectDeleteInput = z.infer<typeof projectDeleteInput>;
export type ProjectLifecycleResult = z.infer<typeof projectLifecycleResult>;
