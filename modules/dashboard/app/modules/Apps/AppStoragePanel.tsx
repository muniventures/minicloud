import { useEffect, useState } from "react";
import { listAppStorage } from "@/api/appsApi";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { CopyButton } from "@/core/components/CopyButton";
import type { AppServiceInventoryResponse, AppStorageVolume } from "@/models/controlPlane";

const example = `    storage:
      uploads:
        sizeGb: 20
        mountPath: /app/uploads
        uid: 1000
        gid: 1000`;

export function AppStoragePanel({ appId, services }: { appId: string; services: AppServiceInventoryResponse[] }) {
  const [volumes, setVolumes] = useState<AppStorageVolume[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    setError(null);
    void listAppStorage(appId, controller.signal).then((items) => {
      if (!controller.signal.aborted) setVolumes(items);
    }).catch((failure) => {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : "Unable to load storage.");
    });
    return () => controller.abort();
  }, [appId, revision]);
  return <div className="space-y-4">
    <Card>
      <CardHeader className="flex-row items-center justify-between"><CardTitle>Durable storage</CardTitle><Button variant="outline" onClick={() => setRevision((value) => value + 1)}>Refresh storage</Button></CardHeader>
      <CardContent className="space-y-3">
        <p className="text-sm text-muted-foreground">Hetzner volumes keep app files across redeploys. Removing a mount or deleting an app retains its volumes and their provider charges.</p>
        {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
        {volumes === null && !error ? <p className="text-sm">Loading storage…</p> : null}
        {volumes?.length === 0 ? <p className="text-sm">No storage volumes registered.</p> : null}
        {volumes?.map((volume) => {
          const mounts = services.flatMap((service) => {
            const mount = service.storage?.[volume.name];
            return mount ? [`${service.name}: ${mount.mountPath}`] : [];
          });
          return <div key={volume.name} className="rounded-md border p-3 text-sm">
            <p className="font-medium">{volume.name} · {volume.sizeGb} GB · {volume.region}</p>
            <p>{volume.status === "registered" ? "Registered" : volume.status === "creation_unresolved" ? "Creation needs attention — inspect the deployment error" : "Pending creation"}</p>
            <p className="break-all font-mono text-xs">Hetzner volume: {volume.providerVolumeId ?? "Not recorded"}</p>
            <p className="break-all">{mounts.length ? mounts.join(" · ") : "No active service mounts"}</p>
          </div>;
        })}
        <p className="text-xs text-muted-foreground">Registration reflects the last recorded provider state. It does not confirm current disk or mount health. Back up volume data separately.</p>
      </CardContent>
    </Card>
    <Card>
      <CardHeader><CardTitle>Add storage to a service</CardTitle></CardHeader>
      <CardContent className="space-y-3 text-sm">
        <p>Add this mapping under a service in <code>minicloud.yml</code>, adjust the name, size, path and container user, then run <code>minicloud deploy</code>. Available on Hetzner apps only.</p>
        <pre className="overflow-x-auto rounded-md bg-muted p-3 text-xs">{example}</pre>
        <CopyButton value={example} label="Copy storage configuration" />
        <p className="text-muted-foreground">Use 10–10,000 GB per volume. Increase the size to grow; shrinking and automatic ownership changes are unsupported. Reuse a name to reconnect its data. Renaming creates an empty volume. New disks incur provider charges; backups are separate.</p>
      </CardContent>
    </Card>
  </div>;
}
