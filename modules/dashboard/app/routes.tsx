import { DatabasesModule } from "./modules/Databases/DatabasesModule";
import { DatabaseDetailModule } from "./modules/Databases/DatabaseDetailModule";
import type { RouteObject } from "react-router-dom";
import { Navigate } from "react-router-dom";
import { AppShell } from "./core/layout/AppShell";
import { AuthLayout } from "./core/layout/AuthLayout";
import { ApiKeysModule } from "./modules/ApiKeys/ApiKeysModule";
import { AppDetailModule } from "./modules/Apps/AppDetailModule";
import { AppsModule } from "./modules/Apps/AppsModule";
import { CreateAppModule } from "./modules/Apps/CreateAppModule";
import { DeploymentDetailModule } from "./modules/Deployments/DeploymentDetailModule";
import { DeploymentsModule } from "./modules/Deployments/DeploymentsModule";
import { OnboardingModule } from "./modules/Onboarding/OnboardingModule";
import { SignInModule } from "./modules/Auth/SignInModule";
import { SignUpModule } from "./modules/Auth/SignUpModule";
import { TeamSettingsModule } from "./modules/Settings/TeamSettingsModule";
import { CliLoginModule } from "./modules/CliLogin/CliLoginModule";

import { ProvidersModule } from "./modules/Providers/ProvidersModule";

export const routes: RouteObject[] = [
  {
    element: <AuthLayout />,
    children: [
      { path: "/sign-in", element: <SignInModule /> },
      { path: "/sign-up", element: <SignUpModule /> }
    ]
  },
  { path: "/onboarding", element: <OnboardingModule /> },
  { path: "/cli/login", element: <CliLoginModule /> },
  {
    element: <AppShell />,
    children: [
      { path: "/", element: <Navigate to="/apps" replace /> },
      { path: "/apps", element: <AppsModule /> },
      { path: "/apps/new", element: <CreateAppModule /> },
      { path: "/databases", element: <DatabasesModule /> },
      { path: "/databases/:databaseId", element: <DatabaseDetailModule /> },
      { path: "/providers", element: <ProvidersModule /> },
      { path: "/apps/:appId", element: <AppDetailModule /> },
      { path: "/apps/:appId/deployments", element: <DeploymentsModule /> },
      { path: "/apps/:appId/deployments/:deploymentId", element: <DeploymentDetailModule /> },
      { path: "/deployments", element: <DeploymentsModule /> },
      { path: "/deployments/:deploymentId", element: <DeploymentDetailModule /> },
      { path: "/settings/api-keys", element: <ApiKeysModule /> },
      { path: "/settings/team", element: <TeamSettingsModule /> }
    ]
  },
  { path: "*", element: <Navigate to="/apps" replace /> }
];
