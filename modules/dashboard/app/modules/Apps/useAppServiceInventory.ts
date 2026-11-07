import { useCallback, useEffect, useRef, useState } from "react";
import { listAppServices } from "@/api/appsApi";
import type { AppServiceInventoryResponse } from "@/models/controlPlane";

export function useAppServiceInventory(
  appId?: string,
  organizationId?: string,
) {
  const [services, setServices] = useState<AppServiceInventoryResponse[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [now, setNow] = useState(Date.now());
  const pending = useRef<Promise<void> | null>(null);
  const controller = useRef<AbortController | null>(null);
  const refresh = useCallback(async () => {
    if (!appId || !organizationId) return;
    if (pending.current) return pending.current;
    const request = new AbortController();
    controller.current = request;
    setLoading(true);
    const work = listAppServices(appId, request.signal)
      .then((next) => {
        if (!request.signal.aborted) {
          setServices(next);
          setError(null);
        }
      })
      .catch((error) => {
        if (!request.signal.aborted)
          setError(
            error instanceof Error
              ? error.message
              : "Service status could not be refreshed.",
          );
      })
      .finally(() => {
        if (controller.current === request) {
          pending.current = null;
          setLoading(false);
          setNow(Date.now());
        }
      });
    pending.current = work;
    return work;
  }, [appId, organizationId]);
  useEffect(() => {
    controller.current?.abort();
    pending.current = null;
    setServices([]);
    setError(null);
    let canceled = false;
    let timer: ReturnType<typeof setTimeout>;
    const poll = async () => {
      await refresh();
      if (!canceled) timer = setTimeout(poll, 15_000);
    };
    void poll();
    const clock = setInterval(() => setNow(Date.now()), 15_000);
    return () => {
      canceled = true;
      controller.current?.abort();
      pending.current = null;
      clearTimeout(timer);
      clearInterval(clock);
    };
  }, [refresh]);
  return { services, error, loading, now, refresh };
}
