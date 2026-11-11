import { describe, expect, it, vi } from "vitest";
import {
  loadRuntimeConfig,
  parseRuntimeConfig,
  runtimeConfig,
  type RuntimeConfig
} from "./runtimeConfig";

const validConfig: RuntimeConfig = {
  apiBaseUrl: "https://api.example.test",
  appDomain: "apps.example.test",
  sourceUrl: "https://github.com/muniventures/minicloud",
  auth: {
    origin: "https://api.example.test",
    capabilitiesUrl: "https://api.example.test/api/account/capabilities"
  }
};

describe("parseRuntimeConfig", () => {
  it("accepts a valid public runtime configuration", () => {
    expect(parseRuntimeConfig(validConfig)).toEqual(validConfig);
  });

  it("allows local HTTP configuration for development", () => {
    expect(
      parseRuntimeConfig({
        ...validConfig,
        apiBaseUrl: "http://localhost:3210",
        auth: {
          origin: "http://localhost:3210",
          capabilitiesUrl: "http://localhost:3210/api/account/capabilities"
        },
        sourceUrl: "http://localhost:4000/source"
      }).apiBaseUrl
    ).toBe("http://localhost:3210");
  });

  it.each([
    ["unknown fields", { ...validConfig, firebase: {} }],
    ["API paths", { ...validConfig, apiBaseUrl: "https://api.example.test/api" }],
    ["insecure public API origins", { ...validConfig, apiBaseUrl: "http://api.example.test" }],
    ["mismatched auth origins", { ...validConfig, auth: { ...validConfig.auth, origin: "https://other.example.test" } }],
    ["incorrect capabilities endpoints", { ...validConfig, auth: { ...validConfig.auth, capabilitiesUrl: "https://api.example.test/api/me" } }],
    ["app-domain paths", { ...validConfig, appDomain: "apps.example.test/path" }],
    ["source URL credentials", { ...validConfig, sourceUrl: "https://user:password@example.test/repo" }],
    ["insecure public source URLs", { ...validConfig, sourceUrl: "http://example.test/repo" }]
  ])("rejects %s", (_description, config) => {
    expect(() => parseRuntimeConfig(config)).toThrow();
  });
});

describe("loadRuntimeConfig", () => {
  it("keeps the localhost Vite development configuration without fetching a file", async () => {
    const fetchConfig = vi.fn();
    const config = await loadRuntimeConfig(fetchConfig, true);

    expect(fetchConfig).not.toHaveBeenCalled();
    expect(config.apiBaseUrl).toBe("http://localhost:3210");
    expect(config.auth.capabilitiesUrl).toBe("http://localhost:3210/api/account/capabilities");
    expect(runtimeConfig.apiBaseUrl).toBe(config.apiBaseUrl);
    expect(runtimeConfig.appDomain).toBe(config.appDomain);
  });

  it("loads and validates the startup-generated configuration", async () => {
    const fetchConfig = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(validConfig), { status: 200 })
    );

    await expect(loadRuntimeConfig(fetchConfig, false)).resolves.toEqual(validConfig);
    expect(fetchConfig).toHaveBeenCalledWith("/runtime-config.json", { cache: "no-store" });
    expect(runtimeConfig.apiBaseUrl).toBe(validConfig.apiBaseUrl);
  });

  it("rejects unavailable or malformed runtime configuration", async () => {
    await expect(
      loadRuntimeConfig(
        vi.fn().mockResolvedValue(new Response("", { status: 503 })),
        false
      )
    ).rejects.toThrow("503");

    await expect(
      loadRuntimeConfig(
        vi.fn().mockResolvedValue(new Response("{", { status: 200 })),
        false
      )
    ).rejects.toThrow("not valid JSON");
  });
});
