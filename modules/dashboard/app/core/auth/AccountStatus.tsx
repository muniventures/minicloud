import { Button } from "@/components/ui/button";
import { useAuth } from "./AuthProvider";
import { Link } from "react-router-dom";

export function AccountStatus() {
  const { status, profileError, refreshSession } = useAuth();
  return (
    <div className="flex min-h-screen flex-col items-center justify-center gap-4 px-6 text-center" role="status">
      <p>{status === "initializing" ? "Loading account" :
        status === "setup_required" ? "Instance setup is required. Complete operator setup before signing in." :
        status === "setup_blocked" ? "Instance setup is blocked. Contact the host administrator." :
        profileError ?? "Account service is temporarily unavailable."}</p>
      {status !== "initializing" ? <Button onClick={() => void refreshSession()}>Retry</Button> : null}
      {status === "setup_required" ? <Link className="underline" to="/setup">Complete first operator setup</Link> : null}
    </div>
  );
}
