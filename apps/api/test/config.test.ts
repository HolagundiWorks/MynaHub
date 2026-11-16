import { describe, expect, it } from "vitest";
import { ConfigError, loadConfig, loadRuntimeConfig } from "../src/config.js";

const good = {
  PUBLIC_API_URL: "https://api.company.example",
  DATABASE_URL: "postgresql://myna_runtime:s3cretvalue@ep-x.example/neondb",
  BETTER_AUTH_SECRET: "0123456789abcdef0123456789abcdef0123",
};

describe("loadConfig", () => {
  it("keeps migration credentials available to setup but refuses them in the serving process", () => {
    const owner = "postgresql://owner:private-owner-value@ep-x.example/neondb";
    expect(loadConfig({ ...good, DIRECT_DATABASE_URL: owner }).DIRECT_DATABASE_URL).toBe(owner);
    for (const value of [owner, ""]) {
      expect(() => loadRuntimeConfig({ ...good, DIRECT_DATABASE_URL: value })).toThrow(/DIRECT_DATABASE_URL/);
      try {
        loadRuntimeConfig({ ...good, DIRECT_DATABASE_URL: value });
      } catch (error) {
        expect((error as Error).message).not.toContain("private-owner-value");
      }
    }
    expect(loadRuntimeConfig(good).DATABASE_URL).toBe(good.DATABASE_URL);
  });
  it("accepts a minimal valid environment and applies defaults", () => {
    const c = loadConfig(good);
    expect(c.PORT).toBe(3000);
    expect(c.LOG_LEVEL).toBe("info");
    expect(c.NODE_ENV).toBe("production");
    expect(c.DIRECT_DATABASE_URL).toBeUndefined();
  });

  it("is frozen so nothing can change it after start", () => {
    expect(Object.isFrozen(loadConfig(good))).toBe(true);
  });

  it("names every missing variable and refuses to start", () => {
    try {
      loadConfig({});
      expect.unreachable();
    } catch (e) {
      expect(e).toBeInstanceOf(ConfigError);
      const text = (e as ConfigError).message;
      expect(text).toContain("PUBLIC_API_URL");
      expect(text).toContain("DATABASE_URL");
    }
  });

  it("never echoes a secret value in its error", () => {
    try {
      loadConfig({ ...good, DATABASE_URL: "mysql://admin:hunter2-very-secret@host/db" });
      expect.unreachable();
    } catch (e) {
      expect((e as ConfigError).message).toContain("DATABASE_URL");
      expect((e as ConfigError).message).not.toContain("hunter2");
    }
  });

  it("requires https in production but allows http for development", () => {
    expect(() => loadConfig({ ...good, PUBLIC_API_URL: "http://api.company.example" })).toThrow(/https/);
    expect(
      loadConfig({ ...good, NODE_ENV: "development", PUBLIC_API_URL: "http://localhost:3000" }).NODE_ENV,
    ).toBe("development");
  });

  it("requires a signing secret of at least 32 characters and never echoes it", () => {
    expect(() => loadConfig({ ...good, BETTER_AUTH_SECRET: undefined })).toThrow(/BETTER_AUTH_SECRET/);
    try {
      loadConfig({ ...good, BETTER_AUTH_SECRET: "too-short-secret" });
      expect.unreachable();
    } catch (e) {
      expect((e as ConfigError).message).toContain("BETTER_AUTH_SECRET");
      expect((e as ConfigError).message).not.toContain("too-short-secret");
    }
  });

  it("refuses the placeholder secret copied from .env.example", () => {
    expect(() => loadConfig({ ...good, BETTER_AUTH_SECRET: "CHANGE_ME_TO_A_LONG_RANDOM_VALUE" })).toThrow(
      /placeholder/,
    );
  });

  it("reads trusted origins as a list and requires administrator MFA by default", () => {
    const c = loadConfig({
      ...good,
      TRUSTED_ORIGINS: "https://hub.company.example, https://m.company.example,",
    });
    expect(c.TRUSTED_ORIGINS).toEqual(["https://hub.company.example", "https://m.company.example"]);
    expect(loadConfig(good).TRUSTED_ORIGINS).toEqual([]);
    expect(loadConfig(good).REQUIRE_ADMIN_MFA).toBe(true);
    expect(loadConfig({ ...good, REQUIRE_ADMIN_MFA: "false" }).REQUIRE_ADMIN_MFA).toBe(false);
  });

  it("rejects a bad port and a bad log level", () => {
    expect(() => loadConfig({ ...good, PORT: "70000" })).toThrow(ConfigError);
    expect(() => loadConfig({ ...good, LOG_LEVEL: "chatty" })).toThrow(ConfigError);
  });
});
