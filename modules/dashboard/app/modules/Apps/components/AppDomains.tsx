import { useCallback, useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { StatusBadge } from "@/core/components/StatusBadge";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Label } from "@/components/ui/label";
import { AlertDialog, AlertDialogContent, AlertDialogHeader, AlertDialogTitle, AlertDialogDescription, AlertDialogFooter, AlertDialogCancel } from "@/components/ui/alert-dialog";
import { MoreHorizontal } from "lucide-react";
import { listCustomDomains, removeCustomDomain } from "@/api/customDomainsApi";
import type { AppServiceInventoryResponse, CustomDomainRegistrationResponse, DomainBindingResponse } from "@/models/controlPlane";
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
  const [wizard, setWizard] = useState<{ registration?: CustomDomainRegistrationResponse; serviceName?: string } | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
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

  const publicServices = services.filter(service => service.public);
  const verified = registrations.filter(row => row.status === "verified");
  const canAddHostname = publicServices.length > 0 && verified.length > 0;

  return <div className="space-y-4">
    <Card>
      <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-3">
        <div className="space-y-1"><CardTitle>Owned domains</CardTitle><p className="text-sm text-muted-foreground">Add domains and verify ownership for this application. Its services can use the domain or a subdomain. Ownership is separate from routing and HTTPS.</p></div>
        <Button onClick={() => setWizard({})}>Add domain</Button>
      </CardHeader>
      <CardContent className="space-y-3">
        {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
        {loading ? <p role="status" className="text-sm text-muted-foreground">Loading domains…</p> : null}
        {!loading && registrations.length === 0 ? (
          <p className="text-sm text-muted-foreground">No custom domains added to this app.</p>
        ) : null}
        {registrations.length > 0 ? (
          <Table aria-label="Owned domains">
            <TableHeader>
              <TableRow>
                <TableHead>Domain</TableHead>
                <TableHead>Ownership</TableHead>
                <TableHead className="text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {registrations.map(row => (
                <TableRow key={row.id}>
                  <TableCell data-label="Domain" className="break-all font-mono text-sm">{row.hostname}</TableCell>
                  <TableCell data-label="Ownership"><StatusBadge status={row.status} /></TableCell>
                  <TableCell data-label="Actions" className="text-right">
                    <DropdownMenu>
                      <DropdownMenuTrigger asChild>
                        <Button aria-label={`Actions for ${row.hostname}`} size="icon" variant="ghost" className="h-8 w-8 text-muted-foreground hover:text-foreground">
                          <MoreHorizontal className="h-4 w-4" />
                        </Button>
                      </DropdownMenuTrigger>
                      <DropdownMenuContent align="end">
                        <DropdownMenuItem onClick={() => setWizard({ registration: row })}>
                          {row.status === "verified" ? "Ownership settings" : "Verify ownership"}
                        </DropdownMenuItem>
                        <DropdownMenuItem
                          className="text-destructive focus:text-destructive"
                          onClick={() => { setRemoveError(null); setRemoving(row); }}
                        >
                          Remove domain
                        </DropdownMenuItem>
                      </DropdownMenuContent>
                    </DropdownMenu>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : null}
      </CardContent>
    </Card>
    <Card>
      <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-3">
        <div className="space-y-1"><CardTitle>Service hostnames</CardTitle><p className="text-sm text-muted-foreground">Each hostname routes to one service. Routing (DNS) and HTTPS stay separate: a hostname is ready for traffic only when HTTPS confirms it.</p></div>
        <Button onClick={() => setPickerOpen(true)} disabled={!canAddHostname}>
          Add hostname
        </Button>
      </CardHeader>
      <CardContent className="space-y-4">
        {!canAddHostname ? (
          <p className="text-sm text-muted-foreground">
            {publicServices.length === 0
              ? "No public services. Private services stay internal and do not get hostnames."
              : "No verified domain yet. Add a domain above and verify ownership before assigning hostnames."}
          </p>
        ) : null}
        <HostnameTable
          services={publicServices}
          onManageService={onManageService}
        />
      </CardContent>
    </Card>
    <Dialog open={pickerOpen} onOpenChange={setPickerOpen}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Add hostname</DialogTitle>
          <DialogDescription>Choose the service and the verified domain it runs under.</DialogDescription>
        </DialogHeader>
        <AddHostnamePicker
          publicServices={publicServices}
          verified={verified}
          onCancel={() => setPickerOpen(false)}
          onPick={(serviceName, registration) => {
            setPickerOpen(false);
            setWizard({ registration, serviceName });
          }}
        />
      </DialogContent>
    </Dialog>
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

function AddHostnamePicker({
  publicServices,
  verified,
  onCancel,
  onPick,
}: {
  publicServices: AppServiceInventoryResponse[];
  verified: CustomDomainRegistrationResponse[];
  onCancel: () => void;
  onPick: (serviceName: string, registration: CustomDomainRegistrationResponse) => void;
}) {
  const [serviceName, setServiceName] = useState("");
  const [registrationId, setRegistrationId] = useState("");
  const ready = serviceName.length > 0 && verified.some(row => row.id === registrationId);
  return (
    <div className="space-y-4">
      <div className="grid gap-2">
        <Label htmlFor="hostname-service">Service</Label>
        <Select value={serviceName} onValueChange={setServiceName}>
          <SelectTrigger id="hostname-service"><SelectValue placeholder="Choose a public service" /></SelectTrigger>
          <SelectContent>
            {publicServices.map(service => <SelectItem key={service.name} value={service.name}>{service.name}</SelectItem>)}
          </SelectContent>
        </Select>
      </div>
      <div className="grid gap-2">
        <Label htmlFor="hostname-domain">Domain</Label>
        <Select value={registrationId} onValueChange={setRegistrationId}>
          <SelectTrigger id="hostname-domain"><SelectValue placeholder="Choose a verified domain" /></SelectTrigger>
          <SelectContent>
            {verified.map(row => <SelectItem key={row.id} value={row.id}>{row.hostname}</SelectItem>)}
          </SelectContent>
        </Select>
      </div>
      <DialogFooter>
        <Button variant="outline" onClick={onCancel}>Cancel</Button>
        <Button
          disabled={!ready}
          onClick={() => {
            const row = verified.find(item => item.id === registrationId);
            if (row) onPick(serviceName, row);
          }}
        >
          Continue
        </Button>
      </DialogFooter>
    </div>
  );
}

function HostnameTable({
  services,
  onManageService,
}: {
  services: AppServiceInventoryResponse[];
  onManageService: (name: string) => void;
}) {
  const [showPlatform, setShowPlatform] = useState(false);
  const custom = services.flatMap((service) =>
    service.domains
      .filter((binding) => binding.kind === "custom_domain")
      .map((binding) => ({ service, binding })),
  );
  const platform = services.flatMap((service) =>
    service.domains
      .filter((binding) => binding.kind === "minicloud_subdomain")
      .map((binding) => ({ service, binding })),
  );
  const platformNeedsAttention = platform.some(
    ({ binding }) => binding.status !== "active" || binding.sslStatus !== "active",
  );
  const platformVisible = showPlatform || platformNeedsAttention;

  const renderTable = (rows: { service: AppServiceInventoryResponse; binding: DomainBindingResponse }[], readOnly: boolean) => (
    <Table aria-label={readOnly ? "Platform hostnames" : "Custom hostnames"}>
      <TableHeader>
        <TableRow>
          <TableHead>Hostname</TableHead>
          <TableHead>Service</TableHead>
          <TableHead>Routing</TableHead>
          <TableHead>HTTPS</TableHead>
          <TableHead className="text-right">Actions</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map(({ service, binding }) => (
          <TableRow key={binding.id}>
            <TableCell data-label="Hostname">
              <span className="block break-all font-mono text-xs">{binding.hostname}</span>
              {binding.status === "disabled" ? (
                <span className="block text-xs text-muted-foreground">Disabled</span>
              ) : null}
              {readOnly ? (
                <span className="block text-xs text-muted-foreground">Platform (read-only)</span>
              ) : null}
            </TableCell>
            <TableCell data-label="Service" className="font-medium">{service.name}</TableCell>
            <TableCell data-label="Routing">
              <span className="inline-flex flex-wrap gap-1">
                <StatusBadge status={binding.dnsStatus} />
                <StatusBadge status={binding.applyStatus} />
              </span>
            </TableCell>
            <TableCell data-label="HTTPS"><StatusBadge status={binding.sslStatus} /></TableCell>
            <TableCell data-label="Actions" className="text-right">
              {readOnly ? (
                <span className="text-xs text-muted-foreground">Read-only</span>
              ) : (
                <Button size="sm" variant="outline" onClick={() => onManageService(service.name)}>Manage</Button>
              )}
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );

  return (
    <div className="space-y-3">
      {custom.length === 0 && !platformVisible ? (
        <p className="text-sm text-muted-foreground">No hostnames assigned yet.</p>
      ) : null}
      {custom.length ? renderTable(custom, false) : null}
      {platform.length ? (
        <Button
          size="sm"
          variant="ghost"
          aria-expanded={platformVisible}
          onClick={() => setShowPlatform((current) => !current)}
        >
          Platform hostnames ({platform.length})
        </Button>
      ) : null}
      {platform.length && platformVisible ? renderTable(platform, true) : null}
    </div>
  );
}
