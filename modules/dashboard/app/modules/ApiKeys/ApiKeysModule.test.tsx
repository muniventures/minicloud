import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { ApiKeysModule } from "./ApiKeysModule";
import { createApiKey, listApiKeys, revokeApiKey } from "@/api/apiKeysApi";
vi.mock("@/api/apiKeysApi", () => ({
  createApiKey: vi.fn(),
  listApiKeys: vi.fn(),
  revokeApiKey: vi.fn(),
}));
const organization = { id: "org", role: "owner" };
vi.mock("@/core/auth/AuthProvider", () => ({
  useAuth: () => ({ selectedOrganization: organization }),
}));
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
beforeEach(() => {
  vi.stubGlobal("ResizeObserver", class { observe() {} unobserve() {} disconnect() {} });
  sessionStorage.clear();
  vi.clearAllMocks();
  organization.role = "owner";
  vi.mocked(listApiKeys).mockResolvedValue([
    {
      id: "one",
      name: "CI",
      prefix: "mc_ci",
      scopes: ["apps:read"],
      createdAt: "2026-01-01",
      lastUsedAt: null,
    },
    {
      id: "two",
      name: "Old",
      prefix: "mc_old",
      scopes: [],
      createdAt: "2025-01-01",
      expiresAt: "2025-02-01",
      revokedAt: "2025-01-02",
    },
  ]);
});
it("masks prefixes, distinguishes never-used metadata, and gives revocation precedence", async () => {
  render(
    <MemoryRouter>
      <ApiKeysModule />
    </MemoryRouter>,
  );
  await screen.findByText("CI");
  expect(screen.getByText("mc_ci••••")).toBeInTheDocument();
  expect(screen.getByText("Never used")).toBeInTheDocument();
  expect(screen.getByText("Unavailable")).toBeInTheDocument();
  expect(screen.getByText("Revoked")).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Revoke" }));
  expect(await screen.findByText("Revoke API key?")).toBeInTheDocument();
  expect(revokeApiKey).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
  expect(revokeApiKey).not.toHaveBeenCalled();
});
it("preserves one-time token creation and dismissal", async () => {
  vi.mocked(createApiKey).mockResolvedValue({
    id: "new",
    name: "New",
    prefix: "mc_test",
    scopes: [],
    createdAt: "2026-01-01",
    token: "test-only-one-time-token",
  });
  render(
    <MemoryRouter>
      <ApiKeysModule />
    </MemoryRouter>,
  );
  await screen.findByText("CI");
  fireEvent.click(screen.getByRole("button", { name: "Create key" }));
  fireEvent.change(screen.getByLabelText("Name"), { target: { value: "New" } });
  fireEvent.submit(screen.getByLabelText("Name").closest("form")!);
  expect(
    await screen.findByText("test-only-one-time-token"),
  ).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Done" }));
  await waitFor(() =>
    expect(
      screen.queryByText("test-only-one-time-token"),
    ).not.toBeInTheDocument(),
  );
});
it("hides write actions for unauthorized roles", async () => {
  organization.role = "viewer";
  render(
    <MemoryRouter>
      <ApiKeysModule />
    </MemoryRouter>,
  );
  await screen.findByText("CI");
  expect(
    screen.queryByRole("button", { name: "Create key" }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Revoke" }),
  ).not.toBeInTheDocument();
});
