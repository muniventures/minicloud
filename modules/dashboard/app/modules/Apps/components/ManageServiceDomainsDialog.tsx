import { runtimeConfig } from "@/core/config/runtimeConfig";
import { CollectionToolbar } from "@/core/components/CollectionToolbar";
import { useRecordCollection } from "@/core/components/useRecordCollection";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { StatusBadge } from "@/core/components/StatusBadge";
import { CopyButton } from "@/core/components/CopyButton";
import { Star, Trash2 } from "lucide-react";
import { useCallback, useEffect, useMemo, useState, type ChangeEvent } from "react";
import { checkAppDomainAvailability, createAppCustomDomain, createAppDomain, deleteAppDomain, setAppDomainPrimary } from "../../../api/environmentsApi";
import { listCustomDomains, verifyCustomDomainOwnership } from "@/api/customDomainsApi";
import { CustomDomainSetupDialog } from "./CustomDomainSetupDialog";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import type { CustomDomainRegistrationResponse, DomainAvailabilityResponse, DomainBindingResponse } from "../../../models/controlPlane";

type Props = {
  organizationId: string;
  appId: string;
  serviceName: string | null;
  domains: DomainBindingResponse[];
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onSaved: () => Promise<void>;
  onManageAppDomains?: () => void;
};

