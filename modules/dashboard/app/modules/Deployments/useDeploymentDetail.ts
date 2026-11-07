import { useCallback, useEffect, useRef, useState } from "react";
import {
  dispatchDeployment,
  cancelDeployment,
  getDeployment,
  listDeploymentActivities,
  listDeploymentEvents,
  listDeploymentLogs,
  refreshDeployment,
} from "@/api/deploymentsApi";
import { isActiveDeployment } from "./deploymentDetailPresentation";

async function readDetail(id: string, signal: AbortSignal) {
  const [deployment, events, activities, logs] = await Promise.all([
    getDeployment(id, signal),
    listDeploymentEvents(id, signal),
    listDeploymentActivities(id, signal).catch((error) => {
      if (signal.aborted) throw error;
      return [];
    }),
    listDeploymentLogs(id, signal),
  ]);
  return { deployment, events, activities, logs, observedAt: Date.now() };
}

type Snapshot = Awaited<ReturnType<typeof readDetail>>;
type Action = "read" | "refresh" | "dispatch" | "cancel";

export function useDeploymentDetail(id?: string) {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [action, setAction] = useState<Action | null>(null);
  const controller = useRef<AbortController | null>(null);
  const pending = useRef<Promise<Snapshot | undefined> | null>(null);

  const run = useCallback(
    async (nextAction: Action): Promise<Snapshot | undefined> => {
      const signal = controller.current?.signal;
      if (!id || !signal || signal.aborted) return;
      if (pending.current) {
        if (nextAction === "read") return pending.current;
        await pending.current;
        if (signal.aborted) return;
        return run(nextAction);
      }
      setAction(nextAction);
      const request = (async () => {
        try {
          if (nextAction === "cancel") await cancelDeployment(id);
          if (nextAction === "dispatch") await dispatchDeployment(id);
          if (nextAction === "refresh") await refreshDeployment(id);
          if (signal.aborted) return;
          const next = await readDetail(id, signal);
          if (!signal.aborted) {
            setSnapshot(next);
            setError(null);
            return next;
          }
        } catch (failure) {
          if (!signal.aborted)
            setError(
              failure instanceof Error
                ? failure.message
                : "Unable to load deployment.",
            );
        } finally {
          if (!signal.aborted) {
            pending.current = null;
            setAction(null);
          }
        }
      })();
      pending.current = request;
      return request;
    },
    [id],
  );

  useEffect(() => {
    const current = new AbortController();
    controller.current = current;
    pending.current = null;
    setSnapshot(null);
    setError(null);
    void run("read");
    return () => current.abort();
  }, [run]);

  const active = isActiveDeployment(snapshot?.deployment.status);
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
    snapshot,
    error,
    refreshing: action !== null,
    dispatching: action === "dispatch",
    canceling: action === "cancel",
    cancel,
    refresh,
    dispatch,
  };
}
