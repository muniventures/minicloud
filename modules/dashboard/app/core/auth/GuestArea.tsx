import type { ReactNode } from "react";
import { runtimeConfig } from "@/core/config/runtimeConfig";
import { Navigate } from "react-router-dom";
import { useAuth } from "./AuthProvider";

export function GuestArea({ children }: { children: ReactNode }) {
  const { firebaseUser, isInitializing } = useAuth();

  if (isInitializing) {
    return null;
  }

  if (firebaseUser) {
    return <Navigate replace to="/apps" />;
  }

  return (
    <>
      {children}
      <footer className="px-4 py-3 text-center text-sm text-muted-foreground">
        <a className="underline" href={runtimeConfig.sourceUrl}>Source code</a>
      </footer>
    </>
  );
}
