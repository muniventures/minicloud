import type { ReactNode } from "react";
import { Navigate, useLocation } from "react-router-dom";
import { Loader2 } from "lucide-react";
import { useAuth } from "./AuthProvider";

export function AuthenticatedArea({ children }: { children: ReactNode }) {
  const location = useLocation();
  const { firebaseUser, isInitializing, isProfileLoading, profile, profileError } = useAuth();

  if (isInitializing || (firebaseUser && isProfileLoading && !profile)) {
    return (
      <div className="flex min-h-screen items-center justify-center text-sm text-muted-foreground">
        <Loader2 className="mr-2 h-4 w-4 animate-spin" />
        Loading workspace
      </div>
    );
  }

  if (!firebaseUser) {
    return <Navigate replace state={{ from: `${location.pathname}${location.search}` }} to="/sign-in" />;
  }

  if (profileError && !profile) {
    return (
      <div className="flex min-h-screen items-center justify-center px-6 text-center text-sm text-destructive">
        {profileError}
      </div>
    );
  }

  if (profile && profile.organizations.length === 0 && location.pathname !== "/onboarding") {
    return <Navigate replace to="/onboarding" />;
  }

  return <>{children}</>;
}
