import { DatabasesModule } from "./modules/Databases/DatabasesModule";
import { DatabaseDetailModule } from "./modules/Databases/DatabaseDetailModule";
import type { RouteObject } from "react-router-dom";
import { Navigate } from "react-router-dom";
import { AppShell } from "./core/layout/AppShell";
import { AuthLayout } from "./core/layout/AuthLayout";
import { ApiKeysModule } from "./modules/ApiKeys/ApiKeysModule";
import { AppDetailModule } from "./modules/Apps/AppDetailModule";
import { AppLandingRedirect } from "./modules/Apps/AppLandingRedirect";
import { AppSettingsModule } from "./modules/Apps/AppSettingsModule";
import { AppsModule } from "./modules/Apps/AppsModule";
import { CreateAppModule } from "./modules/Apps/CreateAppModule";
import { DeploymentDetailModule } from "./modules/Deployments/DeploymentDetailModule";
import { DeploymentsModule } from "./modules/Deployments/DeploymentsModule";
import { OnboardingModule } from "./modules/Onboarding/OnboardingModule";
import { SignInModule } from "./modules/Auth/SignInModule";
import { AccountFlowForm } from "./modules/Auth/AccountFlowForm";
import { ProviderContinuationModule } from "./modules/Auth/ProviderContinuationModule";
import { TeamSettingsModule } from "./modules/Settings/TeamSettingsModule";
import { CliLoginModule } from "./modules/CliLogin/CliLoginModule";
import { SetupModule } from "./modules/Setup/SetupModule";
import { ProviderPlanGuide } from "./modules/Setup/ProviderPlanGuide";

import { ProviderAccountsModule, ProvidersModule } from "./modules/Providers/ProvidersModule";

export const routes: RouteObject[] = [
  { path: "/setup", element: <SetupModule /> },
  {
    element: <AuthLayout />,
    children: [
      { path: "/sign-in", element: <SignInModule /> },
      { path: "/recovery", element: <AccountFlowForm kind="recovery" /> }
    ]
  },
  { path: "/onboarding", element: <OnboardingModule /> },
  { path: "/cli/login", element: <CliLoginModule /> },
  { path: "/sign-in/continue", element: <ProviderContinuationModule /> },
  {
    element: <AppShell />,
    children: [
      { path: "/", element: <Navigate to="/apps" replace /> },
      { path: "/getting-started", element: <ProviderPlanGuide /> },
      { path: "/apps", element: <AppsModule /> },
      { path: "/apps/new", element: <CreateAppModule /> },
      { path: "/databases", element: <DatabasesModule /> },
      { path: "/databases/:databaseId", element: <DatabaseDetailModule /> },
      { path: "/plans", element: <ProvidersModule /> },
      { path: "/plans/accounts", element: <ProviderAccountsModule /> },
      { path: "/providers", element: <Navigate to="/plans" replace /> },
      { path: "/apps/:applicationId/settings", element: <AppSettingsModule /> },
      { path: "/apps/:applicationId", element: <AppLandingRedirect /> },
      { path: "/apps/:applicationId/environments/:environmentId/:tab", element: <AppDetailModule /> },
      { path: "/deployments", element: <DeploymentsModule /> },
      { path: "/deployments/:deploymentNumber", element: <DeploymentDetailModule /> },
      { path: "/settings/api-keys", element: <ApiKeysModule /> },
      { path: "/settings/account", element: <AccountFlowForm kind="settings" returnTo="/settings/account" /> },
      { path: "/settings/team", element: <TeamSettingsModule /> }
    ]
  },
  { path: "*", element: <Navigate to="/apps" replace /> }
];
