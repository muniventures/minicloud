import { Button } from "@/components/ui/button";
import { useAuth } from "./AuthProvider";

export function AccountStatus() {
  const { status, profileError, refreshSession } = useAuth();
  return (
    <div className="flex min-h-screen flex-col items-center justify-center gap-4 px-6 text-center" role="status">
      <p>{status === "initializing" ? "Loading account" :
        status === "setup_required" ? "Instance setup is required. Complete operator setup before signing in." :
        status === "setup_blocked" ? "Instance setup is blocked. Contact the host administrator." :
        profileError ?? "Account service is temporarily unavailable."}</p>
      {status !== "initializing" ? <Button onClick={() => void refreshSession()}>Retry</Button> : null}
    </div>
  );
}
