import { spawn } from "node:child_process";
import { renameSync, writeFileSync } from "node:fs";

const defaultSourceUrl = "https://github.com/muniventures/minicloud";

function isLocalHostname(hostname) {
  return hostname === "localhost" || hostname === "127.0.0.1" || hostname === "[::1]";
}

function validateOrigin(value) {
  let url;
  try {
    url = new URL(value);
  } catch {
    throw new Error("MINICLOUD_API_URL must be an absolute origin.");
  }

  if (
    url.origin !== value ||
    url.username ||
    url.password ||
    url.search ||
    url.hash ||
    !(url.protocol === "https:" || (url.protocol === "http:" && isLocalHostname(url.hostname)))
  ) {
    throw new Error("MINICLOUD_API_URL must be an HTTPS origin (HTTP is allowed only for localhost).");
  }

  return url.origin;
}

function validateAppDomain(value) {
  if (
    value.length > 253 ||
    value.endsWith(".") ||
    value !== value.toLowerCase() ||
    value.split(".").some(
      (label) =>
        label.length === 0 ||
        label.length > 63 ||
        !/^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/.test(label)
    )
  ) {
    throw new Error("MINICLOUD_APP_DOMAIN must be a hostname without a scheme, port, or path.");
  }

  const parsed = new URL(`https://${value}`);
  if (parsed.hostname !== value) {
    throw new Error("MINICLOUD_APP_DOMAIN must be a valid hostname.");
  }

  return value;
}

function validateSourceUrl(value) {
  let url;
  try {
    url = new URL(value);
  } catch {
    throw new Error("MINICLOUD_SOURCE_URL must be an absolute URL.");
  }

  if (
    url.username ||
    url.password ||
    url.search ||
    url.hash ||
    !(url.protocol === "https:" || (url.protocol === "http:" && isLocalHostname(url.hostname)))
  ) {
    throw new Error("MINICLOUD_SOURCE_URL must be a public HTTPS URL (HTTP is allowed only for localhost).");
  }

  return url.href;
}

function createConfig() {
  const apiBaseUrl = validateOrigin(process.env.MINICLOUD_API_URL ?? "");
  const appDomain = validateAppDomain(process.env.MINICLOUD_APP_DOMAIN ?? "");
  const sourceUrl = validateSourceUrl(process.env.MINICLOUD_SOURCE_URL ?? defaultSourceUrl);
  return {
    apiBaseUrl,
    appDomain,
    sourceUrl,
    auth: {
      origin: apiBaseUrl,
      capabilitiesUrl: `${apiBaseUrl}/api/account/capabilities`
    }
  };
}

function startNginx() {
  const config = createConfig();
  const output = "/usr/share/nginx/html/runtime-config.json";
  const temporary = `${output}.tmp`;
  writeFileSync(temporary, `${JSON.stringify(config)}\n`, { mode: 0o644 });
  renameSync(temporary, output);

  const nginx = spawn("nginx", ["-g", "daemon off;"], { stdio: "inherit" });
  process.on("SIGTERM", () => nginx.kill("SIGTERM"));
  process.on("SIGINT", () => nginx.kill("SIGINT"));
  nginx.on("error", (error) => {
    console.error("Unable to start dashboard web server.", error.message);
    process.exitCode = 1;
  });
  nginx.on("exit", (code, signal) => {
    process.exitCode = signal ? 1 : (code ?? 1);
  });
}

try {
  startNginx();
} catch (error) {
  console.error("Dashboard startup configuration is invalid:", error instanceof Error ? error.message : "unknown error");
  process.exitCode = 1;
}
