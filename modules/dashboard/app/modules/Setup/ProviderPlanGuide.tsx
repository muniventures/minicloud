import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { listPlans, listProviders } from "../../api/providersApi";
import { useAuth } from "../../core/auth/AuthProvider";

export function ProviderPlanGuide() {
  const { selectedOrganization } = useAuth();
  const organizationId = selectedOrganization?.id;
  const [counts, setCounts] = useState<{ accounts: number; plans: number } | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const [retry, setRetry] = useState(0);

  useEffect(() => {
    setCounts(null);
    setUnavailable(false);
    if (!organizationId) return;
    const controller = new AbortController();
    void Promise.all([listProviders(organizationId, controller.signal), listPlans(organizationId, controller.signal)])
      .then(([accounts, plans]) => {
        if (!controller.signal.aborted) setCounts({ accounts: accounts.length, plans: plans.length });
      }).catch(() => { if (!controller.signal.aborted) setUnavailable(true); });
    return () => controller.abort();
  }, [organizationId, retry]);

  return (
    <section className="max-w-2xl space-y-4">
      <h1 className="text-2xl font-semibold">Provider and plan guide</h1>
      <p>Your workspace is ready. This guide reads existing provider accounts and plans; it never creates infrastructure or submits credentials.</p>
      {!organizationId ? <p>Select a workspace, or <Link className="underline" to="/onboarding">create your workspace</Link> if your authenticated account has none.</p> : <>
        {!counts && !unavailable ? <p role="status">Checking provider accounts and plans…</p> : null}
        {unavailable ? <div role="alert">
          <p>Provider information is temporarily unavailable. Existing configuration has not been changed.</p>
          <Button onClick={() => setRetry((value) => value + 1)}>Retry provider check</Button>
        </div> : null}
        {counts ? <p role="status">{counts.accounts} provider account(s), {counts.plans} plan(s) in this workspace.</p> : null}
        <ol className="list-inside list-decimal space-y-3">
          <li><Link className="underline" to="/plans/accounts">Connect a provider account</Link> using the existing provider screen. Choose Hetzner, DigitalOcean or Vultr and validate its credential there. Do not enter provider credentials into setup.</li>
          <li><Link className="underline" to="/plans">Create a named plan</Link> mapped to that account, region and server size. Account and plan changes require the workspace permissions shown by those screens.</li>
          <li><Link className="underline" to="/apps">Open Apps</Link>, then create an app, choose an environment, and configure its services. Choose placement deliberately before deploying; deployment can allocate billable VPS resources.</li>
        </ol>
      </>}
      <p>Already configured? Continue using Apps → Environment → Services. No additional organization or provider model is needed.</p>
      <Link className="underline" to="/apps">Continue to apps</Link>
    </section>
  );
}
