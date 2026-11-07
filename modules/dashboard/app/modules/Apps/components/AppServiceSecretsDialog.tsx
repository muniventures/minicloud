import { CollectionToolbar } from "@/core/components/CollectionToolbar";
import { useRecordCollection } from "@/core/components/useRecordCollection";
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
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { KeyRound, RefreshCw, Trash2 } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import {
  deleteAppServiceSecret,
  listAppServiceSecrets,
  setAppServiceSecret,
  updateAppServiceSecret
} from "../../../api/appsApi";
import { EmptyState } from "../../../core/components/EmptyState";
import { StatusBadge } from "../../../core/components/StatusBadge";
import type { AppServiceSecretResponse } from "../../../models/controlPlane";

type Props = {
  appId: string;
  serviceName: string | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
};

export function AppServiceSecretsDialog({ appId, serviceName, open, onOpenChange }: Props) {
  const [secrets, setSecrets] = useState<AppServiceSecretResponse[]>([]);
  const [name, setName] = useState("");
  const [value, setValue] = useState("");
  const [editingSecretId, setEditingSecretId] = useState<string | null>(null);
  const [replacementValue, setReplacementValue] = useState("");
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [busySecretId, setBusySecretId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const loadSecrets = useCallback(async () => {
    if (!serviceName) {
      return;
    }

    setLoading(true);
    setError(null);
    try {
      setSecrets(await listAppServiceSecrets(appId, serviceName));
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : "Unable to load secrets.");
    } finally {
      setLoading(false);
    }
  }, [appId, serviceName]);

  useEffect(() => {
    if (!open) {
      setName("");
      setValue("");
      setEditingSecretId(null);
      setReplacementValue("");
      setError(null);
      return;
    }

    void loadSecrets();
  }, [loadSecrets, open]);

  const saveSecret = useCallback(async () => {
    const trimmedName = name.trim();
    if (!serviceName || !trimmedName || !value) {
      return;
    }

    setSaving(true);
    setError(null);
    try {
      await setAppServiceSecret(appId, serviceName, trimmedName, value);
      setName("");
      setValue("");
      await loadSecrets();
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : "Unable to save secret.");
    } finally {
      setSaving(false);
    }
  }, [appId, loadSecrets, name, serviceName, value]);

  const updateSecret = useCallback(async () => {
    if (!editingSecretId || !replacementValue) {
      return;
    }

    setBusySecretId(editingSecretId);
    setError(null);
    try {
      await updateAppServiceSecret(appId, editingSecretId, replacementValue);
      setEditingSecretId(null);
      setReplacementValue("");
      await loadSecrets();
    } catch (updateError) {
      setError(updateError instanceof Error ? updateError.message : "Unable to update secret.");
    } finally {
      setBusySecretId(null);
    }
  }, [appId, editingSecretId, loadSecrets, replacementValue]);

  const deleteSecret = useCallback(async (secretId: string) => {
    if (!window.confirm("Delete this secret?")) {
      return;
    }

    setBusySecretId(secretId);
    setError(null);
    try {
      await deleteAppServiceSecret(appId, secretId);
      await loadSecrets();
    } catch (deleteError) {
      setError(deleteError instanceof Error ? deleteError.message : "Unable to delete secret.");
    } finally {
      setBusySecretId(null);
    }
  }, [appId, loadSecrets]);

  const canSave = Boolean(serviceName && name.trim() && value && !saving);
  const canUpdate = Boolean(editingSecretId && replacementValue && busySecretId !== editingSecretId);

  const collection = useRecordCollection(`${appId}:${serviceName}:secrets`, secrets, { name: item => item.name, status: item => item.status, updatedAt: item => item.updatedAt }, item => item.id);
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-3xl max-h-[90dvh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>Secrets</DialogTitle>
          <DialogDescription>{serviceName ? `Manage metadata and values for ${serviceName}. Existing values are never shown.` : ""}</DialogDescription>
        </DialogHeader>
        <CollectionToolbar label="secrets" {...collection.toolbar} loading={loading} onRefresh={() => loadSecrets().catch(error => setError(String(error)))} />

        <div className="grid gap-3 rounded-md border p-3 sm:grid-cols-[1fr_1fr_auto]">
          <div className="space-y-2">
            <Label htmlFor="secret-name">Name</Label>
            <Input id="secret-name" onChange={(event) => setName(event.target.value)} placeholder="DATABASE_URL" value={name} />
          </div>
          <div className="space-y-2">
            <Label htmlFor="secret-value">Value</Label>
            <Input id="secret-value" onChange={(event) => setValue(event.target.value)} type="password" value={value} />
          </div>
          <div className="flex items-end">
            <Button disabled={!canSave} onClick={() => void saveSecret()}>
              <KeyRound className="mr-2 h-4 w-4" />
              Save
            </Button>
          </div>
        </div>

        {error ? <p className="text-sm text-destructive">{error}</p> : null}

        <div className="overflow-hidden rounded-md border">
          {loading && secrets.length === 0 ? (
            <p className="p-4 text-sm text-muted-foreground">Loading secrets...</p>
          ) : secrets.length === 0 ? (
            <div className="p-4">
              <EmptyState title="No secrets">Add service secrets for the next deployment.</EmptyState>
            </div>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>Updated</TableHead>
                  <TableHead className="text-right">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {collection.records.map((secret) => (
                  <TableRow key={secret.id}>
                    <TableCell data-label="Name" className="font-mono text-xs">{secret.name}</TableCell>
                    <TableCell data-label="Status">
                      <StatusBadge status={secret.status} />
                    </TableCell>
                    <TableCell data-label="Updated">{formatDate(secret.updatedAt)}</TableCell>
                    <TableCell data-label="Actions">
                      {editingSecretId === secret.id ? (
                        <div className="flex flex-wrap justify-end gap-2">
                          <Input
                            aria-label={`New value for ${secret.name}`}
                            className="w-52"
                            onChange={(event) => setReplacementValue(event.target.value)}
                            type="password"
                            value={replacementValue}
                          />
                          <Button disabled={!canUpdate} onClick={() => void updateSecret()} size="sm">
                            Save
                          </Button>
                          <Button
                            onClick={() => {
                              setEditingSecretId(null);
                              setReplacementValue("");
                            }}
                            size="sm"
                            variant="outline"
                          >
                            Cancel
                          </Button>
                        </div>
                      ) : (
                        <div className="flex flex-wrap justify-end gap-2">
                          <Button
                            disabled={busySecretId === secret.id}
                            onClick={() => {
                              setEditingSecretId(secret.id);
                              setReplacementValue("");
                            }}
                            size="sm"
                            variant="outline"
                          >
                            <RefreshCw className="mr-2 h-4 w-4" />
                            Replace
                          </Button>
                          <Button
                            disabled={busySecretId === secret.id}
                            onClick={() => void deleteSecret(secret.id)}
                            size="sm"
                            variant="destructive"
                          >
                            <Trash2 className="mr-2 h-4 w-4" />
                            Delete
                          </Button>
                        </div>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </div>

        <DialogFooter>
          <Button onClick={() => onOpenChange(false)} variant="outline">
            Close
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}
