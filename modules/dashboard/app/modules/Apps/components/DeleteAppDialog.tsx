import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useCallback, useState } from "react";
import { deleteApp } from "@/api/appsApi";
import type { AppResponse } from "@/models/controlPlane";

export function DeleteAppDialog({ app, onRequested, open: controlledOpen, onOpenChange: setControlledOpen, showTrigger = true }: { app: AppResponse; onRequested: () => void; open?: boolean; onOpenChange?: (open: boolean) => void; showTrigger?: boolean }) {
  const [internalOpen, setInternalOpen] = useState(false);
  const open = controlledOpen ?? internalOpen;
  const setOpen = setControlledOpen ?? setInternalOpen;
  const [confirmation, setConfirmation] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const pending = !!app.deletionStatus && app.deletionStatus !== "failed";
  const onOpenChange = useCallback((next: boolean) => {
    if (busy) return;
    setOpen(next);
    setConfirmation("");
    setError(null);
  }, [busy]);
  const submit = useCallback(async () => {
    if (busy || confirmation !== app.slug) return;
    setBusy(true);
    setError(null);
    try {
      await deleteApp(app.id, confirmation);
      setOpen(false);
      onRequested();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Unable to delete application.");
    } finally {
      setBusy(false);
    }
  }, [app.id, app.slug, busy, confirmation, onRequested]);
  return <>
    {showTrigger ? (
      <Button variant="destructive" disabled={pending} onClick={() => onOpenChange(true)}>
        {pending ? "Deleting application…" : app.deletionStatus === "failed" ? "Retry deletion" : "Delete application"}
      </Button>
    ) : null}
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Delete {app.name}?</DialogTitle>
          <DialogDescription>
            This permanently deletes this application and all its branch apps, servers, databases, domains and secrets. This cannot be undone.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-2">
          <Label htmlFor="delete-app-confirmation">Type {app.slug} to confirm</Label>
          <Input id="delete-app-confirmation" autoComplete="off" value={confirmation} disabled={busy} onChange={(event) => setConfirmation(event.target.value)} />
          {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
        </div>
        <DialogFooter>
          <Button variant="outline" disabled={busy} onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button variant="destructive" disabled={busy || confirmation !== app.slug} onClick={() => void submit()}>
            {busy ? "Requesting deletion…" : "Permanently delete"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </>;
}
