import {
  S3Client,
  GetBucketAclCommand,
  PutObjectCommand,
  GetObjectCommand,
  DeleteObjectCommand,
} from "@aws-sdk/client-s3";
import type { Config } from "./config.js";
import { AppError } from "./errors.js";
import { MAX_ATTACHMENT_BYTES } from "./attachment-contract.js";
export interface AttachmentStore {
  check(): Promise<void>;
  put(key: string, bytes: Buffer, contentType: string): Promise<void>;
  get(key: string): Promise<Buffer>;
  remove(key: string): Promise<void>;
}
export function createAttachmentStore(config: Config): AttachmentStore | undefined {
  if (!config.ATTACHMENTS_BUCKET) return undefined;
  const client = new S3Client({
    endpoint: config.AWS_ENDPOINT_URL_S3!,
    region: config.AWS_REGION!,
    credentials: { accessKeyId: config.AWS_ACCESS_KEY_ID!, secretAccessKey: config.AWS_SECRET_ACCESS_KEY! },
    forcePathStyle: true,
    maxAttempts: 1,
    requestChecksumCalculation: "WHEN_REQUIRED",
    responseChecksumValidation: "WHEN_REQUIRED",
  });
  const bucket = config.ATTACHMENTS_BUCKET;
  async function safe<T>(run: () => Promise<T>): Promise<T> {
    try {
      return await run();
    } catch {
      throw new AppError(503, "FILE_STORAGE_UNAVAILABLE", "Private attachment storage is unavailable.");
    }
  }
  const check = () =>
    safe(async () => {
      const acl = await client.send(new GetBucketAclCommand({ Bucket: bucket }), {
        abortSignal: AbortSignal.timeout(10000),
      });
      if (acl.Grants?.some((grant) => Boolean(grant.Grantee?.URI)))
        throw new Error("Private storage required");
    });
  return {
    check,
    put: async (key, bytes, contentType) => {
      await check();
      await safe(() =>
        client.send(
          new PutObjectCommand({
            Bucket: bucket,
            Key: key,
            Body: bytes,
            ContentType: contentType,
            CacheControl: "no-store",
            ContentDisposition: "attachment",
          }),
          { abortSignal: AbortSignal.timeout(20000) },
        ),
      );
    },
    get: async (key) => {
      await check();
      return safe(async () => {
        const result = await client.send(
          new GetObjectCommand({ Bucket: bucket, Key: key, Range: `bytes=0-${MAX_ATTACHMENT_BYTES}` }),
          { abortSignal: AbortSignal.timeout(20000) },
        );
        const body = result.Body;
        if (!body) throw new Error("Invalid attachment");
        const chunks: Buffer[] = [];
        let size = 0;
        try {
          if ((result.ContentLength ?? 0) > MAX_ATTACHMENT_BYTES) throw new Error("Invalid attachment");
          for await (const part of body as AsyncIterable<Uint8Array>) {
            size += part.length;
            if (size > MAX_ATTACHMENT_BYTES) throw new Error("Invalid attachment");
            chunks.push(Buffer.from(part));
          }
        } finally {
          if ("destroy" in body && typeof body.destroy === "function") body.destroy();
        }
        return Buffer.concat(chunks, size);
      });
    },
    remove: (key) =>
      safe(async () => {
        await client.send(new DeleteObjectCommand({ Bucket: bucket, Key: key }), {
          abortSignal: AbortSignal.timeout(10000),
        });
      }),
  };
}
