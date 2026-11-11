import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../auth/AuthProvider";
import { SidebarNavigation } from "./SidebarNavigation";

const logout = vi.fn();

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("SidebarNavigation", () => {
  it("opens the footer user menu and signs out", async () => {
    render(
      <AuthContext.Provider
        value={{
          status: "authenticated",
          capabilities: { password: true, google: false, emailRecovery: false, bootstrapState: "ready" },
          logout,
          refreshSession: async () => {},
          selectOrganization: () => false,
          profile: {
            id: "user-1",
            email: "muni@example.test",
            displayName: "Example Developer",
            organizations: [],
          },
          selectedOrganization: { id: "org-1", name: "MuniVentures" } as never,
          profileError: null,
          refreshProfile: async () => {},
        }}
      >
        <MemoryRouter initialEntries={["/apps"]}>
          <SidebarNavigation onNavigate={() => {}} />
        </MemoryRouter>
      </AuthContext.Provider>,
    );

    expect(screen.getByRole("button", { name: /Minicloud.*MuniVentures/i })).toBeInTheDocument();
    expect(screen.getByText("Platform")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Apps" })).toHaveAttribute("aria-current", "page");
    fireEvent.pointerDown(screen.getByRole("button", { name: /example developer.*muni@example.test/i }), {
      button: 0,
      ctrlKey: false,
    });
    expect(await screen.findByRole("menuitem", { name: "Settings" })).toHaveAttribute(
      "href",
      "/settings/account",
    );
    fireEvent.click(screen.getByRole("menuitem", { name: "Log out" }));

    expect(logout).toHaveBeenCalledOnce();
  });
});
