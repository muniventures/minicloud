import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { getEnvironment, updateEnvironment } from "@/api/applicationsApi";
import type { ApplicationEnvironment } from "@/models/application";

const TIMEOUT_OPTIONS = [
  { value: "30", label: "30 minutes" },
  { value: "60", label: "1 hour" },
  { value: "120", label: "2 hours" },
  { value: "240", label: "4 hours" },
  { value: "480", label: "8 hours" },
  { value: "1440", label: "1 day" },
  { value: "custom", label: "Custom" },
];

export function EnvironmentSleepSettings({
  applicationId,
  environment,
  canManage,
  onSaved,
}: {
  applicationId: string;
  environment: ApplicationEnvironment;
  canManage: boolean;
  onSaved: () => Promise<void>;
}) {
  const [detailEnvironment, setDetailEnvironment] = useState(environment);
  const [loading, setLoading] = useState(!environment.sleep);
  const [loadFailed, setLoadFailed] = useState(false);
  const savedPolicy = detailEnvironment.sleep?.policy;
  const [enabled, setEnabled] = useState(savedPolicy?.sleepAfterMinutes != null);
  const [timeout, setTimeout] = useState(String(savedPolicy?.sleepAfterMinutes ?? 120));
  const [customTimeout, setCustomTimeout] = useState("120");
  const [httpTrafficIsActivity, setHttpTrafficIsActivity] = useState(savedPolicy?.httpTrafficIsActivity ?? true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (environment.sleep) {
      setDetailEnvironment(environment);
      setLoading(false);
      setLoadFailed(false);
      return;
    }
    let live = true;
    setLoading(true);
    setLoadFailed(false);
    Promise.resolve()
      .then(() => getEnvironment(applicationId, environment.id))
      .then(detail => {
        if (!detail) throw new Error("The environment response did not include sleep settings.");
        if (live) {
          setDetailEnvironment(detail);
          setLoadFailed(false);
          setError(null);
        }
      })
      .catch(loadError => {
        if (live) {
          setError(loadError instanceof Error ? loadError.message : "Unable to load sleep settings.");
          setLoadFailed(true);
        }
      })
      .finally(() => {
        if (live) setLoading(false);
      });
    return () => { live = false; };
  }, [applicationId, environment.id, environment.sleep]);

  useEffect(() => {
    const minutes = savedPolicy?.sleepAfterMinutes ?? 120;
    setEnabled(savedPolicy?.sleepAfterMinutes != null);
    setTimeout(TIMEOUT_OPTIONS.some(option => option.value === String(minutes)) ? String(minutes) : "custom");
    setCustomTimeout(String(minutes));
    setHttpTrafficIsActivity(savedPolicy?.httpTrafficIsActivity ?? true);
  }, [environment.id, savedPolicy?.sleepAfterMinutes, savedPolicy?.httpTrafficIsActivity]);

  const eligible = detailEnvironment.sleep?.eligible !== false;
  const selectedMinutes = timeout === "custom" ? Number(customTimeout) : Number(timeout);
  const validTimeout = Number.isInteger(selectedMinutes) && selectedMinutes >= 30 && selectedMinutes <= 43_200;
  const savedMinutes = savedPolicy?.sleepAfterMinutes ?? 120;
  const dirty =
    enabled !== (savedPolicy?.sleepAfterMinutes != null) ||
    (enabled && selectedMinutes !== savedMinutes) ||
    httpTrafficIsActivity !== (savedPolicy?.httpTrafficIsActivity ?? true);

  async function save() {
    if (!dirty || !canManage || !eligible || loading || loadFailed || (enabled && !validTimeout)) return;
    setSaving(true);
    setError(null);
    try {
      const updated = await updateEnvironment(applicationId, environment.id, {
        sleep: {
          policy: {
            enabled,
            ...(enabled ? { sleepAfterMinutes: selectedMinutes } : {}),
            httpTrafficIsActivity,
            revision: savedPolicy?.revision ?? 0,
          },
        },
      });
      if (!updated) throw new Error("The environment response did not include the updated sleep policy.");
      setDetailEnvironment(updated);
      await onSaved();
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : "Unable to update the sleep policy.");
    } finally {
      setSaving(false);
    }
  }

  const modeChange = detailEnvironment.sleep?.retainedAddress?.previousIpv4 &&
    detailEnvironment.sleep.retainedAddress.previousIpv4 !== detailEnvironment.sleep.retainedAddress.ipv4;

  return (
    <div className="grid min-w-0 gap-3 rounded-md border p-3 text-sm">
      <p className="text-muted-foreground">
        When idle, Selfrun stops the services, snapshots the server, and deletes it. Waking takes several minutes. Your provider bills for snapshot storage.
      </p>
      {loading ? <p role="status" className="text-xs text-muted-foreground">Loading sleep settings…</p> : null}
      <fieldset disabled={!canManage || !eligible || loading || loadFailed || saving} className="grid min-w-0 gap-3">
        <legend className="sr-only">Sleep policy for {environment.name}</legend>
        <label className="flex min-w-0 items-start gap-2">
          <input
            type="checkbox"
            role="switch"
            aria-label="Sleep when idle"
            checked={enabled}
            onChange={(event) => setEnabled(event.target.checked)}
            className="mt-1 shrink-0 accent-primary"
          />
          <span>
            <span className="block font-medium">Sleep when idle</span>
            <span className="block text-xs text-muted-foreground">Sleep is opt-in. The default timeout is 2 hours.</span>
          </span>
        </label>

        <div className="grid min-w-0 gap-2 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)] sm:items-end">
          <div className="grid min-w-0 gap-1">
            <Label htmlFor={`sleep-timeout-${environment.id}`}>Idle timeout</Label>
            <select
              id={`sleep-timeout-${environment.id}`}
              className="h-10 min-w-0 max-w-full rounded-md border bg-background px-3 text-sm"
              value={timeout}
              disabled={!enabled}
              onChange={(event) => setTimeout(event.target.value)}
            >
              {TIMEOUT_OPTIONS.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}
            </select>
          </div>
          {timeout === "custom" ? (
            <div className="grid min-w-0 gap-1">
              <Label htmlFor={`sleep-custom-timeout-${environment.id}`}>Custom timeout in minutes</Label>
              <input
                id={`sleep-custom-timeout-${environment.id}`}
                type="number"
                min={30}
                max={43_200}
                step={1}
                className="h-10 min-w-0 max-w-full rounded-md border bg-background px-3 text-sm"
                value={customTimeout}
                disabled={!enabled}
                onChange={(event) => setCustomTimeout(event.target.value)}
              />
              {enabled && !validTimeout ? <p className="text-xs text-destructive">Choose a whole number from 30 to 43,200 minutes.</p> : null}
            </div>
          ) : null}
        </div>

        <div className="grid gap-1">
          <label className="flex min-w-0 items-start gap-2">
            <input
              type="checkbox"
              role="switch"
              aria-label="Count website traffic as activity"
              checked={httpTrafficIsActivity}
              disabled={!enabled}
              onChange={(event) => setHttpTrafficIsActivity(event.target.checked)}
              className="mt-1 shrink-0 accent-primary"
            />
            <span className="font-medium">Count website traffic as activity</span>
          </label>
          <p className="pl-6 text-xs text-muted-foreground">
            When off, the environment sleeps after the timeout even if people or monitors are using it.
          </p>
        </div>
      </fieldset>

      {detailEnvironment.sleep?.eligible === false ? (
        <p role="status" className="text-xs text-muted-foreground">
          Sleep controls are unavailable: {detailEnvironment.sleep.ineligibleReason ?? "this environment is not eligible."}
        </p>
      ) : null}
      {!canManage ? <p role="status" className="text-xs text-muted-foreground">You need environment-management access to change this policy.</p> : null}

      {detailEnvironment.sleep?.addressMode === "dynamic" ? (
        <p className="rounded-md bg-muted/50 p-3 text-xs">
          Selfrun updates its managed DNS records when the environment wakes. On DigitalOcean, restoring the server from a snapshot may give it a different IP address.
        </p>
      ) : detailEnvironment.sleep?.addressMode === "retained" ? (
        <p className="rounded-md bg-muted/50 p-3 text-xs">
          Custom domains need a fixed address. Selfrun keeps a reserved IP for this environment; on DigitalOcean it may differ from the server&apos;s previous IP. The provider bills it while the environment is asleep.
        </p>
      ) : null}

      {modeChange ? (
        <div role="status" aria-live="polite" className="grid min-w-0 gap-2 rounded-md border border-amber-500/50 bg-amber-500/5 p-3 text-xs">
          <p className="font-medium">Manual DNS update required</p>
          <p className="break-words">
            The reserved address changed from <code>{detailEnvironment.sleep?.retainedAddress?.previousIpv4}</code> to{" "}
            <code>{detailEnvironment.sleep?.retainedAddress?.ipv4}</code>. Update each verified custom domain&apos;s A record at its DNS provider to the new address. Selfrun checks that custom domains resolve to it before sleep starts; managed Selfrun hostnames update automatically. Do not add an AAAA record for this sleep-enabled environment.
          </p>
          <Link
            className="w-fit text-primary underline underline-offset-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            to={`/apps/${encodeURIComponent(applicationId)}/environments/${encodeURIComponent(environment.id)}/domains`}
          >
            Review environment domains
          </Link>
        </div>
      ) : null}

      {error ? <p role="alert" className="break-words text-xs text-destructive">{error}</p> : null}
      <div className="flex flex-wrap gap-2">
        <Button type="button" size="sm" disabled={!dirty || !canManage || !eligible || loading || loadFailed || saving || (enabled && !validTimeout)} onClick={() => void save()}>
          {saving ? "Saving…" : "Save sleep settings"}
        </Button>
        {dirty ? (
          <Button
            type="button"
            size="sm"
            variant="outline"
            disabled={saving}
            onClick={() => {
              const minutes = savedPolicy?.sleepAfterMinutes ?? 120;
              setEnabled(savedPolicy?.sleepAfterMinutes != null);
              setTimeout(TIMEOUT_OPTIONS.some(option => option.value === String(minutes)) ? String(minutes) : "custom");
              setCustomTimeout(String(minutes));
              setHttpTrafficIsActivity(savedPolicy?.httpTrafficIsActivity ?? true);
              setError(null);
            }}
          >
            Discard changes
          </Button>
        ) : null}
      </div>
    </div>
  );
}