export function ManageServiceDomainsDialog({ organizationId, appId, serviceName, domains, open, onOpenChange, onSaved, onManageAppDomains }: Props) {
  const [registrations, setRegistrations] = useState<CustomDomainRegistrationResponse[]>([]);
  const [selectedRegistrationId, setSelectedRegistrationId] = useState("");
  const [wizard, setWizard] = useState<{ registration?: CustomDomainRegistrationResponse; binding?: DomainBindingResponse; subdomain?: string } | null>(null);
  const [customSubdomain, setCustomSubdomain] = useState("");
  const [label, setLabel] = useState("");
  const [availability, setAvailability] = useState<DomainAvailabilityResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [checking, setChecking] = useState(false);
  const [saving, setSaving] = useState(false);
  const [busyDomainId, setBusyDomainId] = useState<string | null>(null);

  const trimmedLabel = useMemo(() => label.trim(), [label]);
  const canCheck = Boolean(serviceName && trimmedLabel && !checking && !saving);
  const canSave = Boolean(serviceName && availability?.available && availability.label === trimmedLabel.toLowerCase() && !saving);

  useEffect(() => {
    if (!open) {
      setCustomSubdomain("");
      setLabel("");
      setWizard(null);
      setAvailability(null);
      setError(null);
      setChecking(false);
      setSaving(false);
      setBusyDomainId(null);
    }
  }, [open]);

  const handleLabelChange = useCallback((event: ChangeEvent<HTMLInputElement>) => {
    setLabel(event.target.value);
    setAvailability(null);
    setError(null);
  }, []);

  const checkAvailability = useCallback(async () => {
    if (!canCheck) {
      return;
    }

    setError(null);
    setChecking(true);
    try {
      setAvailability(await checkAppDomainAvailability(appId, trimmedLabel));
    } catch (checkError) {
      setAvailability(null);
      setError(checkError instanceof Error ? checkError.message : "Unable to check availability.");
    } finally {
      setChecking(false);
    }
  }, [appId, canCheck, trimmedLabel]);

  const saveSubdomain = useCallback(async () => {
    if (!canSave || !serviceName) {
      return;
    }

    setError(null);
    setSaving(true);
    try {
      await createAppDomain(appId, serviceName, trimmedLabel);
      setLabel("");
      setAvailability(null);
      await onSaved();
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : "Unable to save subdomain.");
    } finally {
      setSaving(false);
    }
  }, [appId, canSave, onSaved, serviceName, trimmedLabel]);

  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setRegistrations([]);
    setSelectedRegistrationId("");
    listCustomDomains(appId, controller.signal).then(rows => {
      if (!controller.signal.aborted) {
        setRegistrations(rows);
        setSelectedRegistrationId(rows.find(row => row.status === "verified")?.id ?? "");
      }
    }).catch(error => {
      if (!controller.signal.aborted) setError(error instanceof Error ? error.message : "Unable to load domains.");
    });
    return () => controller.abort();
  }, [open, appId]);

  const refreshDomains = useCallback(async () => {
    const [rows] = await Promise.all([listCustomDomains(appId), onSaved()]);
    setRegistrations(rows);
  }, [appId, onSaved]);

  const selectedRegistration = registrations.find(row => row.id === selectedRegistrationId);
  const assignedHostname = selectedRegistration ? `${customSubdomain.trim() ? customSubdomain.trim().toLowerCase() + "." : ""}${selectedRegistration.hostname}` : "";
  const assignCustomHostname = useCallback(async () => {
    if (!serviceName || !selectedRegistration) return;
    setSaving(true);
    setError(null);
    try {
      const ownership = await verifyCustomDomainOwnership(appId, selectedRegistration.id);
      if (ownership.status !== "verified") {
        setWizard({ registration: ownership, subdomain: customSubdomain });
        return;
      }
      const binding = await createAppCustomDomain(appId, serviceName, assignedHostname);
      setWizard({ binding });
      await refreshDomains();
    } catch (error) {
      try {
        const rows = await listCustomDomains(appId);
        const ownership = rows.find(row => row.id === selectedRegistration.id);
        if (ownership && ownership.status !== "verified") {
          setWizard({ registration: ownership, subdomain: customSubdomain });
          return;
        }
      } catch { /* Preserve the assignment error. */ }
      setError(error instanceof Error ? error.message : "Unable to assign service hostname.");
    } finally { setSaving(false); }
  }, [appId, serviceName, selectedRegistration, customSubdomain, assignedHostname, refreshDomains]);

  const handleSetPrimary = useCallback(async (domainId: string) => {
    setError(null);
    setBusyDomainId(domainId);
    try {
      await setAppDomainPrimary(appId, domainId);
      await onSaved();
    } catch (primaryError) {
      setError(primaryError instanceof Error ? primaryError.message : "Unable to set primary domain.");
    } finally {
      setBusyDomainId(null);
    }
  }, [appId, onSaved]);

  const handleDeleteDomain = useCallback(async (domainId: string) => {
    if (!window.confirm("Delete this domain?")) {
      return;
    }

    setError(null);
    setBusyDomainId(domainId);
    try {
      await deleteAppDomain(appId, domainId);
      await onSaved();
    } catch (deleteError) {
      setError(deleteError instanceof Error ? deleteError.message : "Unable to delete domain.");
    } finally {
      setBusyDomainId(null);
    }
  }, [appId, onSaved]);

  const collection = useRecordCollection(`${appId}:${serviceName}:domains`, domains, { hostname: item => item.hostname, status: item => item.status }, item => item.id);
  if (open && wizard && serviceName) {
    return <CustomDomainSetupDialog
      key={`${organizationId}:${appId}:${serviceName}:${wizard.registration?.id ?? wizard.binding?.id ?? "new"}`}
      organizationId={organizationId} appId={appId} serviceName={serviceName}
      registration={wizard.registration} initialSubdomain={wizard.subdomain} existingBinding={wizard.binding} liveBindings={domains}
      onClose={() => setWizard(null)} onSaved={refreshDomains}
    />;
  }
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-xl max-h-[90dvh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>Manage Domains: {serviceName}</DialogTitle>
          <DialogDescription>
            Connect your own domain or add Minicloud subdomains.
          </DialogDescription>
        </DialogHeader>
        <CollectionToolbar label="domains" {...collection.toolbar} onRefresh={() => refreshDomains().catch(error => setError(String(error)))} />

        {error ? <p className="text-sm text-destructive">{error}</p> : null}

        {/* Existing Domains List */}
        <div className="space-y-2">
          <Label className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
            Current Domains ({domains.length})
          </Label>
          <div className="space-y-2 max-h-60 overflow-y-auto pr-1">
            {collection.records.map((domain) => (
              <div
                key={domain.id}
                className="flex flex-wrap items-center justify-between gap-3 rounded-lg border p-2.5 text-xs bg-card"
              >
                <div className="flex flex-wrap items-center gap-2 min-w-0">
                  <a
                    href={domain.status === "disabled" ? undefined : `https://${domain.hostname}`}
                    target="_blank"
                    rel="noreferrer"
                    className="font-mono font-medium text-primary hover:underline truncate"
                  >
                    {domain.hostname}
                  </a>
                  {domain.isPrimary ? (
                    <span className="inline-flex items-center gap-1 rounded-full bg-success-surface  px-2 py-0.5 text-[10px] font-bold text-success ">
                      <Star className="h-3 w-3 fill-current" /> Primary
                    </span>
                  ) : null}
                  <StatusBadge status={domain.status} />
                </div>

                <div className="flex flex-wrap items-center gap-1">
                  <CopyButton value={domain.hostname} />
                  {!domain.isPrimary ? (
                    <>
                      <Button
                        size="sm"
                        variant="ghost"
                        className="h-7 text-xs font-medium"
                        disabled={busyDomainId === domain.id}
                        onClick={() => void handleSetPrimary(domain.id)}
                      >
                        Set as primary
                      </Button>
                      <Button
                        size="icon"
                        variant="ghost"
                        className="h-7 w-7 text-destructive hover:text-destructive hover:bg-error-surface"
                        disabled={busyDomainId === domain.id}
                        onClick={() => void handleDeleteDomain(domain.id)}
                        title="Delete domain"
                      >
                        <Trash2 className="h-3.5 w-3.5" />
                      </Button>
                    </>
                  ) : null}
                </div>
                {domain.kind === "custom_domain" ? (
                  <Button size="sm" variant="outline" onClick={() => setWizard({ binding: domain })}>
                    {domain.dnsStatus === "verified" ? "Connection settings" : "Continue setup"}
                  </Button>
                ) : null}
              </div>
            ))}
          </div>
        </div>

        <div className="space-y-3 border-t pt-4">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div><h3 className="text-sm font-medium">Custom domains</h3><p className="mt-1 text-xs text-muted-foreground">Use a verified app domain and choose a hostname for this service.</p></div>
            <Button size="sm" variant="outline" onClick={onManageAppDomains}>Manage app domains</Button>
          </div>
          {registrations.length > 0 ? (
            <div className="space-y-2">
              <Label htmlFor="registered-domain">App domains</Label>
              <div className="flex flex-col gap-2 sm:flex-row">
                <Select value={selectedRegistrationId} onValueChange={setSelectedRegistrationId}>
                  <SelectTrigger id="registered-domain" className="min-w-0"><SelectValue placeholder="Choose a domain" /></SelectTrigger>
                  <SelectContent>{registrations.filter(row => row.status === "verified").map(row => <SelectItem key={row.id} value={row.id}>{row.hostname} · {row.status === "verified" ? "Verified · available to use" : "Needs verification"}</SelectItem>)}</SelectContent>
                </Select>

              </div>
              <form className="space-y-3" onSubmit={event => { event.preventDefault(); if (!saving) void assignCustomHostname(); }}>
                <Label htmlFor="custom-service-subdomain">Service subdomain (optional)</Label>
                <div className="flex min-w-0 flex-col rounded-md border sm:flex-row">
                  <Input id="custom-service-subdomain" className="min-w-0 border-0 shadow-none" placeholder="api" autoCapitalize="none" autoCorrect="off" value={customSubdomain} onChange={event => setCustomSubdomain(event.target.value)} disabled={!selectedRegistration || saving} />
                  {selectedRegistration ? <span className="break-all border-t px-3 py-2 font-mono text-sm text-muted-foreground sm:border-l sm:border-t-0">.{selectedRegistration.hostname}</span> : null}
                </div>
                <p className="text-xs text-muted-foreground">Leave blank to use the app domain itself.</p>
                {assignedHostname ? <p aria-live="polite" className="break-all font-mono text-sm">{assignedHostname} → {serviceName}</p> : null}
                <Button type="submit" disabled={!selectedRegistration || !serviceName || saving}>{saving ? "Assigning…" : `Assign to ${serviceName}`}</Button>
              </form>
            </div>
          ) : <p className="text-sm text-muted-foreground">Add and verify an app domain to assign a service hostname.</p>}
        </div>

        {/* Add Subdomain Section */}
        <div className="space-y-3 pt-3 border-t">
          <Label htmlFor="subdomain-label" className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
            Subdomain
          </Label>
          <div className="flex rounded-md border bg-background focus-within:ring-1 focus-within:ring-ring">
            <Input
              id="subdomain-label"
              className="border-0 shadow-none focus-visible:ring-0 text-xs font-mono"
              placeholder="e.g. portal or staging"
              value={label}
              onChange={handleLabelChange}
            />
            <span className="flex shrink-0 items-center border-l px-3 text-xs text-muted-foreground font-mono">
              .{runtimeConfig.appDomain}
            </span>
          </div>

          {availability ? (
            <p className={availability.available ? "text-xs text-success  font-medium" : "text-xs text-destructive"}>
              {availability.available ? `${availability.hostname} is available.` : availability.message ?? "Subdomain is not available."}
            </p>
          ) : null}
        </div>

        <DialogFooter className="flex items-center justify-between sm:justify-between">
          <Button variant="outline" size="sm" onClick={() => onOpenChange(false)}>
            Close
          </Button>
          <div className="flex items-center gap-2">
            <Button
              variant="outline"
              size="sm"
              onClick={() => void checkAvailability()}
              disabled={!canCheck}
            >
              {checking ? "Checking..." : "Check availability"}
            </Button>
            {availability?.available ? (
              <Button size="sm" onClick={() => void saveSubdomain()} disabled={!canSave}>
                {saving ? "Adding..." : "Add subdomain"}
              </Button>
            ) : null}
          </div>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
