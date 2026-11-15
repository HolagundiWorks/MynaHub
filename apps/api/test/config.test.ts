import { describe, expect, it } from "vitest";
import { ConfigError, loadConfig } from "../src/config.js";

const good = {
  PUBLIC_API_URL: "https://api.company.example",
  DATABASE_URL: "postgresql://myna_runtime:s3cretvalue@ep-x.example/neondb",
};

describe("loadConfig", () => {
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

  it("rejects a bad port and a bad log level", () => {
    expect(() => loadConfig({ ...good, PORT: "70000" })).toThrow(ConfigError);
    expect(() => loadConfig({ ...good, LOG_LEVEL: "chatty" })).toThrow(ConfigError);
  });
});
