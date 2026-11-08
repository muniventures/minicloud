import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, expect, it, vi } from "vitest";
import { AppEnvironmentsPanel } from "./AppEnvironmentsPanel";
import { createAppEnvironment, getApp, renameAppEnvironment } from "../../api/appsApi";
import type { AppResponse } from "../../models/controlPlane";
vi.mock("../../api/appsApi", () => ({ createAppEnvironment: vi.fn(), getApp: vi.fn(), renameAppEnvironment: vi.fn() }));
afterEach(() => { cleanup(); vi.resetAllMocks(); });
const app = { id: "root", environmentName: "prod", environments: [
  { id: "root", name: "prod", plan: "p0-h-0", database: "sqlite" },
  { id: "staging-id", name: "staging", plan: "p0-v-0", database: "sqlite" },
] } as AppResponse;
it("links to separate environment runtimes and creates with the chosen provider plan", async () => {
  vi.mocked(getApp).mockResolvedValue(app);
  vi.mocked(createAppEnvironment).mockResolvedValue(app);
  const updated = vi.fn().mockResolvedValue(undefined);
  render(<MemoryRouter><AppEnvironmentsPanel app={app} onUpdated={updated} /></MemoryRouter>);
  expect(screen.getByRole("link", { name: /staging/ })).toHaveAttribute("href", "/apps/staging-id");
  fireEvent.change(screen.getByLabelText("New environment name"), { target: { value: "qa" } });
  fireEvent.change(screen.getByLabelText("VPS plan"), { target: { value: "p0-d-0" } });
  fireEvent.click(screen.getByText("Add environment"));
  await waitFor(() => expect(updated).toHaveBeenCalledOnce());
  expect(createAppEnvironment).toHaveBeenCalledWith("root", "qa", "p0-d-0");
});
it("renames the current environment", async () => {
  vi.mocked(getApp).mockResolvedValue(app);
  vi.mocked(renameAppEnvironment).mockResolvedValue(app);
  render(<MemoryRouter><AppEnvironmentsPanel app={app} onUpdated={vi.fn().mockResolvedValue(undefined)} /></MemoryRouter>);
  fireEvent.change(screen.getByLabelText("Current environment name"), { target: { value: "live" } });
  fireEvent.click(screen.getByText("Rename"));
  await waitFor(() => expect(renameAppEnvironment).toHaveBeenCalledWith("root", "live"));
});
it("loads sibling environments from the root when viewing a child", async () => {
  vi.mocked(getApp).mockResolvedValue(app);
  render(<MemoryRouter><AppEnvironmentsPanel app={{ ...app, id: "staging-id", parentAppId: "root", environmentName: "staging", environments: [] }} onUpdated={vi.fn().mockResolvedValue(undefined)} /></MemoryRouter>);
  expect(await screen.findByRole("link", { name: /prod/ })).toHaveAttribute("href", "/apps/root");
  expect(getApp).toHaveBeenCalledWith("root");
});
