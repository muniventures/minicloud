import { runtimeConfig } from "@/core/config/runtimeConfig";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useCallback, useEffect, useMemo, useState, type ChangeEvent } from "react";
import { checkAppDomainAvailability, createAppDomain } from "../../../api/environmentsApi";
import type { DomainAvailabilityResponse } from "../../../models/controlPlane";

type Props = {
  appId: string;
  serviceName: string | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onSaved: () => Promise<void>;
};

export function AddSubdomainDialog({ appId, serviceName, open, onOpenChange, onSaved }: Props) {
  const [label, setLabel] = useState("");
  const [availability, setAvailability] = useState<DomainAvailabilityResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [checking, setChecking] = useState(false);
  const [saving, setSaving] = useState(false);

  const trimmedLabel = useMemo(() => label.trim(), [label]);
  const canCheck = Boolean(serviceName && trimmedLabel && !checking && !saving);
  const canSave = Boolean(serviceName && availability?.available && availability.label === trimmedLabel.toLowerCase() && !saving);

  useEffect(() => {
    if (!open) {
      setLabel("");
      setAvailability(null);
      setError(null);
      setChecking(false);
      setSaving(false);
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

  const save = useCallback(async () => {
    if (!canSave || !serviceName) {
      return;
    }

    setError(null);
    setSaving(true);
    try {
      await createAppDomain(appId, serviceName, trimmedLabel);
      await onSaved();
      onOpenChange(false);
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : "Unable to save subdomain.");
    } finally {
      setSaving(false);
    }
  }, [appId, canSave, onOpenChange, onSaved, serviceName, trimmedLabel]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Add subdomain</DialogTitle>
          <DialogDescription>{serviceName ? `Route ${serviceName} from an additional Minicloud subdomain.` : ""}</DialogDescription>
        </DialogHeader>

        <div className="space-y-2">
          <Label htmlFor="subdomain-label">Subdomain</Label>
          <div className="flex rounded-md border bg-background focus-within:ring-1 focus-within:ring-ring">
            <Input
              id="subdomain-label"
              className="border-0 shadow-none focus-visible:ring-0"
              placeholder="my-app"
              value={label}
              onChange={handleLabelChange}
            />
            <span className="flex shrink-0 items-center border-l px-3 text-sm text-muted-foreground">.{runtimeConfig.appDomain}</span>
          </div>
        </div>

        {availability ? (
          <p className={availability.available ? "text-sm text-success" : "text-sm text-destructive"}>
            {availability.available ? `${availability.hostname} is available.` : availability.message ?? "Subdomain is not available."}
          </p>
        ) : null}
        {error ? <p className="text-sm text-destructive">{error}</p> : null}

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={checking || saving}>
            Cancel
          </Button>
          <Button variant="outline" onClick={() => void checkAvailability()} disabled={!canCheck}>
            {checking ? "Checking..." : "Check availability"}
          </Button>
          {availability?.available ? (
            <Button onClick={() => void save()} disabled={!canSave}>
              {saving ? "Saving..." : "Save"}
            </Button>
          ) : null}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
