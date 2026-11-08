import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, expect, it, vi } from "vitest";
import { AppEnvironmentsPanel } from "./AppEnvironmentsPanel";
import { createAppEnvironment, getApp, renameAppEnvironment } from "../../api/appsApi";
import { listPlans, type ProviderPlan } from "../../api/providersApi";
import type { AppResponse } from "../../models/controlPlane";
vi.mock("../../api/appsApi", () => ({ createAppEnvironment: vi.fn(), getApp: vi.fn(), renameAppEnvironment: vi.fn() }));
vi.mock("../../api/providersApi", () => ({ listPlans: vi.fn() }));
afterEach(() => { cleanup(); vi.resetAllMocks(); });
const app = { id: "root", organizationId: "org-1", environmentName: "prod", environments: [
  { id: "root", name: "prod", plan: "p0-h-0", database: "sqlite" },
  { id: "staging-id", name: "staging", plan: "p0-v-0", database: "sqlite" },
] } as AppResponse;
it.each([
  ["failed", "deletion failed"],
  ["queued", "deleting"],
  ["preparing", "deleting"],
])("shows %s deletion state accurately", async (deletionStatus, label) => {
  vi.mocked(listPlans).mockResolvedValue([]);
  const deletingApp = { ...app, deletionStatus, environments: [
    { ...app.environments![0], deletionStatus },
    app.environments![1],
  ] } as AppResponse;
  render(<MemoryRouter><AppEnvironmentsPanel app={deletingApp} onUpdated={vi.fn().mockResolvedValue(undefined)} /></MemoryRouter>);
  expect(screen.getByRole("link", { name: `prod · p0-h-0 (${label})` })).toHaveAttribute("href", "/apps/root");
  expect(screen.getByRole("link", { name: "staging · p0-v-0" })).toHaveAttribute("href", "/apps/staging-id");
  await waitFor(() => expect(screen.getByText("No enabled plans.", { exact: false })).toBeInTheDocument());
});
it("links to separate environment runtimes and creates with the chosen provider plan", async () => {
  vi.mocked(listPlans).mockResolvedValue([{ id: "dynamic", name: "Custom small", enabled: true, accountEnabled: true }] as ProviderPlan[]);
  vi.mocked(getApp).mockResolvedValue(app);
  vi.mocked(createAppEnvironment).mockResolvedValue(app);
  const updated = vi.fn().mockResolvedValue(undefined);
  render(<MemoryRouter><AppEnvironmentsPanel app={app} onUpdated={updated} /></MemoryRouter>);
  expect(screen.getByRole("link", { name: /staging/ })).toHaveAttribute("href", "/apps/staging-id");
  await screen.findByRole("option", { name: /Custom small/ });
  fireEvent.change(screen.getByLabelText("New environment name"), { target: { value: "qa" } });
  fireEvent.change(screen.getByLabelText("VPS plan"), { target: { value: "Custom small" } });
  fireEvent.click(screen.getByText("Add environment"));
  await waitFor(() => expect(updated).toHaveBeenCalledOnce());
  expect(createAppEnvironment).toHaveBeenCalledWith("root", "qa", "Custom small");
});
it("renames the current environment", async () => {
  vi.mocked(listPlans).mockResolvedValue([{ id: "dynamic", name: "Custom small", enabled: true, accountEnabled: true }] as ProviderPlan[]);
  vi.mocked(getApp).mockResolvedValue(app);
  vi.mocked(renameAppEnvironment).mockResolvedValue(app);
  render(<MemoryRouter><AppEnvironmentsPanel app={app} onUpdated={vi.fn().mockResolvedValue(undefined)} /></MemoryRouter>);
  fireEvent.change(screen.getByLabelText("Current environment name"), { target: { value: "live" } });
  fireEvent.click(screen.getByText("Rename"));
  await waitFor(() => expect(renameAppEnvironment).toHaveBeenCalledWith("root", "live"));
});
it("loads sibling environments from the root when viewing a child", async () => {
  vi.mocked(listPlans).mockResolvedValue([{ id: "dynamic", name: "Custom small", enabled: true, accountEnabled: true }] as ProviderPlan[]);
  vi.mocked(getApp).mockResolvedValue(app);
  render(<MemoryRouter><AppEnvironmentsPanel app={{ ...app, id: "staging-id", parentAppId: "root", environmentName: "staging", environments: [] }} onUpdated={vi.fn().mockResolvedValue(undefined)} /></MemoryRouter>);
  expect(await screen.findByRole("link", { name: /prod/ })).toHaveAttribute("href", "/apps/root");
  expect(getApp).toHaveBeenCalledWith("root");
});
