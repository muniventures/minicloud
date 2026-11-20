import { Badge } from "@/components/ui/badge";
import type { DevelopmentWorkspaceVmResources } from "@/api/workspaceFilesystemApi";
import type { DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspaceVmResourceChart } from "./WorkspaceVmResourceChart";

export function formatBytes(bytes: number | null | undefined) {
  if (bytes == null) return "Unknown";
  const units = ["B", "KiB", "MiB", "GiB", "TiB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${unit === 0 ? value : value.toFixed(value >= 100 ? 0 : 1)} ${units[unit]}`;
}

export function formatTime(value: string | null | undefined) {
  return value ? new Date(value).toLocaleString() : "unknown";
}

const stateLabel = {
  current: "Current", stale: "Stale", historical: "Last known", unsupported: "Not supported",
} as const;

type Filesystem = DevelopmentWorkspaceVmResources["filesystems"][number];

/** Older images report only paths; newer ones say which storage tier each filesystem holds. */
function filesystemLabel(fs: Filesystem) {
  if (fs.roles?.includes("workspace")) return `Retained volume ${fs.paths.join(", ")}`;
  if (fs.roles?.includes("system")) return `System disk ${fs.paths.join(", ")}`;
  return `Disk ${fs.paths.join(", ")}`;
}

export function WorkspaceVmResources({ workspaceId, organizationId, vm, active = true }: {
  workspaceId: string; organizationId: string; vm: DevelopmentWorkspaceVm;
  /** Whether the VM's resources are on screen; history refreshes only while active. */
  active?: boolean;
}) {
  const resources = vm.resources;
  const label = `Resources for dev environment ${vm.id}`;
  const chart = <WorkspaceVmResourceChart workspaceId={workspaceId} organizationId={organizationId} vm={vm} active={active} />;
  if (!resources) {
    if (vm.phase !== "ready" || vm.desiredState !== "running") return null;
    return <section aria-label={label} className="border-t px-5 py-3 text-sm">
      <h3 className="font-medium">Resource usage</h3>
      <p className="text-xs text-muted-foreground">Waiting for the first sample from this dev environment.</p>
      {chart}
    </section>;
  }
  if (resources.state === "unsupported")
    return <section aria-label={label} className="border-t px-5 py-3 text-sm">
      <h3 className="font-medium">Resource usage <Badge variant="outline">Not supported</Badge></h3>
      <p className="text-muted-foreground">This dev environment image does not report CPU, memory or disk usage. Rebuild with a newer image to see them.</p>
    </section>;
  const { cpu, memory } = resources;
  return <section aria-label={label} className="border-t px-5 py-3 text-sm">
    <h3 className="flex flex-wrap items-center gap-2 font-medium">Resource usage<span className="font-normal text-muted-foreground">· Latest reading</span>
      <Badge variant={resources.state === "current" ? "secondary" : "outline"}>{stateLabel[resources.state]}</Badge>
    </h3>
    <p className="text-xs text-muted-foreground">
      {resources.state === "historical" ? "The dev environment is not running. Values are from the last sample at "
        : resources.state === "stale" ? "No fresh sample for over 2 minutes. Last sample at "
        : "Sampled at "}{formatTime(resources.observedAt)}.
    </p>
    <dl className="mt-2 grid gap-2 sm:grid-cols-3">
      <div><dt className="text-muted-foreground">CPU</dt><dd>
        {cpu?.utilizationPercent != null
          ? `${cpu.utilizationPercent.toFixed(0)}% of ${cpu.logicalCount} cores over ${Math.round(cpu.windowSeconds ?? 0)}s`
          : cpu ? `${cpu.logicalCount} cores · utilization not yet measured` : "Unavailable"}
      </dd></div>
      <div><dt className="text-muted-foreground">Memory</dt><dd>
        {memory ? `${formatBytes(memory.availableBytes)} available of ${formatBytes(memory.totalBytes)}` : "Unavailable"}
      </dd></div>
      {resources.filesystems.length === 0 && <div><dt className="text-muted-foreground">Disk</dt><dd>Unavailable</dd></div>}
      {resources.filesystems.map(fs => <div key={fs.paths.join(",")}>
        <dt className="text-muted-foreground">{filesystemLabel(fs)}</dt>
        <dd>{formatBytes(fs.availableBytes)} available of {formatBytes(fs.totalBytes)}
          {fs.inodesTotal != null && fs.inodesAvailable != null && <span className="block text-xs text-muted-foreground">
            {fs.inodesAvailable.toLocaleString()} of {fs.inodesTotal.toLocaleString()} file slots free</span>}
          {!!fs.nestedMountsExcluded && <span className="block text-xs text-muted-foreground">
            Excludes {fs.nestedMountsExcluded} separately mounted folder{fs.nestedMountsExcluded === 1 ? "" : "s"}</span>}
          {!!fs.scratchBindMounts && <span className="block text-xs text-muted-foreground">
            {fs.scratchBindMounts} build output folder{fs.scratchBindMounts === 1 ? " is" : "s are"} kept on scratch storage, not counted here</span>}
          {fs.roles?.includes("scratch") && <span className="block text-xs text-muted-foreground">
            Also holds scratch storage: caches and build outputs, regenerated automatically and not kept when the VM is replaced</span>}
        </dd>
      </div>)}
    </dl>
    {chart}
  </section>;
}
