import { useEffect, useState } from "react";
import { listPlans, type ProviderPlan } from "../../api/providersApi";

export function usePlanCatalog(organizationId?: string) {
  const [plans, setPlans] = useState<ProviderPlan[]>([]);
  const [loading, setLoading] = useState(!!organizationId);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    setPlans([]); setError(null); setLoading(!!organizationId);
    if (organizationId) {
      listPlans(organizationId, controller.signal)
        .then(value => { if (!controller.signal.aborted) setPlans(value.filter(plan => plan.enabled && plan.accountEnabled)); })
        .catch(error => { if (!controller.signal.aborted) setError(error instanceof Error ? error.message : "Unable to load plans."); })
        .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    }
    return () => controller.abort();
  }, [organizationId]);
  return { plans, loading, error };
}

export function planDescription(plan: ProviderPlan) { return `${plan.providerName} · ${plan.providerType} · ${plan.region} · ${plan.sku}`; }
