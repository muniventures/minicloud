// REVIEW-ONLY harness: mounts the REAL route table (incl. AppShell with
// sidebar) with a mocked transport. Not part of the shipped build.
import { ThemeProvider } from "next-themes";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import "../app/styles.css";
import { setCsrfToken } from "../app/api/apiClient";
import { AuthContext } from "../app/core/auth/AuthProvider";
import { routes } from "../app/routes";
import { installMockTransport } from "./mockTransport";

installMockTransport();

const params = new URLSearchParams(location.search);
const themeParam = params.get("theme");
if (themeParam === "dark") document.documentElement.classList.add("dark");
if (themeParam === "light") document.documentElement.classList.remove("dark");

setCsrfToken("synthetic-csrf");

const authValue = {
  status: "authenticated",
  capabilities: { password: true, google: false, emailRecovery: false, bootstrapState: "ready" },
  profile: {
    id: "user_shot",
    email: "shot@example.test",
    displayName: "Shot",
    organizations: [{ id: "org_fixture_01", name: "Fixture Org", slug: "fixture", role: "owner" }],
  },
  selectedOrganization: { id: "org_fixture_01", name: "Fixture Org", slug: "fixture", role: "owner" },
  profileError: null,
  refreshProfile: async () => {},
  refreshSession: async () => {},
  logout: async () => {},
};

const initialRoute = params.get("route") ?? "/apps";
const router = createMemoryRouter(routes, { initialEntries: [initialRoute] });

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <ThemeProvider
      attribute="class"
      defaultTheme={themeParam === "dark" ? "dark" : themeParam === "light" ? "light" : "system"}
      enableSystem
    >
      <AuthContext.Provider value={authValue as never}>
        <RouterProvider router={router} />
      </AuthContext.Provider>
    </ThemeProvider>
  </StrictMode>,
);
