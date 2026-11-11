import type { ReactNode } from "react";
import { runtimeConfig } from "@/core/config/runtimeConfig";
import { Navigate, useLocation } from "react-router-dom";
import { useAuth } from "./AuthProvider";
import { safeReturnPath } from "../../api/accountApi";
import { AccountStatus } from "./AccountStatus";

export function GuestArea({ children }: { children: ReactNode }) {
  const { status } = useAuth();
  const location = useLocation();

  if (status === "authenticated") {
    return <Navigate replace to={location.pathname === "/recovery" ? "/settings/account" :
      safeReturnPath((location.state as { from?: unknown } | null)?.from)} />;
  }
  if (status !== "unauthenticated") return <AccountStatus />;

  return (
    <>
      {children}
      <footer className="px-4 py-3 text-center text-sm text-muted-foreground">
        <a className="underline" href={runtimeConfig.sourceUrl}>Source code</a>
      </footer>
    </>
  );
}
