import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { KeyRound } from "lucide-react";
import { FormEvent, useEffect, useState } from "react";
import { issueTelemetryCredential } from "../../../api/telemetryApi";
import { CopyButton } from "../../../core/components/CopyButton";
import type { TelemetryCredentialIssueResponse } from "../../../models/controlPlane";

type Props = {
  appId: string;
  services: string[];
  open: boolean;
  initialServiceName?: string;
  initialName?: string;
  onOpenChange: (open: boolean) => void;
  onIssued: (issued: TelemetryCredentialIssueResponse) => void;
};

type ExpirationPreset = "6-months" | "3-months" | "30-days" | "custom";

export function IssueTelemetryCredentialDialog({
  appId,
  services,
  open,
  initialServiceName,
  initialName,
  onOpenChange,
  onIssued
}: Props) {
  const [serviceName, setServiceName] = useState(initialServiceName ?? services[0] ?? "");
  const [name, setName] = useState(initialName ?? "");
  const [expirationPreset, setExpirationPreset] = useState<ExpirationPreset>("3-months");
  const [customExpiresOn, setCustomExpiresOn] = useState("");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [issued, setIssued] = useState<TelemetryCredentialIssueResponse | null>(null);
  const defaultServiceName = services[0] ?? "";

  useEffect(() => {
    if (open) {
      setServiceName(initialServiceName ?? defaultServiceName);
      setName(initialName ?? "");
      setExpirationPreset("3-months");
      setCustomExpiresOn("");
      setError(null);
      setIssued(null);
    }
  }, [defaultServiceName, initialName, initialServiceName, open]);

  async function submit(event: FormEvent) {
    event.preventDefault();
    const trimmedService = serviceName.trim();
    const trimmedName = name.trim();
    if (!trimmedService || !trimmedName || (expirationPreset === "custom" && !customExpiresOn) || saving) {
      return;
    }

    setSaving(true);
    setError(null);
    try {
      const response = await issueTelemetryCredential(appId, {
        serviceName: trimmedService,
        name: trimmedName,
        expiresAt: resolveExpiration(expirationPreset, customExpiresOn)
      });
      setIssued(response);
      onIssued(response);
    } catch (submitError) {
      setError(submitError instanceof Error ? submitError.message : "Unable to issue credential.");
    } finally {
      setSaving(false);
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Issue ingestion credential</DialogTitle>
          <DialogDescription>
            Bound to one app service. The raw token is shown once and cannot be recovered.
          </DialogDescription>
        </DialogHeader>

        {issued ? (
          <>
            <div className="space-y-3">
              <div className="rounded-md border border-border bg-warning-surface p-3">
                <p className="text-sm font-medium">Copy this token now</p>
                <p className="mt-1 text-xs text-muted-foreground">
                  Store it as an app secret named MINICLOUD_OTLP_LOGS_HEADERS. It will never be shown again.
                </p>
                <div className="mt-2 flex flex-wrap items-center gap-2">
                  <code className="max-w-full break-all rounded-md bg-background px-3 py-2 text-xs">{issued.token}</code>
                  <CopyButton value={issued.token} />
                </div>
              </div>
              <p className="text-xs text-muted-foreground">
                Install the new value before revoking the previous credential so export keeps flowing.
              </p>
            </div>
            <DialogFooter>
              <Button onClick={() => onOpenChange(false)} variant="outline">Done</Button>
            </DialogFooter>
          </>
        ) : (
          <form className="space-y-4" onSubmit={submit}>
            <div className="grid gap-2">
              <Label htmlFor="telemetry-credential-service">Service</Label>
              {services.length > 0 ? (
                <Select value={serviceName} onValueChange={setServiceName}>
                  <SelectTrigger id="telemetry-credential-service">
                    <SelectValue placeholder="Select a deployed service" />
                  </SelectTrigger>
                  <SelectContent>
                    {services.map((service) => (
                      <SelectItem key={service} value={service}>{service}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              ) : (
                <Input
                  id="telemetry-credential-service"
                  onChange={(event) => setServiceName(event.target.value)}
                  placeholder="backend"
                  value={serviceName}
                />
              )}
              <p className="text-xs text-muted-foreground">The service must be deployed before a credential can be issued.</p>
            </div>
            <div className="grid gap-2">
              <Label htmlFor="telemetry-credential-name">Name</Label>
              <Input
                id="telemetry-credential-name"
                onChange={(event) => setName(event.target.value)}
                placeholder="production"
                value={name}
              />
            </div>
            <div className="grid gap-2">
              <Label htmlFor="telemetry-credential-expires">Expires</Label>
              <Select
                value={expirationPreset}
                onValueChange={(value) => setExpirationPreset(value as ExpirationPreset)}
              >
                <SelectTrigger id="telemetry-credential-expires">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="6-months">6 months</SelectItem>
                  <SelectItem value="3-months">3 months</SelectItem>
                  <SelectItem value="30-days">30 days</SelectItem>
                  <SelectItem value="custom">Custom</SelectItem>
                </SelectContent>
              </Select>
              {expirationPreset === "custom" ? (
                <div className="grid gap-2">
                  <Label htmlFor="telemetry-credential-custom-expires">Expiration date</Label>
                  <Input
                    id="telemetry-credential-custom-expires"
                    onChange={(event) => setCustomExpiresOn(event.target.value)}
                    type="date"
                    value={customExpiresOn}
                  />
                </div>
              ) : null}
              <p className="text-xs text-muted-foreground">Maximum 365 days.</p>
            </div>
            {error ? <p className="text-sm text-destructive">{error}</p> : null}
            <DialogFooter>
              <Button onClick={() => onOpenChange(false)} type="button" variant="outline">Cancel</Button>
              <Button
                disabled={!serviceName.trim() || !name.trim() || (expirationPreset === "custom" && !customExpiresOn) || saving}
                type="submit"
              >
                <KeyRound className="mr-2 h-4 w-4" />
                Issue credential
              </Button>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  );
}

function resolveExpiration(preset: ExpirationPreset, customExpiresOn: string) {
  if (preset === "custom") {
    return new Date(`${customExpiresOn}T23:59:59.999`).toISOString();
  }

  const expiration = new Date();
  expiration.setDate(expiration.getDate() + (preset === "6-months" ? 180 : preset === "3-months" ? 90 : 30));
  return expiration.toISOString();
}
