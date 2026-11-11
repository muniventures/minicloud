export type RuntimeConfig = {
  apiBaseUrl: string;
  appDomain: string;
  sourceUrl: string;
  auth: {
    origin: string;
    capabilitiesUrl: string;
  };
};

const developmentConfig: RuntimeConfig = {
  apiBaseUrl: "http://localhost:3210",
  appDomain: "app.muni.dev",
  sourceUrl: "https://github.com/muniventures/minicloud",
  auth: {
    origin: "http://localhost:3210",
    capabilitiesUrl: "http://localhost:3210/api/account/capabilities"
  }
};

let currentConfig: RuntimeConfig | undefined = import.meta.env.DEV ? developmentConfig : undefined;

export const runtimeConfig = new Proxy({} as RuntimeConfig, {
  get(_target, property: keyof RuntimeConfig) {
    if (!currentConfig) {
      throw new Error("Dashboard runtime configuration has not been loaded.");
    }

    return currentConfig[property];
  }
});

export function parseRuntimeConfig(value: unknown): RuntimeConfig {
  if (!isRecord(value) || !hasExactKeys(value, ["apiBaseUrl", "appDomain", "sourceUrl", "auth"])) {
    throw new Error("Runtime configuration must contain only apiBaseUrl, appDomain, sourceUrl, and auth.");
  }

  const { apiBaseUrl, appDomain, sourceUrl, auth } = value;
  if (
    typeof apiBaseUrl !== "string" ||
    !isPublicOrigin(apiBaseUrl) ||
    typeof appDomain !== "string" ||
    !isHostname(appDomain) ||
    typeof sourceUrl !== "string" ||
    !isPublicUrl(sourceUrl) ||
    !isRecord(auth) ||
    !hasExactKeys(auth, ["origin", "capabilitiesUrl"]) ||
    auth.origin !== apiBaseUrl ||
    auth.capabilitiesUrl !== `${apiBaseUrl}/api/account/capabilities`
  ) {
    throw new Error("Runtime configuration contains invalid public dashboard settings.");
  }

  return {
    apiBaseUrl,
    appDomain,
    sourceUrl,
    auth: {
      origin: auth.origin,
      capabilitiesUrl: auth.capabilitiesUrl
    }
  };
}

export async function loadRuntimeConfig(
  fetchConfig: typeof fetch = fetch,
  isDevelopment = import.meta.env.DEV
): Promise<RuntimeConfig> {
  if (isDevelopment) {
    currentConfig = developmentConfig;
    return developmentConfig;
  }

  const response = await fetchConfig("/runtime-config.json", { cache: "no-store" });
  if (!response.ok) {
    throw new Error(`Unable to load dashboard runtime configuration (${response.status}).`);
  }

  let payload: unknown;
  try {
    payload = await response.json();
  } catch {
    throw new Error("Dashboard runtime configuration is not valid JSON.");
  }

  currentConfig = parseRuntimeConfig(payload);
  return currentConfig;
}

function isPublicOrigin(value: string): boolean {
  try {
    const url = new URL(value);
    return (
      url.origin === value &&
      url.username === "" &&
      url.password === "" &&
      url.search === "" &&
      url.hash === "" &&
      isAllowedProtocol(url)
    );
  } catch {
    return false;
  }
}

function isPublicUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return (
      isAllowedProtocol(url) &&
      url.username === "" &&
      url.password === "" &&
      url.hostname !== "" &&
      url.search === "" &&
      url.hash === ""
    );
  } catch {
    return false;
  }
}

function isAllowedProtocol(url: URL): boolean {
  return url.protocol === "https:" || (url.protocol === "http:" && isLocalHostname(url.hostname));
}

function isLocalHostname(hostname: string): boolean {
  return hostname === "localhost" || hostname === "127.0.0.1" || hostname === "[::1]";
}

function isHostname(value: string): boolean {
  if (value.length > 253 || value.endsWith(".") || value !== value.toLowerCase()) {
    return false;
  }

  const labels = value.split(".");
  return labels.every(
    (label) =>
      label.length > 0 &&
      label.length <= 63 &&
      /^[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?$/.test(label)
  );
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function hasExactKeys(value: Record<string, unknown>, keys: string[]): boolean {
  return Object.keys(value).length === keys.length && keys.every((key) => key in value);
}

await loadRuntimeConfig();
