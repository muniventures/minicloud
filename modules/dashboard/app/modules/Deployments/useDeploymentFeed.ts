import { useCallback, useEffect, useRef, useState } from "react";
import { listDeployments } from "@/api/deploymentsApi";
import type { DeploymentResponse } from "@/models/controlPlane";

const pollInterval = 5000;
type BeforeRead = () => Promise<unknown>;
type Feed = {
  scope: string;
  deployments: DeploymentResponse[];
  error: string | null;
  loading: boolean;
  observed: boolean;
};
type Session = {
  scope: string;
  read: (beforeRead?: BeforeRead) => Promise<void>;
};

// One lifecycle owns initial, manual and visible-page deployment discovery reads.
export function useDeploymentFeed(organizationId?: string, appId?: string) {
  const scope = JSON.stringify([organizationId, appId]);
  const session = useRef<Session | null>(null);
  const [feed, setFeed] = useState<Feed>({
    scope, deployments: [], error: null, loading: !!organizationId, observed: false,
  });

  useEffect(() => {
    let live = true;
    let pending: Promise<void> | null = null;
    let controller: AbortController | null = null;
    let timer: ReturnType<typeof setTimeout> | undefined;
    setFeed({ scope, deployments: [], error: null, loading: !!organizationId, observed: false });
    const clearTimer = () => clearTimeout(timer);
    const read = (beforeRead?: BeforeRead): Promise<void> => {
      if (!live || !organizationId) return Promise.resolve();
      if (pending) {
        return beforeRead ? pending.then(() => read(beforeRead)) : pending;
      }
      clearTimer();
      const request = new AbortController();
      controller = request;
      pending = (async () => {
        try {
          await beforeRead?.();
          if (!live || request.signal.aborted) return;
          const deployments = await listDeployments({ organizationId, appId }, request.signal);
          if (!live || request.signal.aborted) return;
          setFeed({ scope, deployments, error: null, loading: false, observed: true });
        } catch (error) {
          if (live && !request.signal.aborted) {
            setFeed((current) => ({
              ...current,
              loading: false,
              error: error instanceof Error ? error.message : "Unable to load deployments.",
            }));
          }
        } finally {
          pending = null;
          if (live && !document.hidden) timer = setTimeout(() => void read(), pollInterval);
        }
      })();
      return pending;
    };
    const owner = { scope, read };
    session.current = owner;
    const visibilityChanged = () => {
      clearTimer();
      if (document.hidden) {
        controller?.abort();
      } else if (pending) {
        void pending.then(() => live && !document.hidden ? read() : undefined);
      } else {
        void read();
      }
    };
    document.addEventListener("visibilitychange", visibilityChanged);
    if (!document.hidden) void read();
    return () => {
      live = false;
      clearTimer();
      controller?.abort();
      document.removeEventListener("visibilitychange", visibilityChanged);
      if (session.current === owner) session.current = null;
    };
  }, [scope, organizationId, appId]);

  const refresh = useCallback((beforeRead?: BeforeRead) => {
    return session.current?.scope === scope
      ? session.current.read(beforeRead)
      : Promise.resolve();
  }, [scope]);
  const current = feed.scope === scope
    ? feed
    : { scope, deployments: [], error: null, loading: !!organizationId, observed: false };
  return { ...current, refresh };
}
