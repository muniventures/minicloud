import { CollectionToolbar } from "@/core/components/CollectionToolbar";
import { useRecordCollection } from "@/core/components/useRecordCollection";
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle, AlertDialogTrigger } from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { KeyRound, Plus, RefreshCw } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { listTelemetryCredentials, revokeTelemetryCredential } from "../../../api/telemetryApi";
import { CopyButton } from "../../../core/components/CopyButton";
import { EmptyState } from "../../../core/components/EmptyState";
import type { TelemetryCredentialIssueResponse, TelemetryCredentialResponse } from "../../../models/controlPlane";
import { IssueTelemetryCredentialDialog } from "./IssueTelemetryCredentialDialog";

type Props = {
  appId: string;
  services: string[];
  refreshToken: number;
  onChanged: () => void;
};

export function TelemetryCredentialsCard({ appId, services, refreshToken, onChanged }: Props) {
  const [credentials, setCredentials] = useState<TelemetryCredentialResponse[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [rotateTarget, setRotateTarget] = useState<TelemetryCredentialResponse | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [revealed, setRevealed] = useState<TelemetryCredentialIssueResponse | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true);
    setError(null);
    try {
      setCredentials(await listTelemetryCredentials(appId, signal));
    } catch (loadError) {
      if (loadError instanceof Error && loadError.name === "AbortError") {
        return;
      }

      setError(loadError instanceof Error ? loadError.message : "Unable to load credentials.");
    } finally {
      setLoading(false);
    }
  }, [appId]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load, refreshToken]);

  async function revoke(credentialId: string) {
    setBusyId(credentialId);
    setError(null);
    try {
      await revokeTelemetryCredential(appId, credentialId);
      await load();
      onChanged();
    } catch (revokeError) {
      setError(revokeError instanceof Error ? revokeError.message : "Unable to revoke credential.");
    } finally {
      setBusyId(null);
    }
  }

  const collection = useRecordCollection(`${appId}:credentials`, credentials, { name: item => item.name, service: item => item.serviceName, state: credentialStatus, createdAt: item => item.createdAt }, item => item.id);
  const activeCount = credentials.filter((credential) => credentialStatus(credential) !== "revoked"
    && credentialStatus(credential) !== "expired").length;
  const hasActive = activeCount > 0;

  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between">
        <CardTitle>Ingestion credentials</CardTitle>

      </CardHeader>
      <CardContent className="space-y-4"><CollectionToolbar label="credentials" {...collection.toolbar} onRefresh={() => load()} loading={loading} primary={{ id: "issue", label: "Issue credential", onClick: () => { setRotateTarget(null); setDialogOpen(true); } }} />
        {revealed ? (
          <div className="rounded-md border border-border bg-warning-surface p-3">
            <p className="text-sm font-medium">New token for {revealed.serviceName} (shown once)</p>
            <div className="mt-2 flex flex-wrap items-center gap-2">
              <code className="max-w-full break-all rounded-md bg-background px-3 py-2 text-xs">{revealed.token}</code>
              <CopyButton value={revealed.token} />
              <Button onClick={() => setRevealed(null)} size="sm" variant="outline">Done</Button>
            </div>
          </div>
        ) : null}

        {!loading && !hasActive ? (
          <p className="rounded-md border border-dashed p-3 text-sm text-muted-foreground">
            No active ingestion credential. Exporters using an old, expired, or revoked token are rejected;
            issue a credential and store it as MINICLOUD_OTLP_LOGS_HEADERS to resume export.
          </p>
        ) : null}

        {error ? <p className="text-sm text-destructive">{error}</p> : null}

        {loading && credentials.length === 0 ? (
          <p className="text-sm text-muted-foreground">Loading credentials...</p>
        ) : credentials.length === 0 ? (
          <EmptyState title="No credentials">Issue a service-scoped credential for the OTLP exporter.</EmptyState>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Service</TableHead>
                <TableHead>Prefix</TableHead>
                <TableHead>Version</TableHead>
                <TableHead>Expires</TableHead>
                <TableHead>Last used</TableHead>
                <TableHead>Status</TableHead>
                <TableHead className="text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {collection.records.map((credential) => {
                const status = credentialStatus(credential);
                return (
                  <TableRow key={credential.id}>
                    <TableCell data-label="Name" className="font-medium">{credential.name}</TableCell>
                    <TableCell data-label="Service">{credential.serviceName}</TableCell>
                    <TableCell data-label="Prefix" className="font-mono text-xs">{credential.keyPrefix}</TableCell>
                    <TableCell data-label="Version">v{credential.credentialVersion}</TableCell>
                    <TableCell data-label="Expires">{credential.expiresAt ? formatDate(credential.expiresAt) : "Never"}</TableCell>
                    <TableCell data-label="Last used">{credential.lastUsedAt ? formatDate(credential.lastUsedAt) : "Never"}</TableCell>
                    <TableCell data-label="Status">
                      <span className={status === "active" ? "text-success" : status === "expiring" ? "text-warning" : "text-muted-foreground"}>
                        {status === "active" ? "Active" : status === "expiring" ? "Expiring soon" : status === "expired" ? "Expired" : "Revoked"}
                      </span>
                    </TableCell>
                    <TableCell data-label="Actions">
                      <div className="flex flex-wrap justify-end gap-2">
                        {status !== "revoked" ? (
                          <Button
                            disabled={busyId === credential.id}
                            onClick={() => {
                              setRotateTarget(credential);
                              setDialogOpen(true);
                            }}
                            size="sm"
                            variant="outline"
                            title="Issue a replacement credential, install it, then revoke this one."
                          >
                            <RefreshCw className="mr-2 h-4 w-4" />
                            Rotate
                          </Button>
                        ) : null}
                        {status !== "revoked" ? (
                          <AlertDialog>
                            <AlertDialogTrigger asChild>
                              <Button disabled={busyId === credential.id} size="sm" variant="destructive">
                                Revoke
                              </Button>
                            </AlertDialogTrigger>
                            <AlertDialogContent>
                              <AlertDialogHeader>
                                <AlertDialogTitle>Revoke ingestion credential?</AlertDialogTitle>
                                <AlertDialogDescription>
                                  Exporters using this token stop authenticating within 30 seconds.
                                </AlertDialogDescription>
                              </AlertDialogHeader>
                              <AlertDialogFooter>
                                <AlertDialogCancel>Cancel</AlertDialogCancel>
                                <AlertDialogAction onClick={() => void revoke(credential.id)}>Revoke</AlertDialogAction>
                              </AlertDialogFooter>
                            </AlertDialogContent>
                          </AlertDialog>
                        ) : null}
                      </div>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        )}
        <p className="text-xs text-muted-foreground">
          <KeyRound className="mr-1 inline h-3 w-3" />
          Listing shows metadata only. Raw tokens are never retrievable after issuance.
        </p>
      </CardContent>

      <IssueTelemetryCredentialDialog
        appId={appId}
        services={services}
        open={dialogOpen}
        initialServiceName={rotateTarget?.serviceName}
        initialName={rotateTarget ? `${rotateTarget.name}-rotated` : undefined}
        onOpenChange={(open) => {
          setDialogOpen(open);
          if (!open) {
            setRotateTarget(null);
          }
        }}
        onIssued={(issued) => {
          setRevealed(issued);
          void load();
          onChanged();
        }}
      />
    </Card>
  );
}

function credentialStatus(credential: TelemetryCredentialResponse) {
  if (credential.revokedAt) {
    return "revoked" as const;
  }

  if (credential.expiresAt && new Date(credential.expiresAt).getTime() <= Date.now()) {
    return "expired" as const;
  }

  if (credential.expiresAt && new Date(credential.expiresAt).getTime() - Date.now() < 14 * 24 * 60 * 60 * 1000) {
    return "expiring" as const;
  }

  return "active" as const;
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}
