import { useCallback, useEffect, useState } from "react";
import { Navigate, useParams } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { getApplication } from "../../api/applicationsApi";
import { appsFixtureApplications, appsFixturesEnabled } from "../../api/applicationsFixtures";
import { useAuth } from "../../core/auth/AuthProvider";

// Application landing routes use only the canonical application identity.
export function AppLandingRedirect() {
  const { applicationId } = useParams();
  const { selectedOrganization } = useAuth();
  return <ScopedAppLanding key={`${selectedOrganization?.id}:${applicationId}`} />;
}

function ScopedAppLanding() {
  const { applicationId } = useParams();
  const { selectedOrganization } = useAuth();
  const [target, setTarget] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);

  const retry = useCallback(() => {
    setError(null);
    setAttempt((n) => n + 1);
  }, []);

  useEffect(() => {
    if (!applicationId) return;
    let live = true;
    (async () => {
      try {
        const app = appsFixturesEnabled()
          ? appsFixtureApplications().find((item) => item.id === applicationId)
          : await getApplication(applicationId);
        if (!live) return;
        if (!app) throw new Error("Application was not found.");
        if (app.organizationId !== selectedOrganization?.id) throw new Error("Application does not belong to the selected organization.");
        const env = app.environments.find((item) => item.id === app.defaultEnvironmentId);
        setTarget(env
          ? `/apps/${encodeURIComponent(app.id)}/environments/${encodeURIComponent(env.id)}/overview`
          : `/apps/${encodeURIComponent(app.id)}/settings?addEnvironment=1`);
      } catch (e) {
        if (live) setError(e instanceof Error ? e.message : "Unable to load application.");
      }
    })();
    return () => {
      live = false;
    };
  }, [applicationId, attempt, selectedOrganization?.id]);

  if (error) {
    return (
      <div role="alert" className="text-destructive">
        <p>{error}</p>
        <Button variant="outline" size="sm" onClick={retry}>Retry</Button>
      </div>
    );
  }
  if (!target) return <p className="text-sm text-muted-foreground">Loading application…</p>;
  return <Navigate to={target} replace />;
}
