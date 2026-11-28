import { beforeEach, describe, expect, it, vi } from "vitest";
import { apiRequest } from "./apiClient";
import * as applicationsApi from "./applicationsApi";
import {
  getApplication,
  getEnvironment,
  listApplications,
  sleepEnvironment,
  updateEnvironment,
  wakeEnvironment,
} from "./applicationsApi";

vi.mock("./apiClient", () => ({ apiRequest: vi.fn() }));

describe("applicationsApi request shapes", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiRequest).mockResolvedValue({} as never);
  });

  it("lists applications with the organization query param", async () => {
    await listApplications("org 1");
    expect(apiRequest).toHaveBeenCalledWith("/api/apps?organizationId=org%201", {
      signal: undefined,
    });
  });

  it("does not expose a runtime-ID identity resolver", () => {
    expect(applicationsApi).not.toHaveProperty("resolveAppIdentity");
  });

  it("reads one application by id", async () => {
    await getApplication("app 123");
    expect(apiRequest).toHaveBeenCalledWith("/api/apps/app%20123", {
      signal: undefined,
    });
  });

  it("loads the selected environment sleep response by application and environment id", async () => {
    await getEnvironment("app 1", "env/1");
    expect(apiRequest).toHaveBeenCalledWith(
      "/api/apps/app%201/environments/env%2F1",
      { signal: undefined },
    );
  });

  it("patches sleep policy through the revision-checked environment route", async () => {
    const policy = { enabled: true, sleepAfterMinutes: 120, httpTrafficIsActivity: false, revision: 3 };
    await updateEnvironment("app 1", "env/1", { sleep: { policy } });

    expect(apiRequest).toHaveBeenCalledWith(
      "/api/apps/app%201/environments/env%2F1",
      { method: "PATCH", body: { sleep: { policy } } },
    );
  });

  it("posts manual sleep and wake actions with the API contract payloads", async () => {
    await sleepEnvironment("app 1", "env 1");
    expect(apiRequest).toHaveBeenCalledWith(
      "/api/apps/app%201/environments/env%201/sleep",
      { method: "POST" },
    );

    await wakeEnvironment("app 1", "env 1", 240);
    expect(apiRequest).toHaveBeenCalledWith(
      "/api/apps/app%201/environments/env%201/wake",
      { method: "POST", body: { keepAwakeMinutes: 240 } },
    );
  });

  it("keeps action failures visible to the caller", async () => {
    vi.mocked(apiRequest).mockRejectedValueOnce(new Error("Environment is busy."));
    await expect(sleepEnvironment("app 1", "env 1")).rejects.toThrow("Environment is busy.");
  });
});
