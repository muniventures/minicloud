import { AppWindow, Database, KeyRound, Rocket, Server, Settings, Users } from "lucide-react";
import type { ComponentType } from "react";

export type DashboardRouteLayout = "guest" | "main" | "standalone";

export type DashboardRouteDefinition = {
  key: string;
  name: string;
  path: string;
  layout: DashboardRouteLayout;
  nav?: {
    label: string;
    icon: ComponentType<{ className?: string }>;
    group: "main" | "settings";
  };
};

function defineRoute(route: DashboardRouteDefinition) {
  return route;
}

export const dashboardRoutes = {
  setup: defineRoute({ key: "setup", name: "First operator setup", path: "/setup", layout: "standalone" }),
  gettingStarted: defineRoute({ key: "gettingStarted", name: "Provider and plan guide", path: "/getting-started", layout: "main" }),
  signIn: defineRoute({ key: "signIn", name: "Sign in", path: "/sign-in", layout: "guest" }),
  recovery: defineRoute({ key: "recovery", name: "Recover account", path: "/recovery", layout: "guest" }),
  onboarding: defineRoute({ key: "onboarding", name: "Onboarding", path: "/onboarding", layout: "standalone" }),
  apps: defineRoute({ key: "apps", name: "Apps", path: "/apps", layout: "main", nav: { label: "Apps", icon: AppWindow, group: "main" } }),
  databases: defineRoute({ key: "databases", name: "Databases", path: "/databases", layout: "main", nav: { label: "Databases", icon: Database, group: "main" } }),
  databaseDetail: defineRoute({ key: "databaseDetail", name: "Database", path: "/databases/:databaseId", layout: "main" }),
  appCreate: defineRoute({ key: "appCreate", name: "Create app", path: "/apps/new", layout: "main" }),
  plans: defineRoute({ key: "plans", name: "Plans", path: "/plans", layout: "main", nav: { label: "Plans", icon: Server, group: "main" } }),
  providerAccounts: defineRoute({ key: "providerAccounts", name: "Provider accounts", path: "/plans/accounts", layout: "main" }),
  providers: defineRoute({ key: "providers", name: "Providers", path: "/providers", layout: "main" }),
  appDetail: defineRoute({ key: "appDetail", name: "Application", path: "/apps/:applicationId", layout: "main" }),
  environmentDetail: defineRoute({ key: "environmentDetail", name: "Environment", path: "/apps/:applicationId/environments/:environmentId/:tab", layout: "main" }),
  deployments: defineRoute({
    key: "deployments",
    name: "Deployments",
    path: "/deployments",
    layout: "main",
    nav: { label: "Deployments", icon: Rocket, group: "main" }
  }),
  deploymentDetail: defineRoute({ key: "deploymentDetail", name: "Deployment detail", path: "/deployments/:deploymentId", layout: "main" }),
  apiKeys: defineRoute({
    key: "apiKeys",
    name: "API Keys",
    path: "/settings/api-keys",
    layout: "main",
    nav: { label: "API Keys", icon: KeyRound, group: "settings" }
  }),
  teamSettings: defineRoute({
    key: "teamSettings",
    name: "Team",
    path: "/settings/team",
    layout: "main",
    nav: { label: "Team", icon: Users, group: "settings" }
  }),
  settings: defineRoute({
    key: "settings",
    name: "Settings",
    path: "/settings/account",
    layout: "main"
  })
} as const;

export const navigationRoutes = Object.values(dashboardRoutes).filter((route) => route.nav?.group === "main");
export const settingsRoutes = Object.values(dashboardRoutes).filter((route) => route.nav?.group === "settings");

export function buildDashboardPath(route: DashboardRouteDefinition, params: Record<string, string> = {}) {
  return Object.entries(params).reduce((path, [key, value]) => path.replace(`:${key}`, encodeURIComponent(value)), route.path);
}
