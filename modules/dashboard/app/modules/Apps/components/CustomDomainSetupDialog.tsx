import { useCallback, useEffect, useState } from "react";
import { Check, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { StatusBadge } from "@/core/components/StatusBadge";
import { listCustomDomains, registerCustomDomain, verifyCustomDomainOwnership } from "@/api/customDomainsApi";
import { createAppCustomDomain, verifyAppDomain } from "@/api/environmentsApi";
import type { CustomDomainRegistrationResponse, DomainBindingResponse } from "@/models/controlPlane";
import { DomainDnsRecord } from "./DomainDnsRecord";

type Props = {
  organizationId: string;
  appId: string;
  serviceName?: string;
  registration?: CustomDomainRegistrationResponse;
  initialSubdomain?: string;
  existingBinding?: DomainBindingResponse;
  liveBindings: DomainBindingResponse[];
  onClose: () => void;
  onSaved: () => Promise<void>;
};

const steps = ["Domain", "Verify DNS", "Use domain", "Connect"];

export function CustomDomainSetupDialog({ appId, serviceName, registration: initialRegistration, initialSubdomain = "", existingBinding, liveBindings, onClose, onSaved }: Props) {
  const [registration, setRegistration] = useState(initialRegistration);
  const [savedBinding, setSavedBinding] = useState(existingBinding);
  const [step, setStep] = useState(existingBinding?.failureCode === "dns_ownership_pending" ? 1 : existingBinding ? 3 : initialRegistration ? !serviceName || initialRegistration.status !== "verified" ? 1 : 2 : 0);
  const [hostname, setHostname] = useState("");
  const [subdomain, setSubdomain] = useState(initialSubdomain);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const live = liveBindings.find(row => row.id === savedBinding?.id);
  const binding = live && Date.parse(live.updatedAt) > Date.parse(savedBinding?.updatedAt ?? "") ? live : savedBinding;
  const ownershipOnly = !serviceName;
  const visibleSteps = ownershipOnly ? ["Domain", "Verify DNS", "Ready"] : steps;
  const assignedHostname = registration ? `${subdomain.trim() ? `${subdomain.trim()}.` : ""}${registration.hostname}` : "";
  const dnsReady = binding?.dnsStatus === "verified";
  const active = binding?.status === "active";
  const connectionFailed = binding?.status === "failed" || binding?.status === "service_missing";

  const refresh = useCallback(async () => {
    try { await onSaved(); }
    catch { setError("Your changes were saved. The domain list could not refresh; close and reopen it to try again."); }
  }, [onSaved]);

  const addDomain = useCallback(async () => {
    setBusy(true);
    setError(null);
    try {
      const row = await registerCustomDomain(appId, hostname.trim());
      setRegistration(row);
      setStep(row.status === "verified" ? 2 : 1);
      await refresh();
    } catch (error) {
      setError(error instanceof Error ? error.message : "Unable to add domain.");
    } finally { setBusy(false); }
  }, [appId, hostname, refresh]);

  const ownershipFailure = binding?.failureCode === "dns_ownership_pending";
  useEffect(() => {
    if (!ownershipFailure) return;
    let live = true;
    listCustomDomains(appId).then(rows => {
      const row = rows.find(row => row.verificationToken === binding?.verificationToken);
      if (live && row) {
        setRegistration({ ...row, status: "pending", failureMessage: binding?.failureMessage ?? row.failureMessage });
        setStep(1);
      }
    }).catch(error => { if (live) setError(error instanceof Error ? error.message : "Unable to load ownership settings."); });
    return () => { live = false; };
  }, [appId, ownershipFailure, binding?.verificationToken, binding?.failureMessage]);

  const recoverOwnership = useCallback(async () => {
    const rows = await listCustomDomains(appId);
    const row = rows.find(row => row.id === registration?.id);
    if (row && row.status !== "verified") {
      setRegistration(row);
      setStep(1);
      return true;
    }
    return false;
  }, [appId, registration?.id]);

  const verifyOwnership = useCallback(async () => {
    if (!registration) return;
    setBusy(true);
    setError(null);
    try {
      const row = await verifyCustomDomainOwnership(appId, registration.id);
      setRegistration(row);
      if (row.status === "verified") setStep(savedBinding ? 3 : 2);
      await refresh();
    } catch (error) {
      setError(error instanceof Error ? error.message : "Unable to verify DNS.");
    } finally { setBusy(false); }
  }, [appId, registration, savedBinding, refresh]);

  const connectService = useCallback(async () => {
    if (!serviceName || !registration) return;
    setBusy(true);
    setError(null);
    try {
      const ownership = await verifyCustomDomainOwnership(appId, registration.id);
      setRegistration(ownership);
      if (ownership.status !== "verified") {
        setStep(1);
        await refresh();
        return;
      }
      const row = await createAppCustomDomain(appId, serviceName, assignedHostname);
      setSavedBinding(row);
      setStep(3);
      await refresh();
    } catch (error) {
      // The assignment endpoint rechecks DNS too; recover if ownership changed in flight.
      try { if (await recoverOwnership()) { await refresh(); return; } } catch { /* Preserve the assignment error if refresh fails. */ }
      setError(error instanceof Error ? error.message : "Unable to connect domain.");
    } finally { setBusy(false); }
  }, [appId, serviceName, registration, assignedHostname, recoverOwnership, refresh]);

  const checkConnection = useCallback(async () => {
    if (!binding) return;
    setBusy(true);
    setError(null);
    try {
      setSavedBinding(await verifyAppDomain(appId, binding.id));
      await refresh();
    } catch (error) {
      setError(error instanceof Error ? error.message : "Unable to check connection.");
    } finally { setBusy(false); }
  }, [appId, binding, refresh]);

  return (
    <Dialog open onOpenChange={open => { if (!open && !busy) onClose(); }}>
      <DialogContent className="flex max-w-xl flex-col gap-0 overflow-hidden p-0">
        <DialogHeader className="px-6 pb-4 pt-6 text-left">
          <DialogTitle>{ownershipOnly ? "App domain ownership" : "Assign a service hostname"}</DialogTitle>
          <DialogDescription>{ownershipOnly ? "Add and verify a domain for this application." : "Choose a hostname under a verified app domain."}</DialogDescription>
        </DialogHeader>
        <ol aria-label="Domain setup progress" className={`grid ${ownershipOnly ? "grid-cols-3" : "grid-cols-4"} border-y bg-muted/30 px-4 py-4`}>
          {visibleSteps.map((label, index) => (
            <li key={label} aria-current={step === index ? "step" : undefined} className={`flex flex-col items-center gap-1.5 text-xs ${step === index ? "font-medium text-foreground" : "text-muted-foreground"}`}>
              <span className={`flex h-6 w-6 items-center justify-center rounded-full border ${step === index ? "border-primary bg-primary text-primary-foreground" : "border-border"}`}>
                {index < step ? <Check aria-hidden="true" className="h-3.5 w-3.5" /> : index + 1}
              </span>{label}
            </li>
          ))}
        </ol>
        <div className="min-h-0 space-y-4 overflow-y-auto px-6 py-5">
          {step === 0 ? (
            <form id="register-domain" className="space-y-4" onSubmit={event => { event.preventDefault(); if (hostname.trim() && !busy) void addDomain(); }}>
              <div><h3 className="font-semibold">Add your domain</h3><p className="mt-1 text-sm text-muted-foreground">Start with a domain you own. You can connect it or its subdomains to services after verification.</p></div>
              <div className="space-y-2"><Label htmlFor="domain-hostname">Your domain</Label><Input id="domain-hostname" autoFocus autoCapitalize="none" autoCorrect="off" placeholder="example.com" value={hostname} onChange={event => setHostname(event.target.value)} disabled={busy} /><p className="text-xs text-muted-foreground">Enter only the domain, without https:// or a path. A subdomain such as api.example.com is also supported.</p></div>
            </form>
          ) : null}
          {step === 1 && !registration ? <p role="status" className="text-sm text-muted-foreground">Loading ownership verification…</p> : null}
          {step === 1 && registration ? (
            <>
              <div><h3 className="font-semibold">Verify you own {registration.hostname}</h3><p className="mt-1 text-sm text-muted-foreground">Open your DNS provider, find this domain’s DNS settings, and add the TXT record below. Save it, then select Verify DNS.</p></div>
              <DomainDnsRecord type="TXT" name={`_minicloud-verification.${registration.hostname}`} value={registration.verificationToken} />
              <p className="text-sm text-muted-foreground">Keep the TXT record. DNS propagation can take up to 48 hours.</p>
              {registration.failureMessage ? <p role="status" className="rounded-md border bg-muted/30 p-3 text-sm">{registration.failureMessage}</p> : null}
            </>
          ) : null}
          {step === 2 && registration ? (
            <>
              <div role="status" className="flex items-start gap-3 rounded-lg border bg-success-surface p-4 text-success"><Check className="mt-0.5 h-5 w-5 shrink-0" /><div><h3 className="font-semibold">Domain verified</h3><p className="mt-1 text-sm">{registration.hostname} is available to use with your services.</p></div></div>
              {!ownershipOnly ? <>
              <div><h3 className="font-semibold">Connect to {serviceName}</h3><p className="mt-1 text-sm text-muted-foreground">Use the domain itself, or choose a subdomain for this service. You can also save it for later.</p></div>
              <div className="space-y-2"><Label htmlFor="service-subdomain">Subdomain (optional)</Label><Input id="service-subdomain" placeholder="api" value={subdomain} onChange={event => setSubdomain(event.target.value)} disabled={busy} /><p className="text-xs text-muted-foreground">Leave blank to use {registration.hostname}.</p></div>
              <div className="rounded-md border bg-muted/30 p-3 text-sm"><p className="break-all font-mono">{assignedHostname}</p><p className="mt-1 text-muted-foreground">Routes to {serviceName}</p></div>
              </> : <p className="text-sm text-muted-foreground">Assign this domain or its subdomains to public services from the app’s Domains tab.</p>}
            </>
          ) : null}
          {step === 3 && binding ? (
            <>
              <div><h3 className="font-semibold">{active ? "Your domain is connected" : connectionFailed ? "Connection needs attention" : dnsReady ? "DNS verified · setting up HTTPS" : "Point your domain to the service"}</h3><p className="mt-1 break-all text-sm text-muted-foreground">{binding.hostname} → {serviceName}</p></div>
              {dnsReady ? <div role="status" className="space-y-2 rounded-lg border p-4"><StatusBadge status={binding.status} /><p className="text-sm text-muted-foreground">{active ? "Your service is available over HTTPS." : connectionFailed ? binding.failureMessage ?? "Routing could not complete. Check the service status, then try again." : "DNS is correct. Routing and the HTTPS certificate are being configured automatically. You can close this dialog."}</p>{active ? <a href={`https://${binding.hostname}`} target="_blank" rel="noreferrer" className="inline-block text-sm underline">Open {binding.hostname}</a> : null}</div> : (
                <>
                  <p className="text-sm text-muted-foreground">Add this record at your DNS provider. Replace conflicting A, AAAA or CNAME records for this hostname only. On Cloudflare, select <strong>DNS only</strong> (gray cloud).</p>
                  {binding.targetIp ? <DomainDnsRecord key={binding.hostname} type={binding.targetIp.includes(":") ? "AAAA" : "A"} name={binding.hostname} value={binding.targetIp} /> : <p role="status" className="rounded-md border p-3 text-sm">Deploy this app first to get its server address, then select Check connection.</p>}
                  {binding.failureMessage ? <p role="status" className="text-sm text-muted-foreground">{binding.failureMessage}</p> : null}
                </>
              )}
              {binding.lastCheckedAt ? <p className="text-xs text-muted-foreground">Last checked {new Date(binding.lastCheckedAt).toLocaleString()}</p> : null}
            </>
          ) : null}
          {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
        </div>
        <DialogFooter className="gap-2 border-t bg-background px-6 py-4 sm:justify-between sm:space-x-0">
          <Button variant="outline" onClick={onClose} disabled={busy}>{step === 0 ? "Cancel" : step === 3 || (ownershipOnly && step === 2) ? "Done" : "Save for later"}</Button>
          {step === 0 ? <Button type="submit" form="register-domain" disabled={!hostname.trim() || busy}>{busy ? "Adding…" : "Continue to DNS verification"}</Button> : null}
          {step === 1 ? <Button onClick={() => void verifyOwnership()} disabled={busy || !registration}>{busy ? <><Loader2 className="h-4 w-4 animate-spin" /> Checking DNS…</> : "Verify DNS"}</Button> : null}
          {step === 2 && !ownershipOnly ? <Button onClick={() => void connectService()} disabled={busy}>{busy ? "Connecting…" : `Connect to ${serviceName}`}</Button> : null}
          {step === 3 && !active ? <Button onClick={() => void checkConnection()} disabled={busy}>{busy ? "Checking…" : "Check connection"}</Button> : null}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
