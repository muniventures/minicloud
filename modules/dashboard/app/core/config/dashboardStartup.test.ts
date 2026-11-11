/// <reference types="node" />
import { spawnSync } from "node:child_process";
import { chmodSync, cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import path from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { parseRuntimeConfig } from "./runtimeConfig";

const dashboardRoot = path.resolve(__dirname, "../../..");
const startupScript = path.join(dashboardRoot, "dashboard-startup.mjs");
const artifactRoot = path.resolve(dashboardRoot, "../../.local/artifacts/dashboard-startup-tests");

function snapshot(directory: string) {
  return Object.fromEntries(
    readdirSync(directory, { recursive: true, withFileTypes: true })
      .filter((entry) => entry.isFile() && entry.name !== "runtime-config.json")
      .map((entry) => {
        const file = path.join(entry.parentPath, entry.name);
        return [path.relative(directory, file), readFileSync(file, "utf8")];
      })
  );
}

describe("dashboard container startup", () => {
  let workspace: string;
  let image: string;
  let stubPath: string;

  beforeEach(() => {
    mkdirSync(artifactRoot, { recursive: true });
    workspace = mkdtempSync(path.join(artifactRoot, "run-"));
    image = path.join(workspace, "html");
    mkdirSync(path.join(image, "assets"), { recursive: true });
    writeFileSync(path.join(image, "index.html"), "<script type=module src=/assets/app.js></script>");
    writeFileSync(path.join(image, "assets", "app.js"), "fetch('/runtime-config.json')");
    stubPath = path.join(workspace, "bin");
    mkdirSync(stubPath);
    const nginx = path.join(stubPath, "nginx");
    writeFileSync(nginx, `#!/bin/sh\nprintf '%s\\n' "$*" > "${workspace}/nginx-args"\n`);
    chmodSync(nginx, 0o755);
  });

  afterEach(() => rmSync(workspace, { recursive: true, force: true }));

  function start(root: string, environment: Record<string, string>) {
    return spawnSync(process.execPath, [startupScript], {
      encoding: "utf8",
      env: { PATH: `${stubPath}:${process.env.PATH}`, MINICLOUD_DASHBOARD_HTML_ROOT: root, ...environment }
    });
  }

  it("serves one immutable build under two installations' public configuration", () => {
    const first = path.join(workspace, "first");
    const second = path.join(workspace, "second");
    cpSync(image, first, { recursive: true });
    cpSync(image, second, { recursive: true });

    const one = start(first, { MINICLOUD_API_URL: "https://api.one.example", MINICLOUD_APP_DOMAIN: "apps.one.example" });
    const two = start(second, {
      MINICLOUD_API_URL: "https://api.two.example",
      MINICLOUD_APP_DOMAIN: "apps.two.example",
      MINICLOUD_SOURCE_URL: "https://source.two.example/minicloud"
    });

    expect(one.status, one.stderr).toBe(0);
    expect(two.status, two.stderr).toBe(0);
    expect(readFileSync(path.join(workspace, "nginx-args"), "utf8")).toBe("-g daemon off;\n");
    const firstConfig = parseRuntimeConfig(JSON.parse(readFileSync(path.join(first, "runtime-config.json"), "utf8")));
    const secondConfig = parseRuntimeConfig(JSON.parse(readFileSync(path.join(second, "runtime-config.json"), "utf8")));
    expect(firstConfig).toEqual({
      apiBaseUrl: "https://api.one.example",
      appDomain: "apps.one.example",
      sourceUrl: "https://github.com/muniventures/minicloud",
      auth: { origin: "https://api.one.example", capabilitiesUrl: "https://api.one.example/api/account/capabilities" }
    });
    expect(secondConfig.apiBaseUrl).toBe("https://api.two.example");
    expect(secondConfig.appDomain).toBe("apps.two.example");
    expect(secondConfig.sourceUrl).toBe("https://source.two.example/minicloud");
    expect(snapshot(first)).toEqual(snapshot(image));
    expect(snapshot(second)).toEqual(snapshot(image));
  });

  it.each([
    [{ MINICLOUD_APP_DOMAIN: "apps.example" }],
    [{ MINICLOUD_API_URL: "http://api.example", MINICLOUD_APP_DOMAIN: "apps.example" }],
    [{ MINICLOUD_API_URL: "https://api.example/path", MINICLOUD_APP_DOMAIN: "apps.example" }],
    [{ MINICLOUD_API_URL: "https://api.example", MINICLOUD_APP_DOMAIN: "https://apps.example" }],
    [{ MINICLOUD_API_URL: "https://api.example", MINICLOUD_APP_DOMAIN: "apps.example", MINICLOUD_SOURCE_URL: "https://u:p@source.example" }]
  ])("refuses invalid public configuration before serving %#", (environment) => {
    const result = start(image, environment);
    expect(result.status).toBe(1);
    expect(result.stderr).toContain("Dashboard startup configuration is invalid");
    expect(existsSync(path.join(image, "runtime-config.json"))).toBe(false);
    expect(existsSync(path.join(workspace, "nginx-args"))).toBe(false);
  });
});
