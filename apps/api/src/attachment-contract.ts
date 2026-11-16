import { z } from "zod";
export const MAX_ATTACHMENT_BYTES = 5 * 1024 * 1024;
export const MAX_ATTACHMENT_BASE64 = Math.ceil(MAX_ATTACHMENT_BYTES / 3) * 4;
export const attachmentTypes = [
  "application/pdf",
  "image/png",
  "image/jpeg",
  "image/webp",
  "text/plain",
  "text/csv",
] as const;
export const attachmentName = z
  .string()
  .trim()
  .min(1)
  .max(180)
  .refine((v) => !/[\x00-\x1f\x7f/\\]/.test(v) && v !== "." && v !== "..");
export const attachmentView = z.object({
  id: z.uuid(),
  projectId: z.uuid(),
  name: attachmentName,
  contentType: z.enum(attachmentTypes),
  size: z.number().int().min(1).max(MAX_ATTACHMENT_BYTES),
  sha256: z.string().regex(/^[a-f0-9]{64}$/),
  version: z.number().int().positive(),
});
export const attachmentPage = z.object({
  projectId: z.uuid(),
  files: z.array(attachmentView).max(25),
  nextCursor: z.uuid().nullable(),
});
export const attachmentUploadInput = z
  .object({
    id: z.uuid(),
    operationId: z.uuid(),
    name: attachmentName,
    contentType: z.enum(attachmentTypes),
    contentBase64: z
      .string()
      .min(4)
      .max(MAX_ATTACHMENT_BASE64)
      .refine(
        (value) =>
          value.length <= MAX_ATTACHMENT_BASE64 &&
          value.length % 4 === 0 &&
          /^[A-Za-z0-9+/]*={0,2}$/.test(value),
      ),
  })
  .strict();
export const attachmentDeleteInput = z
  .object({
    operationId: z.uuid(),
    expectedVersion: z.number().int().min(1).max(2147483646),
    confirmation: z.literal("DELETE"),
  })
  .strict();
export const attachmentResult = z.discriminatedUnion("outcome", [
  z.object({ outcome: z.literal("uploaded"), operationId: z.uuid(), file: attachmentView }),
  z.object({
    outcome: z.literal("conflict"),
    operationId: z.uuid(),
    expectedVersion: z.number().int(),
    current: attachmentView,
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
export type AttachmentResult = z.infer<typeof attachmentResult>;
export type AttachmentUploadInput = z.infer<typeof attachmentUploadInput>;
export type AttachmentDeleteInput = z.infer<typeof attachmentDeleteInput>;
