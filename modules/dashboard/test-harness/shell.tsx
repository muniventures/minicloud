// REVIEW-ONLY harness: mounts the REAL route table (incl. AppShell with
// sidebar) with a mocked transport. Not part of the shipped build.
import { ThemeProvider } from "next-themes";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import "../app/styles.css";
import { auth } from "../app/core/auth/firebase";
import { AuthContext } from "../app/core/auth/AuthProvider";
import { routes } from "../app/routes";
import { installMockTransport } from "./mockTransport";

installMockTransport();

const params = new URLSearchParams(location.search);
const themeParam = params.get("theme");
if (themeParam === "dark") document.documentElement.classList.add("dark");
if (themeParam === "light") document.documentElement.classList.remove("dark");

const fakeUser = { getIdToken: async () => "fake-token", uid: "shot" };
try {
  (auth as unknown as { currentUser: unknown }).currentUser = fakeUser;
} catch {
  Object.defineProperty(auth, "currentUser", { value: fakeUser, configurable: true });
}

const authValue = {
  user: fakeUser,
  firebaseUser: fakeUser,
  loading: false,
  isInitializing: false,
  isProfileLoading: false,
  profile: {
    id: "user_shot",
    email: "shot@example.test",
    displayName: "Shot",
    organizations: [{ id: "org_fixture_01", name: "Fixture Org", slug: "fixture", role: "owner" }],
  },
  selectedOrganization: { id: "org_fixture_01", name: "Fixture Org", slug: "fixture", role: "owner" },
  profileError: null,
  refreshProfile: async () => {},
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
