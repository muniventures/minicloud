import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Link } from "react-router-dom";
import type { ApplicationEnvironment, Application } from "@/models/application";
import { appSettingsPath } from "./AppsModule";
import { formatAge } from "./AppsModule";

type Props = {
  application: Application;
  environment: ApplicationEnvironment;
  currentTab: string;
  onSwitch: (environmentId: string) => void;
  onRefresh: () => void;
  loading: boolean;
  refreshing: boolean;
  moreActions?: React.ReactNode;
  canManage?: boolean;
};

const PERSISTENT_LABEL = "Persistent environments";
const PREVIEW_LABEL = "Previews";

export function EnvironmentScopeHeader({
  application,
  environment,
  currentTab,
  onSwitch,
  onRefresh,
  loading,
  refreshing,
  moreActions,
  canManage = false,
}: Props) {
  const persistents = application.environments.filter((e) => e.kind !== "preview");
  const previews = application.environments.filter((e) => e.kind === "preview");
  const value = environment.id;

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          <h1 className="max-w-full break-words text-2xl font-semibold tracking-tight">{application.name}</h1>
          <Select value={value} onValueChange={onSwitch}>
            <SelectTrigger
              aria-label={`Environment for ${application.name}`}
              className="w-auto min-w-0 max-w-full sm:min-w-44"
            >
              <SelectValue>{environment.name}</SelectValue>
            </SelectTrigger>
            <SelectContent>
              {persistents.length ? (
                <SelectGroup>
                  <SelectLabel>{PERSISTENT_LABEL}</SelectLabel>
                  {persistents.map((env) => (
                    <SelectItem key={env.id} value={env.id} disabled={!!env.deletionStatus && env.deletionStatus !== "failed"}>
                      <span className="flex items-center gap-2">
                        <span>{env.name}</span>
                        <EnvironmentHint env={env} />
                      </span>
                    </SelectItem>
                  ))}
                </SelectGroup>
              ) : null}
              {previews.length ? (
                <SelectGroup>
                  <SelectLabel>{PREVIEW_LABEL}</SelectLabel>
                  {previews.map((env) => (
                    <SelectItem key={env.id} value={env.id} disabled={!!env.deletionStatus && env.deletionStatus !== "failed"}>
                      <span className="flex items-center gap-2">
                        <span>{env.name}</span>
                        <EnvironmentHint env={env} />
                      </span>
                    </SelectItem>
                  ))}
                </SelectGroup>
              ) : null}
            </SelectContent>
          </Select>
          {environment.kind === "preview" ? <span className="rounded bg-muted px-2 py-1 text-xs">Preview</span> : null}
          <span className="text-sm">
            {canManage && !application.deletionStatus ? <>
              <Link className="text-primary hover:underline" to={`${appSettingsPath(application.id)}?addEnvironment=1`}>
                Add environment
              </Link>
              {" · "}
            </> : null}
            <Link
              className="text-primary hover:underline"
              to={appSettingsPath(application.id)}
            >
              Manage environments
            </Link>
          </span>
        </div>
        <div className="flex items-center gap-2">
          <Button
            size="sm"
            variant="outline"
            disabled={loading || refreshing}
            onClick={onRefresh}
          >
            {refreshing ? "Refreshing…" : `Refresh ${application.name}`}
          </Button>
          {moreActions}
        </div>
      </div>
      <p className="text-sm text-muted-foreground">
        <span>
          {application.slug} · plan {environment.plan} · database {environment.database}
        </span>
        <span className="ml-2">In environment {environment.name}.</span>
      </p>
      <p className="sr-only" aria-live="polite">
        Viewing {application.name}, environment {environment.name}, tab {currentTab}.
      </p>
    </div>
  );
}

function EnvironmentHint({ env }: { env: ApplicationEnvironment }) {
  const obs = env.runtimeObservation;
  if (env.deletionStatus && env.deletionStatus !== "failed") return <span className="text-xs text-muted-foreground">Deleting</span>;
  if (env.deletionStatus === "failed") return <span className="text-xs text-destructive">Deletion failed</span>;
  if (!obs.observedAt) return <span className="text-xs text-muted-foreground">No observation</span>;
  if (obs.stale) return <span className="text-xs text-muted-foreground">Stale · {formatAge(obs.observedAt)}</span>;
  return <span className="text-xs text-muted-foreground">{!obs.connected ? "Disconnected" : obs.state === "running" ? "Running" : obs.state || "Unknown"} · {formatAge(obs.observedAt)}</span>;
}
