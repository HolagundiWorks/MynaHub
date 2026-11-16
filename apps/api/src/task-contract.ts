import { z } from "zod";

export const taskFields = {
  title: z.string().trim().min(1).max(240),
  description: z.string().max(10000),
  dueAt: z.iso
    .datetime({ offset: true })
    .refine((value) => {
      const year = new Date(value).getUTCFullYear();
      return year >= 1 && year <= 9999;
    })
    .nullable(),
  status: z.enum(["open", "in_progress", "completed", "cancelled"]),
  priority: z.enum(["low", "normal", "high", "urgent"]),
};
export const taskView = z.object({
  id: z.uuid(),
  projectId: z.uuid(),
  ...taskFields,
  version: z.number().int().positive(),
  assignees: z
    .array(z.object({ membershipId: z.uuid(), name: z.string() }))
    .max(20)
    .default([]),
});
export const taskCreateInput = z
  .object({
    operationId: z.uuid(),
    id: z.uuid(),
    ...taskFields,
  })
  .strict();
export const taskEditInput = z
  .object({
    operationId: z.uuid(),
    expectedVersion: z.number().int().min(1).max(2147483646),
    ...taskFields,
  })
  .strict();
export const taskMutationResult = z.discriminatedUnion("outcome", [
  z.object({ outcome: z.literal("applied"), operationId: z.uuid(), task: taskView }),
  z.object({
    outcome: z.literal("conflict"),
    operationId: z.uuid(),
    expectedVersion: z.number().int(),
    submitted: z.object(taskFields),
    current: taskView,
  }),
]);
export type TaskCreateInput = z.infer<typeof taskCreateInput>;
export type TaskEditInput = z.infer<typeof taskEditInput>;
export type TaskMutationResult = z.infer<typeof taskMutationResult>;
