import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../../core/auth/AuthProvider";
import { accountCapabilities, accountSession, authFixture, browserFlow, googleFlow, googleUrl, jsonResponse } from "../../test/accountFixtures";
import { AccountFlowForm } from "./AccountFlowForm";
import { routes } from "../../routes";
import { setCsrfToken } from "../../api/apiClient";
import { browserNavigation } from "../../api/accountApi";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); setCsrfToken(null); });
function mount(kind: "login" | "settings" | "recovery" = "login", recovery = false, google = false, returnTo?: string) {
  const auth = authFixture({ capabilities: { ...accountCapabilities, emailRecovery: recovery, google } });
  render(<AuthContext.Provider value={auth}><MemoryRouter><AccountFlowForm kind={kind} returnTo={returnTo} /></MemoryRouter></AuthContext.Provider>);
  return auth;
}

describe("account browser forms", () => {
  it("submits password and upstream CSRF once without persisting or displaying secrets", async () => {
    const storage = vi.spyOn(Storage.prototype, "setItem");
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow: browserFlow(), session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }));
    vi.stubGlobal("fetch", fetcher);
    const auth = mount();
    fireEvent.change(await screen.findByLabelText("Email"), { target: { value: "operator@example.test" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "synthetic-password" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    await waitFor(() => expect(auth.refreshSession).toHaveBeenCalledOnce());
    expect(JSON.parse(fetcher.mock.calls[1][1].body)).toEqual({
      csrf_token: "upstream-csrf", identifier: "operator@example.test", password: "synthetic-password", method: "password"
    });
    expect(storage).not.toHaveBeenCalled();
    expect(document.body.textContent).not.toContain("synthetic-password");
    expect(document.body.textContent).not.toContain(accountSession.csrfToken);
  });

  it("handles invalid credentials by reading the form, never replaying the password", async () => {
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow: browserFlow(), session: null }))
      .mockResolvedValueOnce(jsonResponse({ error: { code: "auth_flow_invalid", message: "synthetic-password upstream diagnostic" } }, 400))
      .mockResolvedValueOnce(jsonResponse({ flow: browserFlow(), session: null }));
    vi.stubGlobal("fetch", fetcher);
    mount();
    fireEvent.change(await screen.findByLabelText("Email"), { target: { value: "operator@example.test" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "synthetic-password" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Check your details");
    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(3));
    expect(fetcher.mock.calls.filter(call => call[1].method === "POST")).toHaveLength(1);
    expect(screen.getByLabelText("Password")).toHaveValue("");
    expect(document.body.textContent).not.toContain("synthetic-password");
  });

  it("requires an explicit restart after an expired form", async () => {
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ error: { code: "auth_flow_expired", message: "Expired" } }, 410))
      .mockResolvedValueOnce(jsonResponse({ flow: browserFlow(), session: null }));
    vi.stubGlobal("fetch", fetcher);
    mount();
    expect(await screen.findByRole("alert")).toHaveTextContent("Restart");
    expect(fetcher).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByText("Restart form"));
    expect(await screen.findByLabelText("Password")).toBeInTheDocument();
  });

  it("does not render signup, Google or recovery unless explicitly enabled", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({ flow: browserFlow(), session: null })));
    mount();
    await screen.findByLabelText("Email");
    expect(screen.queryByText(/Google/)).not.toBeInTheDocument();
    expect(screen.queryByText("Forgot password?")).not.toBeInTheDocument();
    expect(routes.flatMap(route => route.children ?? []).some(route => route.path === "/sign-up")).toBe(false);
  });

  it("does not start a disabled recovery flow", () => {
    const fetcher = vi.fn();
    vi.stubGlobal("fetch", fetcher);
    mount("recovery");
    expect(screen.getByText(/not enabled/)).toBeInTheDocument();
    expect(fetcher).not.toHaveBeenCalled();
  });

  it("supports enabled recovery email and code steps without treating the email as a code", async () => {
    const flow = browserFlow("recovery");
    flow.ui.nodes = [flow.ui.nodes[0],
      { group: "code", type: "email", name: "email", value: null, required: true, messages: [] },
      { group: "code", type: "submit", name: "method", value: "code", required: false, messages: [] }];
    const codeFlow = { ...flow, ui: { ...flow.ui, nodes: [flow.ui.nodes[0],
      { group: "code", type: "text", name: "code", value: null, required: true, messages: [] },
      flow.ui.nodes[2]] } };
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: codeFlow, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }));
    vi.stubGlobal("fetch", fetcher);
    const auth = mount("recovery", true);
    fireEvent.change(await screen.findByLabelText("Email"), { target: { value: "operator@example.test" } });
    fireEvent.click(screen.getByRole("button", { name: "Continue recovery" }));
    expect(await screen.findByLabelText("Recovery code")).toHaveValue("");
    fireEvent.change(screen.getByLabelText("Recovery code"), { target: { value: "123456" } });
    fireEvent.click(screen.getByRole("button", { name: "Continue recovery" }));
    await waitFor(() => expect(auth.refreshSession).toHaveBeenCalledOnce());
    expect(JSON.parse(fetcher.mock.calls[2][1].body).code).toBe("123456");
  });

  it("renders and submits both profile name parts from the real Kratos settings shape", async () => {
    const flow = browserFlow("settings");
    flow.ui.nodes = [
      flow.ui.nodes[0],
      { group: "profile", type: "text", name: "traits.name.first", value: "Synthetic", required: false, messages: [] },
      { group: "profile", type: "text", name: "traits.name.last", value: "Operator", required: false, messages: [] },
      { group: "profile", type: "submit", name: "method", value: "profile", required: false, messages: [] }
    ];
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }));
    vi.stubGlobal("fetch", fetcher);
    setCsrfToken("synthetic-csrf");
    mount("settings");
    expect(await screen.findByLabelText("First name")).toHaveValue("Synthetic");
    fireEvent.change(screen.getByLabelText("Last name"), { target: { value: "Lovelace" } });
    fireEvent.click(screen.getByRole("button", { name: "Save profile" }));
    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(2));
    expect(JSON.parse(fetcher.mock.calls[1][1].body)).toEqual({
      csrf_token: "upstream-csrf", "traits.name.first": "Synthetic", "traits.name.last": "Lovelace", method: "profile"
    });
  });

  it("renders enabled recovery nodes safely and sends session CSRF for password settings", async () => {
    const flow = browserFlow("settings");
    flow.ui.nodes[2].value = "must-not-render";
    flow.ui.nodes.push({ group: "default", name: "injected", type: "script", value: "<script>alert(1)</script>", required: false, messages: [] });
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }));
    vi.stubGlobal("fetch", fetcher);
    setCsrfToken("synthetic-csrf");
    mount("settings");
    fireEvent.change(await screen.findByLabelText("Email"), { target: { value: "operator@example.test" } });
    expect(screen.getByLabelText("Password")).toHaveValue("");
    expect(document.body.innerHTML).not.toContain("must-not-render");
    expect(document.body.innerHTML).not.toContain("<script>");
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "synthetic-new-password" } });
    fireEvent.click(screen.getByRole("button", { name: "Save password" }));
    expect(await screen.findByText("Password updated.")).toBeInTheDocument();
    expect(fetcher.mock.calls[1][1].headers).toHaveProperty("X-Minicloud-CSRF", "synthetic-csrf");
  });

  it("hides Google unless both runtime capabilities and the flow expose the provider", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({ flow: googleFlow(), session: null })));
    mount("login", false, false);
    await screen.findByLabelText("Email");
    expect(screen.queryByText(/Google/)).not.toBeInTheDocument();
    cleanup();
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({ flow: browserFlow(), session: null })));
    mount("login", false, true);
    await screen.findByLabelText("Email");
    expect(screen.queryByText(/Google/)).not.toBeInTheDocument();
  });

  it("navigates to the validated provider only after an explicit click", async () => {
    const assign = vi.spyOn(browserNavigation, "assign").mockImplementation(() => {});
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow: googleFlow(), session: null }))
      .mockResolvedValueOnce(jsonResponse({ provider: "google", redirectUrl: googleUrl }));
    vi.stubGlobal("fetch", fetcher);
    mount("login", false, true, "/cli/login?session=request-1");
    const button = await screen.findByRole("button", { name: "Continue with Google" });
    expect(fetcher).toHaveBeenCalledTimes(1);
    expect(assign).not.toHaveBeenCalled();
    fireEvent.click(button);
    await waitFor(() => expect(assign).toHaveBeenCalledWith(googleUrl));
    expect(fetcher.mock.calls[1][0]).toContain("/api/account/flows/login/oidc?flow=");
    expect(fetcher.mock.calls[1][0]).toContain("returnTo=%2Fcli%2Flogin%3Fsession%3Drequest-1");
    expect(JSON.parse(fetcher.mock.calls[1][1].body)).toEqual({ csrf_token: "upstream-csrf", provider: "google" });
  });

  it("never navigates to an unexpected provider destination", async () => {
    const assign = vi.spyOn(browserNavigation, "assign").mockImplementation(() => {});
    vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(jsonResponse({ flow: googleFlow("settings"), session: null }))
      .mockResolvedValueOnce(jsonResponse({ provider: "google", redirectUrl: "https://evil.test/o/oauth2/v2/auth" })));
    setCsrfToken("synthetic-csrf");
    mount("settings", false, true);
    fireEvent.click(await screen.findByRole("button", { name: "Link Google account" }));
    expect(await screen.findByRole("alert")).toBeInTheDocument();
    expect(assign).not.toHaveBeenCalled();
  });
});
