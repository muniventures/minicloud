import { useCallback, useEffect, useRef, useState } from "react";
import {
  dispatchDeployment,
  cancelDeployment,
  getDeployment,
  listDeploymentActivities,
  listDeploymentEvents,
  listDeploymentLogs,
  refreshDeployment,
  type DeploymentRef,
} from "@/api/deploymentsApi";
import { isActiveDeployment } from "./deploymentDetailPresentation";
import { deploymentKey } from "./deploymentRoutes";

async function readDetail(ref: DeploymentRef, signal: AbortSignal) {
  const [deployment, events, activities, logs] = await Promise.all([
    getDeployment(ref, signal),
    listDeploymentEvents(ref, signal),
    listDeploymentActivities(ref, signal).catch((error) => {
      if (signal.aborted) throw error;
      return [];
    }),
    listDeploymentLogs(ref, signal),
  ]);
  return { deployment, events, activities, logs, observedAt: Date.now() };
}

type Snapshot = Awaited<ReturnType<typeof readDetail>>;
type Action = "read" | "refresh" | "dispatch" | "cancel";
type Session = {
  key: string;
  controller: AbortController;
  pending: Promise<Snapshot | undefined> | null;
};
type Scoped<T> = { key: string; value: T };

// Every request, pending action and observation is owned by one organization+number scope.
export function useDeploymentDetail(ref?: DeploymentRef) {
  const organizationId = ref?.organizationId;
  const number = ref?.number;
  const key = organizationId && number ? deploymentKey({ organizationId, number }) : null;
  const [snapshot, setSnapshot] = useState<Scoped<Snapshot> | null>(null);
  const [error, setError] = useState<Scoped<string> | null>(null);
  const [action, setAction] = useState<Scoped<Action> | null>(null);
  const session = useRef<Session | null>(null);

  const run = useCallback(
    async (nextAction: Action): Promise<Snapshot | undefined> => {
      const owner = session.current;
      if (!key || !organizationId || !number || !owner || owner.key !== key) return;
      const { signal } = owner.controller;
      if (signal.aborted) return;
      if (owner.pending) {
        if (nextAction === "read") return owner.pending;
        await owner.pending;
        if (signal.aborted) return;
        return run(nextAction);
      }
      const target = { organizationId, number };
      setAction({ key, value: nextAction });
      const request = (async () => {
        try {
          if (nextAction === "cancel") await cancelDeployment(target);
          if (nextAction === "dispatch") await dispatchDeployment(target);
          if (nextAction === "refresh") await refreshDeployment(target);
          if (signal.aborted) return;
          const next = await readDetail(target, signal);
          if (!signal.aborted) {
            setSnapshot({ key, value: next });
            setError(null);
            return next;
          }
        } catch (failure) {
          if (!signal.aborted)
            setError({
              key,
              value: failure instanceof Error ? failure.message : "Unable to load deployment.",
            });
        } finally {
          if (!signal.aborted) {
            owner.pending = null;
            setAction(null);
          }
        }
      })();
      owner.pending = request;
      return request;
    },
    [key, organizationId, number],
  );

  useEffect(() => {
    if (!key) {
      session.current = null;
      return;
    }
    const owner: Session = { key, controller: new AbortController(), pending: null };
    session.current = owner;
    setSnapshot(null);
    setError(null);
    setAction(null);
    void run("read");
    return () => {
      owner.controller.abort();
      if (session.current === owner) session.current = null;
    };
  }, [key, run]);

  const current = snapshot?.key === key ? snapshot.value : null;
  const currentAction = action?.key === key ? action.value : null;
  const active = isActiveDeployment(current?.deployment.status);
  useEffect(() => {
    if (!active) return;
    let canceled = false;
    let timer: ReturnType<typeof setTimeout>;
    const poll = async () => {
      const next = await run("read");
      if (!canceled && (!next || isActiveDeployment(next.deployment.status)))
        timer = setTimeout(poll, 3000);
    };
    timer = setTimeout(poll, 3000);
    return () => {
      canceled = true;
      clearTimeout(timer);
    };
  }, [active, run]);

  const refresh = useCallback(() => run("refresh"), [run]);
  const cancel = useCallback(() => run("cancel"), [run]);
  const dispatch = useCallback(() => run("dispatch"), [run]);
  return {
    snapshot: current,
    error: error?.key === key ? error.value : null,
    refreshing: currentAction !== null,
    dispatching: currentAction === "dispatch",
    canceling: currentAction === "cancel",
    cancel,
    refresh,
    dispatch,
  };
}
