import type { ReactNode } from "react";
import { Navigate, useLocation } from "react-router-dom";
import { useAuth } from "./AuthProvider";
import { AccountStatus } from "./AccountStatus";

export function AuthenticatedArea({ children }: { children: ReactNode }) {
  const location = useLocation();
  const { status } = useAuth();

  if (status === "unauthenticated") {
    return <Navigate replace state={{ from: `${location.pathname}${location.search}` }} to="/sign-in" />;
  }

  if (status !== "authenticated") return <AccountStatus />;

  return <>{children}</>;
}
