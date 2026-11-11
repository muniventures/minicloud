import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { apiRequest, setCsrfToken } from "../../api/apiClient";
import { accountCapabilities, accountProfile, accountSession, authFixture, jsonResponse } from "../../test/accountFixtures";
import { AuthContext, AuthProvider, useAuth } from "./AuthProvider";
import { AuthenticatedArea } from "./AuthenticatedArea";
import { GuestArea } from "./GuestArea";

function Probe() {
  const auth = useAuth();
  return <><p>{auth.status}</p><p>{auth.profile?.email}</p>
    <button onClick={() => void auth.refreshSession()}>Refresh</button>
    <button onClick={() => void auth.logout()}>Logout</button></>;
}
function installAccount(status = 200) {
  const fetcher = vi.fn(async (input: string) => {
    if (input.endsWith("/capabilities")) return jsonResponse(accountCapabilities);
    if (input.endsWith("/session")) return status === 200 ? jsonResponse(accountSession) :
      jsonResponse({ error: { code: status === 401 ? "unauthenticated" : "auth_unavailable", message: "Retry" } }, status);
    if (input.endsWith("/logout")) return new Response(null, { status: 204 });
    return jsonResponse(accountProfile);
  });
  vi.stubGlobal("fetch", fetcher);
  return fetcher;
}
beforeEach(() => setCsrfToken(null));
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("AuthProvider", () => {
  it("loads the seeded operator without creating a workspace and clears identity on logout", async () => {
    const fetcher = installAccount();
    render(<AuthProvider><Probe /></AuthProvider>);
    expect(screen.getByText("initializing")).toBeInTheDocument();
    expect(await screen.findByText("authenticated")).toBeInTheDocument();
    expect(screen.getByText(accountProfile.email)).toBeInTheDocument();
    expect(fetcher.mock.calls.map(call => call[0])).toEqual([
      expect.stringContaining("/capabilities"), expect.stringContaining("/session"), expect.stringContaining("/api/me")
    ]);
    fireEvent.click(screen.getByText("Logout"));
    expect(await screen.findByText("unauthenticated")).toBeInTheDocument();
    expect(screen.queryByText(accountProfile.email)).not.toBeInTheDocument();
    const logoutOptions = vi.mocked(fetch).mock.calls.at(-1)?.[1];
    expect(logoutOptions).toMatchObject({ method: "POST", headers: { "X-Minicloud-CSRF": "synthetic-csrf" } });
  });

  it("preserves a valid profile on an outage, retries, and clears only on confirmed expiry", async () => {
    const fetcher = installAccount();
    render(<AuthProvider><Probe /></AuthProvider>);
    await screen.findByText("authenticated");
    fetcher.mockImplementationOnce(async () => jsonResponse({ error: { code: "auth_unavailable", message: "Retry" } }, 503));
    fireEvent.click(screen.getByText("Refresh"));
    await screen.findByText("unavailable");
    expect(screen.getByText(accountProfile.email)).toBeInTheDocument();
    fireEvent.click(screen.getByText("Refresh"));
    await screen.findByText("authenticated");
    fetcher.mockImplementationOnce(async () => jsonResponse({ error: { code: "unauthenticated", message: "Sign in" } }, 401));
    fireEvent.click(screen.getByText("Refresh"));
    await screen.findByText("unauthenticated");
    expect(screen.queryByText(accountProfile.email)).not.toBeInTheDocument();
  });

  it("blocks protected content when a later API request confirms session expiry", async () => {
    const fetcher = installAccount();
    render(<AuthProvider><Probe /></AuthProvider>);
    await screen.findByText("authenticated");
    fetcher.mockImplementationOnce(async () => jsonResponse({ error: { code: "unauthenticated", message: "Sign in" } }, 401));
    await expect(apiRequest("/api/apps")).rejects.toMatchObject({ status: 401 });
    await screen.findByText("unauthenticated");
    expect(screen.queryByText(accountProfile.email)).not.toBeInTheDocument();
  });

  it.each(["required", "blocked"] as const)("exposes explicit setup_%s without requesting a profile", async state => {
    const fetcher = vi.fn().mockResolvedValue(jsonResponse({ ...accountCapabilities, bootstrapState: state }));
    vi.stubGlobal("fetch", fetcher);
    render(<AuthProvider><Probe /></AuthProvider>);
    await screen.findByText(`setup_${state}`);
    expect(fetcher).toHaveBeenCalledTimes(1);
  });

  it("does not interpret an empty membership list as setup or create a workspace", async () => {
    installAccount();
    vi.mocked(fetch).mockImplementation(async input =>
      jsonResponse(String(input).endsWith("/capabilities") ? accountCapabilities :
        String(input).endsWith("/session") ? accountSession : { ...accountProfile, organizations: [] }));
    render(<AuthProvider><Probe /></AuthProvider>);
    await screen.findByText("authenticated");
    expect(fetch).toHaveBeenCalledTimes(3);
  });
});

function LocationProbe() {
  const location = useLocation();
  return <p>{JSON.stringify(location.state)}</p>;
}
it("preserves CLI consent query through the protected route and sign-in state", async () => {
  render(<AuthContext.Provider value={authFixture({ status: "unauthenticated" })}>
    <MemoryRouter initialEntries={["/cli/login?session=request-1"]}>
      <Routes>
        <Route path="/cli/login" element={<AuthenticatedArea>Consent</AuthenticatedArea>} />
        <Route path="/sign-in" element={<LocationProbe />} />
      </Routes>
    </MemoryRouter>
  </AuthContext.Provider>);
  expect(await screen.findByText('{"from":"/cli/login?session=request-1"}')).toBeInTheDocument();
});
it("blocks outages without redirecting or revealing protected content", async () => {
  const refresh = vi.fn();
  render(<AuthContext.Provider value={authFixture({ status: "unavailable", refreshSession: refresh })}>
    <MemoryRouter initialEntries={["/cli/login?session=request-1"]}>
      <AuthenticatedArea>Private consent</AuthenticatedArea>
    </MemoryRouter>
  </AuthContext.Provider>);
  expect(screen.queryByText("Private consent")).not.toBeInTheDocument();
  fireEvent.click(screen.getByText("Retry"));
  await waitFor(() => expect(refresh).toHaveBeenCalledOnce());
});
it("returns authenticated guests to their safe intended path, not always apps", async () => {
  render(<AuthContext.Provider value={authFixture()}>
    <MemoryRouter initialEntries={[{ pathname: "/sign-in", state: { from: "/cli/login?session=request-1" } }]}>
      <Routes>
        <Route path="/sign-in" element={<GuestArea>Sign in</GuestArea>} />
        <Route path="/cli/login" element={<p>Explicit CLI consent</p>} />
      </Routes>
    </MemoryRouter>
  </AuthContext.Provider>);
  expect(await screen.findByText("Explicit CLI consent")).toBeInTheDocument();
});
