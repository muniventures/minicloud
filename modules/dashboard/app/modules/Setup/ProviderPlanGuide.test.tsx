import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { ProviderPlanGuide } from "./ProviderPlanGuide";
import { AuthContext } from "../../core/auth/AuthProvider";
import { authFixture } from "../../test/accountFixtures";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function mount(hasOrganization = true) {
  render(<AuthContext.Provider value={authFixture(hasOrganization ? {} : { selectedOrganization: null })}>
    <MemoryRouter><ProviderPlanGuide /></MemoryRouter>
  </AuthContext.Provider>);
}

describe("provider and plan guidance", () => {
  it.each([{ items: [] }, { items: [{ id: "existing" }] }])("uses existing read APIs/screens for new and ready operators; never allocates", async ({ items }) => {
    const fetcher = vi.fn().mockImplementation(() => Promise.resolve(new Response(JSON.stringify(items), {
      headers: { "content-type": "application/json" }
    })));
    vi.stubGlobal("fetch", fetcher);
    mount();
    expect(await screen.findByText(`${items.length} provider account(s), ${items.length} plan(s) in this workspace.`)).toBeInTheDocument();
    expect(fetcher).toHaveBeenCalledTimes(2);
    expect(fetcher.mock.calls.every(([, options]) => options.method === "GET")).toBe(true);
    expect(fetcher.mock.calls.map(([url]) => new URL(url).pathname).sort()).toEqual(["/api/plans", "/api/providers"]);
    expect(screen.getByRole("link", { name: "Connect a provider account" })).toHaveAttribute("href", "/plans/accounts");
    expect(screen.getByRole("link", { name: "Create a named plan" })).toHaveAttribute("href", "/plans");
    expect(screen.getByText(/Apps → Environment → Services/)).toBeInTheDocument();
    expect(screen.getByText(/deployment can allocate billable VPS/)).toBeInTheDocument();
  });

  it("keeps unavailable provider reads separate from setup and permits explicit safe read retry", async () => {
    const fetcher = vi.fn().mockRejectedValueOnce(new Error("private provider diagnostic"))
      .mockResolvedValue(new Response("[]", { headers: { "content-type": "application/json" } }));
    vi.stubGlobal("fetch", fetcher);
    mount();
    expect(await screen.findByRole("alert")).toHaveTextContent("Existing configuration has not been changed");
    expect(document.body.textContent).not.toContain("private provider");
    fetcher.mockImplementation(() => Promise.resolve(new Response("[]", { headers: { "content-type": "application/json" } })));
    fireEvent.click(screen.getByRole("button", { name: "Retry provider check" }));
    expect(await screen.findByText("0 provider account(s), 0 plan(s) in this workspace.")).toBeInTheDocument();
    expect(fetcher.mock.calls.every(([, options]) => options.method === "GET")).toBe(true);
  });

  it("does not reinterpret missing organization membership as installation bootstrap", () => {
    const fetcher = vi.fn();
    vi.stubGlobal("fetch", fetcher);
    mount(false);
    expect(screen.getByRole("link", { name: "create your workspace" })).toHaveAttribute("href", "/onboarding");
    expect(screen.queryByRole("link", { name: /setup/ })).not.toBeInTheDocument();
    expect(fetcher).not.toHaveBeenCalled();
  });
});
