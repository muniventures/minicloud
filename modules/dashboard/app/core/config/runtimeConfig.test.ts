import { describe, expect, it, vi } from "vitest";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
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
    ["insecure public source URLs", { ...validConfig, sourceUrl: "http://example.test/repo" }],
    ["private identity configuration", { ...validConfig, auth: { ...validConfig.auth, adminOrigin: "http://kratos:4434" } }],
    ["private secrets configuration", { ...validConfig, openBao: { token: "synthetic-private-token" } }]
  ])("rejects %s", (_description, config) => {
    expect(() => parseRuntimeConfig(config)).toThrow();
  });

  describe("dashboard startup", () => {
    function runStartup(environment: Record<string, string | undefined>) {
      const artifacts = resolve(process.cwd(), "../../.local/artifacts");
      mkdirSync(artifacts, { recursive: true });
      const fixture = mkdtempSync(join(artifacts, "dashboard-startup-"));
      try {
        const bin = join(fixture, "bin");
        mkdirSync(bin);
        writeFileSync(
          join(bin, "nginx"),
          '#!/bin/sh\nprintf "%s\\n" "$@" > "$NGINX_ARGV_FILE"\n',
          { mode: 0o700 }
        );
        const argvFile = join(fixture, "nginx-argv");
        const configFile = join(fixture, "runtime-config.json");
        const result = spawnSync(
          process.execPath,
          [resolve(process.cwd(), "dashboard-startup.mjs")],
          {
            encoding: "utf8",
            env: {
              ...process.env,
              MINICLOUD_API_URL: "https://api.example.test",
              MINICLOUD_APP_DOMAIN: "apps.example.test",
              MINICLOUD_SOURCE_URL: "https://github.com/muniventures/minicloud",
              ...environment,
              MINICLOUD_DASHBOARD_HTML_ROOT: fixture,
              NGINX_ARGV_FILE: argvFile,
              PATH: `${bin}:${process.env.PATH}`
            },
            timeout: 10000
          }
        );
        return {
          status: result.status,
          output: `${result.stdout}${result.stderr}`,
          config: existsSync(configFile) ? JSON.parse(readFileSync(configFile, "utf8")) : undefined,
          serverStarted: existsSync(argvFile)
        };
      } finally {
        rmSync(fixture, { recursive: true, force: true });
      }
    }

    it("uses the same startup executable for unrelated installations and excludes private inputs", () => {
      for (const [apiBaseUrl, appDomain] of [
        ["https://api.alpha.example", "apps.alpha.example"],
        ["https://control.beta.test:8443", "workloads.beta.test"]
      ]) {
        const result = runStartup({
          MINICLOUD_API_URL: apiBaseUrl,
          MINICLOUD_APP_DOMAIN: appDomain,
          OpenBao__Address: "https://openbao:8200",
          OpenBao__Token: "synthetic-private-token",
          Kratos__AdminBaseUrl: "http://kratos:4434/",
          MINICLOUD_SETUP_TOKEN: "synthetic-setup-token"
        });
        expect(result.status, result.output).toBe(0);
        expect(result.serverStarted).toBe(true);
        expect(parseRuntimeConfig(result.config)).toEqual({
          apiBaseUrl,
          appDomain,
          sourceUrl: "https://github.com/muniventures/minicloud",
          auth: { origin: apiBaseUrl, capabilitiesUrl: `${apiBaseUrl}/api/account/capabilities` }
        });
        expect(JSON.stringify(result.config)).not.toMatch(/openbao|kratos|synthetic/);
      }
    });

    it.each([
      { MINICLOUD_API_URL: "http://api.example.test" },
      { MINICLOUD_API_URL: "https://api.example.test/admin" },
      { MINICLOUD_API_URL: "https://user:synthetic-secret@api.example.test" },
      { MINICLOUD_APP_DOMAIN: "apps.example.test/path" },
      { MINICLOUD_SOURCE_URL: "https://user:synthetic-secret@example.test/source" }
    ])("rejects invalid public values before writing configuration or starting the server: %j", (environment) => {
      const result = runStartup(environment);
      expect(result.status).toBe(1);
      expect(result.serverStarted).toBe(false);
      expect(result.config).toBeUndefined();
      expect(result.output).not.toContain("synthetic-secret");
    });
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
