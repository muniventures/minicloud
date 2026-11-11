import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { AuthContext, type AuthStatus } from "../../core/auth/AuthProvider";
import { authFixture, browserFlow, jsonResponse } from "../../test/accountFixtures";
import { ProviderContinuationModule } from "./ProviderContinuationModule";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

function mount(search: string, status: AuthStatus) {
  render(
    <AuthContext.Provider value={authFixture({ status })}>
      <MemoryRouter initialEntries={[`/sign-in/continue?${search}`]}>
        <Routes>
          <Route path="/sign-in/continue" element={<ProviderContinuationModule />} />
          <Route path="/cli/login" element={<p>Explicit CLI consent</p>} />
          <Route path="/apps" element={<p>Apps</p>} />
        </Routes>
      </MemoryRouter>
    </AuthContext.Provider>
  );
}

describe("Google callback continuation", () => {
  it("returns an authenticated callback to explicit CLI consent", async () => {
    const fetcher = vi.fn();
    vi.stubGlobal("fetch", fetcher);
    mount("result=success&kind=login&returnTo=%2Fcli%2Flogin%3Fsession%3Drequest-1", "authenticated");
    expect(await screen.findByText("Explicit CLI consent")).toBeInTheDocument();
    expect(fetcher).not.toHaveBeenCalled();
  });

  it("ignores unsafe return targets", async () => {
    mount("result=success&kind=login&returnTo=https%3A%2F%2Fevil.test", "authenticated");
    expect(await screen.findByText("Apps")).toBeInTheDocument();
  });

  it("re-reads the same pending login flow after a failed callback", async () => {
    const flow = browserFlow();
    const fetcher = vi.fn().mockResolvedValue(jsonResponse({ flow, session: null }));
    vi.stubGlobal("fetch", fetcher);
    mount(`result=failed&kind=login&flow=${flow.id}&returnTo=%2Fcli%2Flogin%3Fsession%3Drequest-1`, "unauthenticated");
    expect(await screen.findByLabelText("Password")).toBeInTheDocument();
    expect(screen.getByText(/did not complete/)).toBeInTheDocument();
    await waitFor(() => expect(fetcher).toHaveBeenCalledOnce());
    expect(fetcher.mock.calls[0][0]).toContain(`/api/account/flows/login?id=${flow.id}`);
  });

  it("explains an unadmitted Google identity without starting a session", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({ flow: browserFlow(), session: null })));
    mount("result=forbidden&kind=settings&returnTo=%2Fsettings%2Faccount", "authenticated");
    expect(await screen.findByText(/not admitted/)).toBeInTheDocument();
    expect(screen.getByText("Return to account settings")).toBeInTheDocument();
  });
});
