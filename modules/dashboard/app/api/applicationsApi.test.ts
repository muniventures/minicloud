import { beforeEach, describe, expect, it, vi } from "vitest";
import { apiRequest } from "./apiClient";
import * as applicationsApi from "./applicationsApi";
import { getApplication, listApplications } from "./applicationsApi";

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
});
