import { PutObjectCommand, GetObjectCommand, DeleteObjectCommand, S3Client } from "@aws-sdk/client-s3";

export function storageBucket(): string {
  return process.env.S3_BUCKET?.trim() || "uploads";
}

export function createStorageClient(): S3Client {
  const endpoint = process.env.AWS_ENDPOINT_URL_S3;
  const region = process.env.AWS_REGION;
  const accessKeyId = process.env.AWS_ACCESS_KEY_ID;
  const secretAccessKey = process.env.AWS_SECRET_ACCESS_KEY;
  if (!endpoint || !region || !accessKeyId || !secretAccessKey) {
    throw new Error("Object storage env vars missing (AWS_*). Run neon deploy and use .env.local.");
  }
  return new S3Client({
    region,
    endpoint,
    forcePathStyle: true,
    credentials: { accessKeyId, secretAccessKey },
  });
}

export async function verifyObjectStorage(client: S3Client): Promise<void> {
  const bucket = storageBucket();
  const key = `healthcheck/${Date.now()}.txt`;
  const body = "o-studio neon object storage ok";
  await client.send(
    new PutObjectCommand({ Bucket: bucket, Key: key, Body: body, ContentType: "text/plain" }),
  );
  const got = await client.send(new GetObjectCommand({ Bucket: bucket, Key: key }));
  const text = await got.Body?.transformToString();
  if (text !== body) throw new Error("Object storage round-trip mismatch");
  await client.send(new DeleteObjectCommand({ Bucket: bucket, Key: key }));
  console.log(`[object-storage] ok — bucket "${bucket}" read/write`);
}

/** Key for a synced `.osp` snapshot at a revision (used by Kivo push). */
export function ospSnapshotKey(projectId: string, revision: number): string {
  return `projects/${projectId}/r${revision}.osp`;
}
