import { beforeEach, describe, expect, it, vi } from "vitest";
import { createAttachmentStore } from "../src/attachment-store.js";
import { loadConfig } from "../src/config.js";
import { MAX_ATTACHMENT_BYTES } from "../src/attachment-contract.js";
const { send } = vi.hoisted(() => ({ send: vi.fn() }));
vi.mock("@aws-sdk/client-s3", () => {
  class Command {
    constructor(public input: unknown) {}
  }
  return {
    S3Client: class {
      send = send;
    },
    GetBucketAclCommand: Command,
    PutObjectCommand: Command,
    GetObjectCommand: Command,
    DeleteObjectCommand: Command,
  };
});
const config = () =>
  loadConfig({
    PUBLIC_API_URL: "https://api.company.example",
    DATABASE_URL: "postgresql://unused/unused",
    BETTER_AUTH_SECRET: "test-storage-secret-test-storage-secret",
    ATTACHMENTS_BUCKET: "mynahub-attachments",
    AWS_ENDPOINT_URL_S3: "https://br-preview.storage.c-1.ap-southeast-1.aws.neon.tech",
    AWS_REGION: "ap-southeast-1",
    AWS_ACCESS_KEY_ID: "fixture-key",
    AWS_SECRET_ACCESS_KEY: "fixture-secret",
  });
beforeEach(() => send.mockReset());
describe("private bounded object storage", () => {
  it("refuses public ACL grants before any object upload or download", async () => {
    send.mockResolvedValue({
      Grants: [{ Grantee: { URI: "http://acs.amazonaws.com/groups/global/AllUsers" } }],
    });
    const store = createAttachmentStore(config())!;
    await expect(store.put("key", Buffer.from("data"), "text/plain")).rejects.toMatchObject({
      code: "FILE_STORAGE_UNAVAILABLE",
    });
    expect(send).toHaveBeenCalledTimes(1);
  });
  it("closes oversized object streams even when content length rejects them before reading", async () => {
    const destroy = vi.fn();
    const body = {
      destroy,
      async *[Symbol.asyncIterator]() {
        yield Buffer.from("data");
      },
    };
    send
      .mockResolvedValueOnce({ Grants: [] })
      .mockResolvedValueOnce({ ContentLength: MAX_ATTACHMENT_BYTES + 1, Body: body });
    await expect(createAttachmentStore(config())!.get("key")).rejects.toMatchObject({
      code: "FILE_STORAGE_UNAVAILABLE",
    });
    expect(destroy).toHaveBeenCalledOnce();
    expect(send.mock.calls[1]?.[0].input.Range).toBe("bytes=0-" + MAX_ATTACHMENT_BYTES);
  });
  it("bounds streamed bytes when length is missing and closes the stream on rejection", async () => {
    const destroy = vi.fn();
    const body = {
      destroy,
      async *[Symbol.asyncIterator]() {
        yield Buffer.alloc(MAX_ATTACHMENT_BYTES);
        yield Buffer.from("x");
      },
    };
    send.mockResolvedValueOnce({ Grants: [] }).mockResolvedValueOnce({ Body: body });
    await expect(createAttachmentStore(config())!.get("key")).rejects.toMatchObject({
      code: "FILE_STORAGE_UNAVAILABLE",
    });
    expect(destroy).toHaveBeenCalledOnce();
  });
  it("rejects incomplete or non-Neon storage configuration without exposing secrets", () => {
    const base = {
      PUBLIC_API_URL: "https://api.company.example",
      DATABASE_URL: "postgresql://unused/unused",
      BETTER_AUTH_SECRET: "test-storage-secret-test-storage-secret",
      ATTACHMENTS_BUCKET: "mynahub-attachments",
      AWS_SECRET_ACCESS_KEY: "private-fixture-value",
    };
    try {
      loadConfig(base);
      expect.unreachable();
    } catch (error) {
      expect(String(error)).toContain("AWS_ENDPOINT_URL_S3");
      expect(String(error)).not.toContain("private-fixture-value");
    }
    expect(() =>
      loadConfig({
        ...base,
        AWS_ENDPOINT_URL_S3: "http://127.0.0.1:1234",
        AWS_REGION: "ap-southeast-1",
        AWS_ACCESS_KEY_ID: "fixture",
      }),
    ).toThrow();
  });
});
