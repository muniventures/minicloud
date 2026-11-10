import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AppsModule } from "./AppsModule";
import { listApplications } from "../../api/applicationsApi";
import { appsFixtureApplications } from "../../api/applicationsFixtures";

vi.mock("../../api/applicationsApi", () => ({
  listApplications: vi.fn(),
}));

vi.mock("../../core/auth/AuthProvider", () => ({
  useAuth: () => ({
    selectedOrganization: { id: "org_fixture_01", name: "Fixture Org", role: "owner" },
  }),
}));

describe("AppsModule", () => {
  afterEach(cleanup);
  beforeEach(() => {
    sessionStorage.clear();
    vi.clearAllMocks();
    vi.mocked(listApplications).mockResolvedValue(appsFixtureApplications());
  });
  const mount = () => render(<MemoryRouter><AppsModule /></MemoryRouter>);

  it("shows each application once with environment subrows and no Plan/Runtime columns or filters", async () => {
    mount();
    await screen.findByText("MuniPages");
    expect(screen.getByRole("columnheader", { name: "App" })).toBeInTheDocument();
    expect(screen.getByRole("columnheader", { name: "Environment" })).toBeInTheDocument();
    expect(screen.getByRole("columnheader", { name: "Latest deployment" })).toBeInTheDocument();
    expect(screen.getByRole("columnheader", { name: "Actions" })).toBeInTheDocument();
    expect(screen.queryByRole("columnheader", { name: "Plan" })).not.toBeInTheDocument();
    expect(screen.queryByRole("columnheader", { name: "Runtime" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Card view" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "List view" })).not.toBeInTheDocument();
    // production + staging are visible persistent subrows of one app
    expect(screen.getByText("production")).toBeInTheDocument();
    expect(screen.getByText("staging")).toBeInTheDocument();
    expect(screen.getByText("No deployments yet")).toBeInTheDocument();
  });

  it("keeps previews collapsed behind a count and exposes them on demand", async () => {
    mount();
    await screen.findByText("MuniPages");
    // feat-checkout (succeeded, no attention) is collapsed; fix-billing has no deployment
    const toggle = await screen.findByRole("button", { name: /Show 2 previews/ });
    expect(screen.queryByText("feat-checkout")).not.toBeInTheDocument();
    fireEvent.click(toggle);
    expect(screen.getByText("feat-checkout")).toBeInTheDocument();
    expect(screen.getByText("fix-billing")).toBeInTheDocument();
  });

  it("matches environment filters within one environment only and marks the match", async () => {
    mount();
    await screen.findByText("MuniPages");
    fireEvent.click(screen.getByRole("button", { name: "Filter & sort" }));
    fireEvent.keyDown(screen.getByRole("combobox", { name: "Latest deployment" }), { key: "ArrowDown" });
    fireEvent.click(await screen.findByRole("option", { name: "failed" }));
    // staging (failed) matches; no other app has a failed latest deployment
    expect(screen.getByText("staging")).toBeInTheDocument();
    expect(screen.getByText("Matches filter")).toBeInTheDocument();
    expect(screen.queryByText("EmptyApp")).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByText("1 of 2 apps")).toBeInTheDocument());
  });

  it("shows a zero-environment application with the Add environment action", async () => {
    mount();
    await screen.findByText("EmptyApp");
    expect(screen.getByText("No environments")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add environment" })).toBeInTheDocument();
  });

  it("reports load failure with retry instead of empty success", async () => {
    vi.mocked(listApplications).mockRejectedValue(new Error("Unable to load apps."));
    mount();
    expect(await screen.findByRole("alert")).toHaveTextContent("Unable to load apps.");
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
  });
});
