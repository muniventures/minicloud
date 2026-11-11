import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Link, Navigate, useSearchParams } from "react-router-dom";
import { parseProviderContinuation } from "../../api/accountApi";
import { useAuth } from "../../core/auth/AuthProvider";
import { AccountStatus } from "../../core/auth/AccountStatus";
import { AccountFlowForm } from "./AccountFlowForm";

const failureMessages = {
  failed: "Google sign-in did not complete. Try again or use your password.",
  forbidden: "This Google account is not admitted to this Minicloud instance.",
  unavailable: "Account service is unavailable. Retry when it is available."
} as const;

/** Landing route for the API's fixed Google callback; it never follows provider URLs itself. */
export function ProviderContinuationModule() {
  const [search] = useSearchParams();
  const { status } = useAuth();
  const continuation = parseProviderContinuation(search);

  if (continuation.result === "success") {
    // The callback already rotated the browser session; CLI consent and other targets keep their own explicit actions.
    if (status === "authenticated") return <Navigate replace to={continuation.returnTo} />;
    if (status !== "unauthenticated") return <AccountStatus />;
  }
  const message = continuation.result === "success"
    ? "Sign in could not be confirmed. Sign in again."
    : failureMessages[continuation.result];

  return (
    <main className="flex min-h-screen items-center justify-center bg-background px-4 py-10">
      {continuation.kind === "login" && status === "unauthenticated" ? (
        <AccountFlowForm kind="login" returnTo={continuation.returnTo} flowId={continuation.flowId ?? undefined} notice={message} />
      ) : (
        <Card className="w-full max-w-md">
          <CardHeader>
            <CardTitle>{continuation.kind === "settings" ? "Account settings" : "Sign in"}</CardTitle>
            <CardDescription>{status === "initializing" ? "Loading account" : "Access your Minicloud account."}</CardDescription>
          </CardHeader>
          <CardContent className="space-y-4">
            <p role="alert" className="text-sm text-destructive">{message}</p>
            <Link className="block underline" to={continuation.kind === "settings" ? "/settings/account" : "/sign-in"}
              state={{ from: continuation.returnTo }}>
              {continuation.kind === "settings" ? "Return to account settings" : "Return to sign in"}
            </Link>
          </CardContent>
        </Card>
      )}
    </main>
  );
}
