import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { sleepEnvironment, wakeEnvironment } from "@/api/applicationsApi";
import type { ApplicationEnvironment, EnvironmentSleep } from "@/models/application";

type Props = {
  applicationId: string;
  applicationName: string;
  environment: ApplicationEnvironment;
  onRefresh: () => Promise<void>;
};

export function EnvironmentSleepStatus({ applicationId, applicationName, environment, onRefresh }: Props) {
  const sleep = environment.sleep;
  const [dialog, setDialog] = useState<"sleep" | "wake" | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [pendingOperation, setPendingOperation] = useState<NonNullable<EnvironmentSleep["operation"]> | null>(null);
  const [now, setNow] = useState(Date.now());

  useEffect(() => {
    if (!sleep) return;
    const timer = setInterval(() => setNow(Date.now()), 60_000);
    return () => clearInterval(timer);
  }, [sleep]);

  useEffect(() => {
    const queuedSleepPreventedByWake =
      pendingOperation?.kind === "sleep" &&
      sleep?.state === "awake" &&
      sleep.operation == null &&
      sleep.keepAwakeUntil != null &&
      Date.parse(sleep.keepAwakeUntil) > Date.now();
    if (pendingOperation && (sleep?.operation?.id === pendingOperation.id || queuedSleepPreventedByWake))
      setPendingOperation(null);
  }, [pendingOperation, sleep?.operation?.id, sleep?.state, sleep?.keepAwakeUntil]);

  useEffect(() => {
    setPendingOperation(null);
    setDialog(null);
    setError(null);
    setNotice(null);
  }, [applicationId, environment.id]);

  const operation = sleep?.operation ?? pendingOperation;
  const activeOperation = operation && ["queued", "running"].includes(operation.status);
  const pendingState = pendingOperation && sleep?.operation?.id !== pendingOperation.id
    ? pendingOperation.kind === "sleep" ? "sleeping" : "waking"
    : activeOperation
      ? operation.kind === "sleep" ? "sleeping" : "waking"
      : sleep?.state;
  const shouldPoll = pendingOperation !== null || activeOperation || sleep?.state === "sleeping" || sleep?.state === "waking";

  useEffect(() => {
    if (!shouldPoll) return;
    const timer = setTimeout(() => void onRefresh(), 3_000);
    return () => clearTimeout(timer);
  }, [onRefresh, shouldPoll, operation?.status, sleep?.state]);

  if (!sleep) return null;

  const asleep = pendingState === "asleep";
  const inProgress = pendingState === "sleeping" || pendingState === "waking" || Boolean(activeOperation);
  const failed = pendingState === "sleep_failed" || pendingState === "wake_failed";
  const enabled = sleep.policy.sleepAfterMinutes !== null;
  const ineligible = !sleep.eligible;

  async function runAction(action: "sleep" | "wake" | "keep-awake") {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      if (action === "sleep") {
        const result = await sleepEnvironment(applicationId, environment.id);
        setPendingOperation(result.operation);
        setDialog(null);
        setNotice(`Sleep requested for ${applicationName} / ${environment.name}.`);
      } else {
        const result = await wakeEnvironment(applicationId, environment.id, 240);
        if ("state" in result) {
          setNotice(`${environment.name} will stay awake for 4 hours.`);
        } else {
          setPendingOperation(result.operation);
          setNotice(`Wake requested for ${applicationName} / ${environment.name}.`);
        }
        setDialog(null);
      }
      await onRefresh();
    } catch (actionError) {
      setError(actionError instanceof Error ? actionError.message : "Unable to update the environment power state.");
    } finally {
      setBusy(false);
    }
  }

  const statusText = pendingOperation && sleep.operation?.id !== pendingOperation.id
    ? `${pendingState === "sleeping" ? "Sleeping" : "Waking"} request queued…`
    : statusDescription(sleep, now, operation);
  return (
    <Card>
      <CardHeader>
        <CardTitle>Environment sleep</CardTitle>
      </CardHeader>
      <CardContent className="grid min-w-0 gap-3 text-sm">
        <div role="status" aria-live="polite" className="min-w-0 break-words">
          <p className="font-medium">{statusText}</p>
          {inProgress && operation?.step ? (
            <p className="mt-1 text-xs text-muted-foreground">Current step: {humanize(operation.step)}.</p>
          ) : null}
          {ineligible && pendingState === "awake" ? (
            <p className="mt-1 text-xs text-muted-foreground">Sleep unavailable: {sleep.ineligibleReason ?? "this environment is not eligible."}</p>
          ) : null}
        </div>

        {sleep.lastError ? <p role="alert" className="break-words text-destructive">{sleep.lastError.message}</p> : null}
        {error && !dialog ? <p role="alert" className="break-words text-destructive">{error}</p> : null}
        {notice ? <p role="status" aria-live="polite" className="break-words text-muted-foreground">{notice}</p> : null}

        <div className="flex min-w-0 flex-wrap gap-2">
          {asleep ? (
            <Button type="button" disabled={busy} onClick={() => { setError(null); setDialog("wake"); }}>
              Wake
            </Button>
          ) : null}
          {pendingState === "awake" ? (
            <>
              {enabled ? (
                <Button type="button" variant="outline" disabled={busy} onClick={() => void runAction("keep-awake")}>
                  Keep awake
                </Button>
              ) : null}
              <Button type="button" variant="outline" disabled={busy || ineligible || inProgress} onClick={() => { setError(null); setDialog("sleep"); }}>
                Sleep now
              </Button>
            </>
          ) : null}
          {failed ? (
            <Button type="button" disabled={busy} onClick={() => void runAction("wake")}>
              {busy ? "Retrying…" : "Retry"}
            </Button>
          ) : null}
        </div>
      </CardContent>

      <Dialog open={dialog !== null} onOpenChange={(open) => { if (!busy && !open) setDialog(null); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{dialog === "sleep" ? "Sleep this environment?" : "Wake this environment?"}</DialogTitle>
            <DialogDescription>
              {dialog === "sleep"
                ? `Sleep ${applicationName} / ${environment.name}? Selfrun stops its services, creates a snapshot, then deletes the server. Waking takes several minutes.`
                : `Wake ${applicationName} / ${environment.name}? Selfrun restores the server and starts its services. This can take several minutes.`}
            </DialogDescription>
          </DialogHeader>
          {error ? <p role="alert" className="break-words text-sm text-destructive">{error}</p> : null}
          <DialogFooter>
            <Button type="button" variant="outline" disabled={busy} onClick={() => setDialog(null)}>Cancel</Button>
            <Button type="button" disabled={busy} onClick={() => void runAction(dialog === "sleep" ? "sleep" : "wake")}>
              {busy ? "Working…" : dialog === "sleep" ? "Sleep now" : "Wake environment"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  );
}

function statusDescription(sleep: EnvironmentSleep, now: number, operation: EnvironmentSleep["operation"]) {
  if (operation && ["queued", "running"].includes(operation.status)) {
    const title = operation.kind === "sleep" ? "Sleeping" : "Waking";
    return operation.status === "queued" ? `${title} request queued…` : `${title} in progress…`;
  }
  if (sleep.state === "asleep") {
    const size = sleep.snapshot?.sizeGb == null ? "" : ` · snapshot ${sleep.snapshot.sizeGb} GB`;
    return `Asleep since ${new Date(sleep.stateChangedAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}${size}`;
  }
  if (sleep.state === "sleeping" || sleep.state === "waking") {
    const title = sleep.state === "sleeping" ? "Sleeping" : "Waking";
    return operation?.status === "queued" ? `${title} request queued…` : `${title} in progress…`;
  }
  if (sleep.state === "sleep_failed" || sleep.state === "wake_failed") return "Environment power operation failed.";
  if (sleep.policy.sleepAfterMinutes === null) return "Awake · sleep is disabled.";
  if (!sleep.eligible || !sleep.sleepsAt) return "Sleep is enabled · waiting for eligibility.";
  const remaining = Date.parse(sleep.sleepsAt) - now;
  return `Sleeps in about ${formatRemaining(remaining)} · keep awake extends the timer by 4 hours.`;
}

function formatRemaining(milliseconds: number) {
  const totalMinutes = Math.max(0, Math.ceil(milliseconds / 60_000));
  if (totalMinutes === 0) return "now";
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  if (hours === 0) return `${minutes} min`;
  return minutes === 0 ? `${hours} h` : `${hours} h ${minutes} min`;
}

function humanize(value: string) {
  return value.replaceAll("_", " ");
}
