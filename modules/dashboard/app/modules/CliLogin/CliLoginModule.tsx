import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { CheckCircle2, Loader2, Terminal } from "lucide-react";
import { useEffect, useState } from "react";
import { Navigate, useSearchParams } from "react-router-dom";
import { approveCliLoginSession, getCliLoginSession } from "../../api/cliLoginApi";
import { AuthenticatedArea } from "../../core/auth/AuthenticatedArea";
import { useAuth } from "../../core/auth/AuthProvider";
import type { CliLoginSessionResponse } from "../../models/controlPlane";

export function CliLoginModule() {
  return (
    <AuthenticatedArea>
      <CliLoginContent />
    </AuthenticatedArea>
  );
}

function CliLoginContent() {
  const [searchParams] = useSearchParams();
  const sessionId = searchParams.get("session");
  const { selectedOrganization } = useAuth();
  const [session, setSession] = useState<CliLoginSessionResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [isApproving, setIsApproving] = useState(false);

  useEffect(() => {
    if (!sessionId) {
      return;
    }

    getCliLoginSession(sessionId)
      .then(setSession)
      .catch((loadError) => setError(loadError instanceof Error ? loadError.message : "Unable to load CLI login session."));
  }, [sessionId]);

  if (!sessionId) {
    return <Navigate replace to="/apps" />;
  }

  async function approve() {
    if (!sessionId || !selectedOrganization || !["owner", "admin"].includes(selectedOrganization.role)) {
      return;
    }

    setIsApproving(true);
    setError(null);
    try {
      setSession(await approveCliLoginSession(sessionId, selectedOrganization.id));
    } catch (approveError) {
      setError(approveError instanceof Error ? approveError.message : "Unable to approve CLI login.");
    } finally {
      setIsApproving(false);
    }
  }

  const isApproved = session?.status === "approved" || session?.status === "exchanged";
  const isExpired = session?.status === "expired";
  const canApprove = selectedOrganization?.role === "owner" || selectedOrganization?.role === "admin";

  return (
    <div className="flex min-h-screen items-center justify-center bg-background px-4 py-8">
      <Card className="w-full max-w-lg">
        <CardHeader>
          <div className="mb-2 flex h-10 w-10 items-center justify-center rounded-md bg-primary text-primary-foreground">
            {isApproved ? <CheckCircle2 className="h-5 w-5" /> : <Terminal className="h-5 w-5" />}
          </div>
          <CardTitle>Minicloud CLI login</CardTitle>
          <CardDescription>{selectedOrganization ? `Authorize CLI access for ${selectedOrganization.name}.` : "Choose an organization before approving CLI access."}</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {session ? (
            <div className="rounded-md border border-border p-3 text-sm">
              <div>Status: {session.status}</div>
              <div>Expires: {formatDate(session.expiresAt)}</div>
              <div>Scopes: {session.scopes.join(", ")}</div>
            </div>
          ) : (
            <div className="flex items-center text-sm text-muted-foreground">
              <Loader2 className="mr-2 h-4 w-4 animate-spin" />
              Loading CLI request
            </div>
          )}
          {error ? <p className="text-sm text-destructive">{error}</p> : null}
          {!canApprove ? <p role="alert">You do not have permission to approve CLI access.</p> : null}
          {isApproved ? <p className="text-sm text-muted-foreground">You can return to your terminal.</p> : null}
          {isExpired ? <p className="text-sm text-destructive">This CLI login session expired. Run `minicloud login` again.</p> : null}
          {!isApproved && !isExpired ? (
            <Button className="w-full" disabled={!session || !canApprove || isApproving} onClick={approve}>
              {isApproving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Terminal className="h-4 w-4" />}
              Approve CLI login
            </Button>
          ) : null}
        </CardContent>
      </Card>
    </div>
  );
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}
