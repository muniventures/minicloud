import { useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Link } from "react-router-dom";
import type { ApplicationEnvironment, Application } from "@/models/application";
import { appSettingsPath } from "./AppsModule";
import { formatAge } from "./AppsModule";
import { getEnvironment } from "@/api/applicationsApi";
import type { EnvironmentSleep } from "@/models/application";

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
  const [sleepByEnvironment, setSleepByEnvironment] = useState<Record<string, EnvironmentSleep | null>>(() =>
    Object.fromEntries(application.environments.filter(env => env.sleep).map(env => [env.id, env.sleep!])),
  );
  const [sleepStatusLoaded, setSleepStatusLoaded] = useState(false);
  const [sleepStatusLoading, setSleepStatusLoading] = useState(false);
  const [sleepStatusError, setSleepStatusError] = useState<string | null>(null);
  const [unavailableSleepStatuses, setUnavailableSleepStatuses] = useState<string[]>([]);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  async function loadSleepStatuses() {
    if (sleepStatusLoaded || sleepStatusLoading) return;
    setSleepStatusLoading(true);
    setSleepStatusError(null);
    const results = await Promise.all(application.environments.map(async env => {
      if (env.sleep) return { id: env.id, sleep: env.sleep, failed: false };
      try {
        const detail = await getEnvironment(application.id, env.id);
        return { id: env.id, sleep: detail?.sleep ?? null, failed: false };
      } catch {
        return { id: env.id, sleep: null, failed: true };
      }
    }));
    if (mounted.current) {
      setSleepByEnvironment(Object.fromEntries(results.map(result => [result.id, result.sleep])));
      const failedNames = results.filter(result => result.failed)
        .map(result => application.environments.find(env => env.id === result.id)?.name)
        .filter((name): name is string => Boolean(name));
      setUnavailableSleepStatuses(results.filter(result => result.failed).map(result => result.id));
      setSleepStatusError(failedNames.length ? `Sleep status could not be loaded for ${failedNames.join(", ")}.` : null);
      setSleepStatusLoaded(true);
      setSleepStatusLoading(false);
    }
  }

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          <h1 className="max-w-full break-words text-2xl font-semibold tracking-tight">{application.name}</h1>
          <Select value={value} onValueChange={onSwitch} onOpenChange={(open) => { if (open) void loadSleepStatuses(); }}>
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
                        <EnvironmentHint env={env} sleep={sleepByEnvironment[env.id] ?? env.sleep} statusUnavailable={unavailableSleepStatuses.includes(env.id)} />
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
                        <EnvironmentHint env={env} sleep={sleepByEnvironment[env.id] ?? env.sleep} statusUnavailable={unavailableSleepStatuses.includes(env.id)} />
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
      {sleepStatusLoading ? <p role="status" aria-live="polite" className="text-xs text-muted-foreground">Checking environment sleep status…</p> : null}
      {sleepStatusError ? <p role="status" aria-live="polite" className="text-xs text-muted-foreground">{sleepStatusError}</p> : null}
      <p className="sr-only" aria-live="polite">
        Viewing {application.name}, environment {environment.name}, tab {currentTab}.
      </p>
    </div>
  );
}

function EnvironmentHint({ env, sleep, statusUnavailable }: { env: ApplicationEnvironment; sleep?: EnvironmentSleep | null; statusUnavailable: boolean }) {
  if (sleep?.state === "asleep") return <span className="text-xs text-muted-foreground">Asleep</span>;
  if (sleep?.state === "waking") return <span className="text-xs text-muted-foreground">Waking</span>;
  if (statusUnavailable) return <span className="text-xs text-muted-foreground">Sleep status unavailable</span>;
  const obs = env.runtimeObservation;
  if (env.deletionStatus && env.deletionStatus !== "failed") return <span className="text-xs text-muted-foreground">Deleting</span>;
  if (env.deletionStatus === "failed") return <span className="text-xs text-destructive">Deletion failed</span>;
  if (!obs.observedAt) return <span className="text-xs text-muted-foreground">No observation</span>;
  if (obs.stale) return <span className="text-xs text-muted-foreground">Stale · {formatAge(obs.observedAt)}</span>;
  return <span className="text-xs text-muted-foreground">{!obs.connected ? "Disconnected" : obs.state === "running" ? "Running" : obs.state || "Unknown"} · {formatAge(obs.observedAt)}</span>;
}
