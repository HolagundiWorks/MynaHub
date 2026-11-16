import { z } from "zod";
const date = z.iso.date().refine((value) => value >= "0001-01-01" && value <= "9999-12-31");
export const siteFields = z.object({
  kind: z.enum(["instruction", "inspection", "snag"]),
  recordDate: date,
  title: z.string().trim().min(1).max(240),
  details: z.string().max(10000),
  location: z.string().trim().max(240),
  assignedTo: z.string().trim().max(240),
  dueDate: date.nullable(),
  status: z.enum(["open", "resolved", "closed"]),
  resolvedDate: date.nullable(),
});
const dates = (v: z.infer<typeof siteFields>) =>
  v.status === "open" ? v.resolvedDate === null : v.resolvedDate !== null && v.resolvedDate >= v.recordDate;
export const siteView = siteFields.extend({
  id: z.uuid(),
  projectId: z.uuid(),
  version: z.number().int().positive(),
});
export const siteCreateInput = siteFields
  .extend({ id: z.uuid(), operationId: z.uuid() })
  .strict()
  .refine(dates, "Check the resolution date and status.");
export const siteEditInput = siteFields
  .extend({ expectedVersion: z.number().int().min(1).max(2147483646), operationId: z.uuid() })
  .strict()
  .refine(dates, "Check the resolution date and status.");
export const siteDeleteInput = z
  .object({
    expectedVersion: z.number().int().min(1).max(2147483646),
    operationId: z.uuid(),
    confirmation: z.literal("DELETE"),
  })
  .strict();
export const sitePage = z.object({
  projectId: z.uuid(),
  records: z.array(siteView).max(25),
  nextCursor: z.uuid().nullable(),
});
export const siteMutationResult = z.discriminatedUnion("outcome", [
  z.object({ outcome: z.enum(["created", "applied"]), operationId: z.uuid(), record: siteView }),
  z.object({
    outcome: z.literal("conflict"),
    operationId: z.uuid(),
    expectedVersion: z.number().int(),
    current: siteView,
  }),
  z.object({
    outcome: z.literal("deleted"),
    operationId: z.uuid(),
    id: z.uuid(),
    projectId: z.uuid(),
    version: z.number().int().positive(),
    deletedAt: z.iso.datetime(),
  }),
]);
export type SiteMutationResult = z.infer<typeof siteMutationResult>;
export type SiteMutationInput =
  z.infer<typeof siteCreateInput> | z.infer<typeof siteEditInput> | z.infer<typeof siteDeleteInput>;
