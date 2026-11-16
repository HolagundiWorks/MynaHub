import { z } from "zod";

export const projectView = z.object({
  id: z.uuid(),
  name: z.string(),
  code: z.string(),
  status: z.enum(["active", "on_hold", "completed", "archived"]),
  version: z.number().int().positive(),
});
export const projectEditInput = z
  .object({
    operationId: z.uuid(),
    expectedVersion: z.number().int().min(1).max(2147483646),
    name: z.string().trim().min(1).max(240),
    code: z.string().trim().max(80),
    status: projectView.shape.status,
  })
  .strict();
export const projectEditResult = z.discriminatedUnion("outcome", [
  z.object({ outcome: z.literal("applied"), operationId: z.uuid(), project: projectView }),
  z.object({
    outcome: z.literal("conflict"),
    operationId: z.uuid(),
    expectedVersion: z.number().int(),
    submitted: projectEditInput.pick({ name: true, code: true, status: true }),
    current: projectView,
  }),
]);
export type ProjectEditInput = z.infer<typeof projectEditInput>;
export type ProjectEditResult = z.infer<typeof projectEditResult>;
