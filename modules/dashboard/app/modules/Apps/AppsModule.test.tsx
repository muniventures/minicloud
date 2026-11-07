import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AppsModule } from "./AppsModule";
import { listApps } from "../../api/appsApi";
import type { AppResponse } from "../../models/controlPlane";

vi.mock("../../api/appsApi", () => ({
  listApps: vi.fn()
}));

vi.mock("../../core/auth/AuthProvider", () => ({
  useAuth: () => ({
    selectedOrganization: { id: "org_123", name: "MuniVentures" }
  })
}));

const mockApps: AppResponse[] = [
  {
    id: "app_example-suite",
    organizationId: "org_123",
    name: "Example Suite",
    slug: "example-suite",
    plan: "p0",
    database: "sqlite",
    domains: [
      {
        id: "dom_1",
        hostname: "muni-example-suite-admin.app.muni.dev",
        url: "https://muni-example-suite-admin.app.muni.dev",
        sslStatus: "active",
        reserved: true
      },
      {
        id: "dom_2",
        hostname: "muni-example-suite-agency-ui.app.muni.dev",
        url: "https://muni-example-suite-agency-ui.app.muni.dev",
        sslStatus: "active",
        reserved: true
      }
    ],
    latestDeployment: {
      id: "dep_1",
      status: "failed",
      completedAt: new Date(Date.now() - 14 * 60000).toISOString()
    }
  },
  {
    id: "app_astools",
    organizationId: "org_123",
    name: "As Tools",
    slug: "as-tools",
    plan: "p0",
    database: "sqlite",
    domains: [
      {
        id: "dom_3",
        hostname: "muni-as-tools-as-route-analyzer.app.muni.dev",
        url: "https://muni-as-tools-as-route-analyzer.app.muni.dev",
        sslStatus: "active",
        reserved: true
      }
    ],
    latestDeployment: {
      id: "dep_2",
      status: "succeeded",
      completedAt: new Date(Date.now() - 2 * 3600000).toISOString()
    }
  }
];

describe("AppsModule", () => {
  afterEach(cleanup);
  beforeEach(() => { sessionStorage.clear(); vi.clearAllMocks(); vi.mocked(listApps).mockResolvedValue(mockApps); });
  const mount = () => render(<MemoryRouter><AppsModule /></MemoryRouter>);
  it("defaults to cards, reports runtime unavailable and has no empty More menu", async () => {
    mount(); await screen.findByText("Example Suite");
    expect(screen.getAllByText("Runtime unavailable")).toHaveLength(2);
    expect(screen.queryByText("Healthy")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "More" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Card view" })).toHaveAttribute("aria-pressed", "true");
  });
  it("combines domain search and filters, preserves search and sorting on clear and refresh", async () => {
    mount(); await screen.findByText("Example Suite");
    fireEvent.change(screen.getByRole("textbox", { name: "Search apps" }), { target: { value: "route-analyzer" } });
    fireEvent.click(screen.getByRole("button", { name: "Filter & sort" }));
    fireEvent.keyDown(screen.getByRole("combobox", { name: "Database" }), { key: "ArrowDown" });
    fireEvent.click(await screen.findByRole("option", { name: "sqlite" }));
    fireEvent.keyDown(screen.getByRole("combobox", { name: "Latest deployment" }), { key: "ArrowDown" });
    fireEvent.click(await screen.findByRole("option", { name: "failed" }));
    expect(screen.getByText("0 matching apps")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Descending" }));
    fireEvent.click(screen.getByRole("button", { name: "Clear filters" }));
    expect(screen.getByText("1 matching apps")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Descending" })).toHaveAttribute("aria-pressed", "true");
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.getByRole("textbox", { name: "Search apps" })).toHaveValue("route-analyzer");
    fireEvent.click(screen.getByRole("button", { name: "Refresh apps" }));
    await waitFor(() => expect(listApps).toHaveBeenCalledTimes(2));
    expect(screen.queryByText("Example Suite")).not.toBeInTheDocument();
  });
  it("preserves results and chosen list mode across remount", async () => {
    const view = mount(); await screen.findByText("Example Suite");
    fireEvent.change(screen.getByRole("textbox", { name: "Search apps" }), { target: { value: "agency" } });
    fireEvent.click(screen.getByRole("button", { name: "List view" }));
    expect(screen.queryByText("As Tools")).not.toBeInTheDocument();
    view.unmount(); mount(); await screen.findByText("Example Suite");
    expect(screen.getByRole("button", { name: "List view" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("textbox", { name: "Search apps" })).toHaveValue("agency");
  });
});
