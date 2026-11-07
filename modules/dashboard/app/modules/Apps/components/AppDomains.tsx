import { useCallback, useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { StatusBadge } from "@/core/components/StatusBadge";
import { AlertDialog, AlertDialogContent, AlertDialogHeader, AlertDialogTitle, AlertDialogDescription, AlertDialogFooter, AlertDialogCancel } from "@/components/ui/alert-dialog";
import { listCustomDomains, removeCustomDomain } from "@/api/customDomainsApi";
import type { AppServiceInventoryResponse, CustomDomainRegistrationResponse } from "@/models/controlPlane";
import { CustomDomainSetupDialog } from "./CustomDomainSetupDialog";

type Props = {
  organizationId: string;
  appId: string;
  services: AppServiceInventoryResponse[];
  onSaved: () => Promise<void>;
  onManageService: (name: string) => void;
};

export function AppDomains({ organizationId, appId, services, onSaved, onManageService }: Props) {
  const [registrations, setRegistrations] = useState<CustomDomainRegistrationResponse[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [removing, setRemoving] = useState<CustomDomainRegistrationResponse | null>(null);
  const [removingBusy, setRemovingBusy] = useState(false);
  const [removeError, setRemoveError] = useState<string | null>(null);
  const [serviceName, setServiceName] = useState("");
  const [wizard, setWizard] = useState<{ registration?: CustomDomainRegistrationResponse; serviceName?: string } | null>(null);
  const bindings = services.flatMap(service => service.domains);
  const refresh = useCallback(async () => {
    const rows = await listCustomDomains(appId);
    setRegistrations(rows);
    await onSaved();
  }, [appId, onSaved]);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    listCustomDomains(appId, controller.signal).then(rows => {
      if (!controller.signal.aborted) { setRegistrations(rows); setError(null); }
    }).catch(error => {
      if (!controller.signal.aborted) setError(error instanceof Error ? error.message : "Unable to load app domains.");
    }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [appId]);

  const remove = useCallback(async () => {
    if (!removing) return;
    setRemovingBusy(true);
    setRemoveError(null);
    try {
      await removeCustomDomain(appId, removing.id);
      setRegistrations(rows => rows.filter(row => row.id !== removing.id));
      setRemoving(null);
      try { await refresh(); }
      catch { setError("Domain removed. The list could not refresh; reload to see the latest state."); }
    } catch (error) {
      setRemoveError(error instanceof Error ? error.message : "Unable to remove domain.");
    } finally { setRemovingBusy(false); }
  }, [appId, removing, refresh]);
  const assignedServices = removing ? services.filter(service => service.domains.some(binding => binding.verificationToken === removing.verificationToken)) : [];

  return <div className="space-y-4">
    <Card>
      <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-3">
        <div className="space-y-1"><CardTitle>App domains</CardTitle><p className="text-sm text-muted-foreground">Add domains and verify ownership for this application. Its services can use the domain or a subdomain.</p></div>
        <Button onClick={() => setWizard({})}>Add domain</Button>
      </CardHeader>
      <CardContent className="space-y-3">
        {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
        {loading ? <p role="status" className="text-sm text-muted-foreground">Loading domains…</p> : registrations.length === 0 ? <p className="text-sm text-muted-foreground">No custom domains added to this app.</p> : null}
        {registrations.map(row => <div key={row.id} className="flex flex-wrap items-center justify-between gap-3 rounded-lg border p-3">
          <div className="flex min-w-0 flex-wrap items-center gap-2"><span className="break-all font-mono text-sm">{row.hostname}</span><StatusBadge status={row.status} /></div>
          <div className="flex gap-2"><Button size="sm" variant="outline" onClick={() => setWizard({ registration: row })}>{row.status === "verified" ? "Ownership settings" : "Verify ownership"}</Button><Button size="sm" variant="outline" className="text-destructive" aria-label={`Remove ${row.hostname}`} onClick={() => { setRemoveError(null); setRemoving(row); }}>Remove</Button></div>
        </div>)}
      </CardContent>
    </Card>
    <Card>
      <CardHeader><CardTitle>Service hostnames</CardTitle><p className="text-sm text-muted-foreground">Choose a verified app domain, then configure a hostname for a public service. Each hostname routes to one service.</p></CardHeader>
      <CardContent className="space-y-4">
        <div className="flex flex-wrap gap-2">
          <Select value={serviceName} onValueChange={setServiceName}><SelectTrigger aria-label="Service for domain assignment" className="w-full sm:w-56"><SelectValue placeholder="Choose a public service" /></SelectTrigger><SelectContent>{services.filter(service => service.public).map(service => <SelectItem key={service.name} value={service.name}>{service.name}</SelectItem>)}</SelectContent></Select>
          {registrations.filter(row => row.status === "verified").map(row => <Button key={row.id} variant="outline" disabled={!serviceName} onClick={() => setWizard({ registration: row, serviceName })}>Use {row.hostname}</Button>)}
        </div>
        {services.filter(service => service.public).map(service => <div key={service.name} className="flex flex-wrap items-center justify-between gap-3 rounded-lg border p-3">
          <div className="min-w-0 space-y-1"><p className="text-sm font-medium">{service.name}</p>{service.domains.map(binding => <div key={binding.id} className="flex flex-wrap items-center gap-2 text-xs"><span className="break-all font-mono">{binding.hostname}</span><StatusBadge status={binding.status} /></div>)}</div>
          <Button size="sm" variant="outline" onClick={() => onManageService(service.name)}>Manage hostnames</Button>
        </div>)}
        {!services.some(service => service.public) ? <p className="text-sm text-muted-foreground">Deploy a public service to assign hostnames. Domain ownership can be verified before deployment.</p> : null}
      </CardContent>
    </Card>
    <AlertDialog open={Boolean(removing)} onOpenChange={open => { if (!open && !removingBusy) setRemoving(null); }}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Remove {removing?.hostname}?</AlertDialogTitle>
          <AlertDialogDescription>{assignedServices.length ? "Remove this domain’s service hostnames first. The domain is still assigned to:" : "This removes the domain from this application. Your DNS records remain at your provider. Adding it again will require ownership verification."}</AlertDialogDescription>
        </AlertDialogHeader>
        {assignedServices.map(service => <Button key={service.name} variant="outline" onClick={() => { setRemoving(null); onManageService(service.name); }}>Manage {service.name} hostnames</Button>)}
        {removeError ? <p role="alert" className="text-sm text-destructive">{removeError}</p> : null}
        <AlertDialogFooter>
          <AlertDialogCancel disabled={removingBusy}>Cancel</AlertDialogCancel>
          <Button variant="destructive" disabled={removingBusy || assignedServices.length > 0} onClick={() => void remove()}>{removingBusy ? "Removing…" : "Remove domain"}</Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
    {wizard ? <CustomDomainSetupDialog key={`${wizard.registration?.id ?? "new"}:${wizard.serviceName ?? "ownership"}`} organizationId={organizationId} appId={appId} serviceName={wizard.serviceName} registration={wizard.registration} liveBindings={bindings} onClose={() => setWizard(null)} onSaved={refresh} /> : null}
  </div>;
}
