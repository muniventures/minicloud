import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../../core/auth/AuthProvider";
import { authFixture } from "../../test/accountFixtures";
import { approveCliLoginSession, getCliLoginSession } from "../../api/cliLoginApi";
import { CliLoginModule } from "./CliLoginModule";

vi.mock("../../api/cliLoginApi", () => ({
  approveCliLoginSession: vi.fn(),
  getCliLoginSession: vi.fn()
}));
afterEach(() => { cleanup(); vi.clearAllMocks(); });
const session = { sessionId: "request-1", status: "pending" as const, scopes: ["api_keys:write" as const], expiresAt: "2099-01-01T00:00:00Z" };
function mount(role = "owner") {
  const auth = authFixture();
  render(<AuthContext.Provider value={{ ...auth, selectedOrganization: { ...auth.selectedOrganization!, role } }}>
    <MemoryRouter initialEntries={["/cli/login?session=request-1"]}><CliLoginModule /></MemoryRouter>
  </AuthContext.Provider>);
}
it("preserves explicit human consent instead of automatically approving after login", async () => {
  vi.mocked(getCliLoginSession).mockResolvedValue(session);
  vi.mocked(approveCliLoginSession).mockResolvedValue({ ...session, status: "approved" });
  mount();
  expect(await screen.findByText("Status: pending")).toBeInTheDocument();
  expect(approveCliLoginSession).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Approve CLI login" }));
  await waitFor(() => expect(approveCliLoginSession).toHaveBeenCalledWith("request-1", "org-1"));
  expect(await screen.findByText("You can return to your terminal.")).toBeInTheDocument();
});
it("does not approve when the membership cannot write API keys", async () => {
  vi.mocked(getCliLoginSession).mockResolvedValue(session);
  mount("developer");
  await screen.findByText("Status: pending");
  expect(screen.getByRole("button", { name: "Approve CLI login" })).toBeDisabled();
  expect(screen.getByRole("alert")).toHaveTextContent("do not have permission");
  expect(approveCliLoginSession).not.toHaveBeenCalled();
});
