import { useCallback, useEffect, useRef, useState } from "react";
import { AlertTriangle, MoreHorizontal } from "lucide-react";
import { getWorkspaceLaunchUrl, mutateWorkspaceVm, workspaceTask } from "@/api/workspacesApi";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspaceLaunchLink } from "./WorkspaceLaunchLink";
import { WorkspaceSelectionFields, type WorkspaceSelection } from "./WorkspaceSelectionFields";
import { WorkspaceVmResources } from "./WorkspaceVmResources";
import { vmStartup, WorkspaceVmStartup } from "./WorkspaceVmStartup";
import { gitSaveLabel, message, phaseLabel, phaseTone, relativeTime, vmName } from "./workspaceVmLabels";

type Props = {
  workspaceId: string;
  organizationId: string;
  vm: DevelopmentWorkspaceVm;
  currency: string;
  refresh: (observation?: DevelopmentWorkspace) => Promise<void>;
  canManage: boolean;
};

const lossDisclosure = "This VM has its own Git-only disk and can't see the primary VM's storage. Untracked files, files ignored by Git and unpushed work can be lost permanently. There is no backup.";
const startingLabel = "Starting VM…";
const toneClass = {
  warning: "border-warning/30 bg-warning-surface text-warning",
  destructive: "",
  success: "border-success/30 bg-success-surface text-success",
  neutral: "",
} as const;
type Launch = { context: string; service: string } & (
  { state: "active"; url: string; deadline: number } | { state: "expired" }
);
type Panel = "resize" | "lease" | "details";

export function WorkspaceVmCard(props: Props) {
  return <VmGenerationCard key={`${props.organizationId}:${props.workspaceId}:${props.vm.id}:${props.vm.generation}`} {...props} />;
}

function VmGenerationCard({ workspaceId, organizationId, vm, currency, refresh, canManage }: Props) {
  const [pending, setPending] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selection, setSelection] = useState<WorkspaceSelection>({
    imageId: vm.nextImageId ?? vm.imageId, profile: vm.nextProfile ?? vm.profile, planId: vm.nextPlanId ?? vm.planId,
  });
  const [selectionReady, setSelectionReady] = useState(false);
  const [taskId, setTaskId] = useState("");
  const [leaseResult, setLeaseResult] = useState<string | null>(null);
  const [previewSlug, setPreviewSlug] = useState("");
  const [previewOpen, setPreviewOpen] = useState(false);
  const [service, setService] = useState("t3");
  const [launch, setLaunch] = useState<Launch | null>(null);
  const [discardOpen, setDiscardOpen] = useState(false);
  const [discardText, setDiscardText] = useState("");
  const [acknowledged, setAcknowledged] = useState(false);
  const [panel, setPanel] = useState<Panel | null>(null);
  const menuTrigger = useRef<HTMLButtonElement>(null);
  const restoreToMenu = useRef(false);
  const mounted = useRef(true);
  const inFlight = useRef(false);
  const request = useRef(0);
  const { ready, starting, failed } = vmStartup(vm);
  const context = JSON.stringify([vm.revision, vm.generation, ready, canManage, service, previewSlug]);
  const currentContext = useRef(context);
  currentContext.current = context;
  const permission = useRef(canManage);
  permission.current = canManage;
  const busy = pending !== null;
  const blocked = busy || !canManage;
  const awaitingGit = vm.phase === "awaiting_git_save";
  const stopping = ["draining", "deleting", "awaiting_git_save"].includes(vm.phase);
  const confirmText = `discard ${vm.id} ${vm.generation}`;
  const name = vmName(vm);
  const imageChanges = !!selection.imageId && selection.imageId !== vm.imageId;
  const startingRequest = pending === startingLabel;
  const visibleLaunch = canManage && launch?.context === context ? launch : null;
  const tone = phaseTone(vm);
  const expireLaunch = useCallback(() => {
    setLaunch(current => current?.state === "active" && current === launch
      ? { context: current.context, service: current.service, state: "expired" } : current);
  }, [launch]);

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; ++request.current; };
  }, []);
  useEffect(() => {
    setSelection({ imageId: vm.nextImageId ?? vm.imageId, profile: vm.nextProfile ?? vm.profile, planId: vm.nextPlanId ?? vm.planId });
  }, [vm.nextImageId, vm.nextProfile, vm.nextPlanId, vm.imageId, vm.profile, vm.planId]);
  useEffect(() => {
    if (!canManage) { ++request.current; setDiscardOpen(false); setDiscardText(""); setAcknowledged(false); setLaunch(null); }
  }, [canManage]);
  useEffect(() => {
    setLaunch(previous => previous?.context === context ? previous : null);
  }, [context]);

  const run = async (label: string, operation: () => Promise<DevelopmentWorkspace | void>, refreshAfter = true) => {
    if (inFlight.current || !permission.current) return;
    inFlight.current = true;
    setPending(label); setError(null);
    if (refreshAfter) { ++request.current; setLaunch(null); }
    try {
      const observation = await operation();
      if (mounted.current && refreshAfter) await refresh(observation || undefined);
    } catch (reason) {
      if (mounted.current) setError(message(reason));
    } finally {
      inFlight.current = false;
      if (mounted.current) setPending(null);
    }
  };
  const action = (operation: "start" | "stop" | "cancel-idle-stop", label: string) =>
    run(label, () => mutateWorkspaceVm(workspaceId, vm.id, organizationId, operation, { generation: vm.generation }));
  // Stopping keeps the generation, so the follow-up start is queued to run once the failed attempt is cleaned up.
  const retryStart = () => run(startingLabel, async () => {
    await mutateWorkspaceVm(workspaceId, vm.id, organizationId, "stop", { generation: vm.generation });
    return mutateWorkspaceVm(workspaceId, vm.id, organizationId, "start", { generation: vm.generation });
  });
  const task = (complete: boolean, cancelled = false) => {
    if (!taskId.trim() || (!complete && !ready)) return;
    void run(cancelled ? "Cancelling task…" : complete ? "Completing task…" : "Renewing lease…", async () => {
      const lease = await workspaceTask(workspaceId, vm.id, organizationId, taskId.trim(), vm.generation, complete, cancelled);
      if (mounted.current) setLeaseResult(`${lease.taskId}: ${lease.status}; generation ${lease.generation}; expires ${lease.expiresAt}`);
    });
  };
  const authorize = (target: string) => {
    if (!ready || !target.trim() || inFlight.current || !canManage) return;
    setService(target);
    setLaunch(null);
    const expectedContext = JSON.stringify([vm.revision, vm.generation, ready, canManage, target, previewSlug]);
    currentContext.current = expectedContext;
    const sequence = ++request.current;
    void run("Getting a secure link…", async () => {
      const current = () => mounted.current && sequence === request.current && currentContext.current === expectedContext && permission.current;
      const report = (error: string) => {
        if (current()) setError(`${error} This didn't start the VM. Check that the VM is running and the preview name is registered, then try again or contact your operator.`);
      };
      let result: Awaited<ReturnType<typeof getWorkspaceLaunchUrl>>;
      try {
        result = await getWorkspaceLaunchUrl(workspaceId, vm.id, organizationId, vm.generation, target);
      } catch {
        report("Couldn't get a secure link.");
        return;
      }
      if (!current()) return;
      let url: URL;
      try { url = new URL(result?.url); }
      catch { report("The link returned was invalid."); return; }
      if (url.protocol !== "https:" || !url.hostname || url.username || url.password) {
        report("The link returned was unsafe.");
        return;
      }
      const deadline = Date.parse(result.expiresAt);
      if (!Number.isFinite(deadline)) { report("The link returned had an invalid expiry."); return; }
      const scope = { context: expectedContext, service: target };
      setLaunch(deadline > Date.now() ? { ...scope, state: "active", url: url.href, deadline } : { ...scope, state: "expired" });
    }, false);
  };
  const closeDiscard = (open: boolean) => {
    setDiscardOpen(open); setDiscardText(""); setAcknowledged(false);
  };
  // The menu closes before the dialog opens; focus then returns to the menu trigger when the dialog closes.
  const fromMenu = (open: () => void) => { restoreToMenu.current = true; open(); };
  const restoreFocus = (event: Event) => {
    if (!restoreToMenu.current) return;
    restoreToMenu.current = false;
    event.preventDefault();
    menuTrigger.current?.focus();
  };

  const showStop = vm.desiredState !== "stopped" || awaitingGit || Boolean(vm.startAfterStop);
  const stopLabel = awaitingGit ? "Retry stop" : "Stop VM";
  const stop = () => void action("stop", awaitingGit ? "Retrying stop…" : "Stopping VM…");
  const startControl = vm.desiredState === "stopped" && <Button disabled={blocked || Boolean(vm.startAfterStop)}
    onClick={() => void action("start", stopping ? "Queueing start…" : startingLabel)}>{stopping ? "Start VM after it stops" : vm.errorCode ? "Retry start" : "Start VM"}</Button>;
  const retryControl = failed && vm.kind === "primary" && canManage && <Button disabled={blocked}
    onClick={() => void retryStart()}>Retry start</Button>;
  const canDiscard = vm.kind === "ephemeral";
  const discardDisabled = blocked || vm.phase === "stopped";

  return <Card role="article" aria-label={`VM ${vm.id}`} className="min-w-0 overflow-hidden">
    <div className="space-y-3 p-5">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <h3 className="min-w-0 break-all text-lg font-semibold">{name}</h3>
        <Badge variant={tone === "destructive" ? "destructive" : "secondary"} className={`shrink-0 ${toneClass[tone]}`}>
          {startingRequest ? startingLabel : phaseLabel(vm)}
        </Badge>
      </div>
      <p className="text-sm text-muted-foreground">
        {vm.profile} · {vm.sku} · {vm.region} · {vm.lastActivityAt
          ? <time dateTime={vm.lastActivityAt} title={vm.lastActivityAt}>Active {relativeTime(vm.lastActivityAt)}</time>
          : "Waiting for first activity"}
      </p>
      {vm.kind === "ephemeral" && <dl className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-[auto_minmax(0,1fr)]">
        <dt className="text-muted-foreground">Repository</dt><dd className="break-all">{vm.repository ?? "Repository unavailable"}</dd>
        <dt className="text-muted-foreground">Git save</dt><dd>{gitSaveLabel(vm)}</dd>
      </dl>}
      <div className="flex flex-wrap items-center gap-2">
        {ready ? <section aria-label={`Connections for VM ${vm.id}`} className="flex flex-wrap items-center gap-2">
          <Button disabled={blocked} onClick={() => authorize("t3")}>Connect to T3</Button>
          <Button variant="outline" disabled={blocked} aria-expanded={previewOpen} onClick={() => {
            ++request.current; setLaunch(null); setPreviewOpen(previous => !previous);
          }}>Open preview</Button>
        </section> : !stopping ? startControl || retryControl
          : startControl || <span className="text-xs text-muted-foreground">{awaitingGit ? "Waiting for Git save" : "Stopping…"}</span>}
        <DropdownMenu modal={false}>
          <DropdownMenuTrigger asChild>
            <Button ref={menuTrigger} size="icon" variant="outline" aria-label={`More actions for ${name}`}>
              <MoreHorizontal aria-hidden="true" className="size-4" />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            {showStop && !awaitingGit && <DropdownMenuItem disabled={blocked} onSelect={stop}>{stopLabel}</DropdownMenuItem>}
            {vm.shutdownDeadline && <DropdownMenuItem disabled={blocked || !["ready", "draining"].includes(vm.phase)}
              onSelect={() => void action("cancel-idle-stop", "Cancelling idle stop…")}>Cancel idle stop</DropdownMenuItem>}
            {canDiscard && !awaitingGit && <DropdownMenuItem disabled={discardDisabled} className="text-destructive focus:text-destructive"
              onSelect={() => fromMenu(() => closeDiscard(true))}>Discard local data…</DropdownMenuItem>}
            {((showStop && !awaitingGit) || vm.shutdownDeadline || (canDiscard && !awaitingGit)) && <DropdownMenuSeparator />}
            <DropdownMenuItem onSelect={() => fromMenu(() => setPanel("resize"))}>Next-start size…</DropdownMenuItem>
            <DropdownMenuItem onSelect={() => fromMenu(() => setPanel("lease"))}>Task leases…</DropdownMenuItem>
            <DropdownMenuItem onSelect={() => fromMenu(() => setPanel("details"))}>VM details…</DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </div>
    {awaitingGit && <section aria-label={`Git save recovery for VM ${vm.id}`} className="border-y border-warning/20 bg-warning-surface p-5 text-sm">
      <div className="flex items-start gap-3">
        <AlertTriangle className="mt-0.5 size-5 shrink-0 text-warning" aria-hidden="true" />
        <div className="min-w-0 space-y-2">
          <h4 className="font-medium">Git save needs attention</h4>
          <p className="break-words text-muted-foreground">Reported reason: {vm.errorMessage ?? vm.errorCode ?? "No reason reported. Ask your operator to inspect this generation's Git-save operation."}</p>
          <p className="text-muted-foreground">Restore repository access, then retry stop. {vm.computeReserved ? "The VM's server is still allocated and may keep charging." : "No server is allocated, but this doesn't confirm the provider deleted it."}</p>
          <ol className="list-inside list-decimal space-y-1 text-xs text-muted-foreground">
            <li>Restore repository access and authentication; check remote availability and resolve any reported Git conflicts.</li>
            <li>Verify the isolated work branch can be pushed, then choose Retry stop for this VM generation.</li>
            <li>Keep the VM while saving, or explicitly discard this generation's local data. Other VMs are not affected.</li>
          </ol>
          <div className="flex flex-wrap gap-2 pt-1">
            <Button variant="outline" disabled={blocked} onClick={stop}>{stopLabel}</Button>
            {canDiscard && <Button variant="ghost" className="text-destructive hover:text-destructive" disabled={discardDisabled}
              onClick={() => closeDiscard(true)}>Discard local data…</Button>}
          </div>
        </div>
      </div>
    </section>}
    <WorkspaceVmStartup vm={vm} pending={startingRequest} />
    {vm.startAfterStop && <p role="status" className="px-5 py-2 text-sm">This VM will start again after it finishes stopping. Select Stop VM to cancel.</p>}
    {pending && !startingRequest && <p role="status" className="px-5 py-2 text-sm">{pending}</p>}
    {!canManage && <p className="px-5 py-2 text-xs text-muted-foreground">You can view this VM but can't start, stop or open it.</p>}
    {vm.errorCode && !awaitingGit && <p role="alert" className="break-words px-5 py-2 text-sm text-destructive">{vm.errorCode}: {vm.errorMessage ?? "No additional details reported."}</p>}
    {error && <p role="alert" className="break-words px-5 py-2 text-sm text-destructive">{error}</p>}
    {ready && previewOpen && <form aria-label={`Open preview for VM ${vm.id}`} className="flex flex-wrap items-end gap-2 border-t px-5 py-3" onSubmit={event => { event.preventDefault(); authorize(previewSlug.trim()); }}>
      <label className="min-w-0 space-y-1 text-sm">Preview name<Input className="w-full sm:w-48" value={previewSlug} disabled={blocked} placeholder="my-preview" required onChange={event => { ++request.current; setLaunch(null); setPreviewSlug(event.target.value); }} /></label>
      <Button variant="outline" disabled={blocked || !previewSlug.trim()}>Get preview link</Button>
    </form>}
    {visibleLaunch?.state === "active" && <WorkspaceLaunchLink url={visibleLaunch.url} deadline={visibleLaunch.deadline}
      label={visibleLaunch.service === "t3" ? "Open T3" : "Open preview"} onExpire={expireLaunch} />}
    <div className={visibleLaunch?.state === "expired" ? "flex flex-wrap items-center gap-2 px-5 py-2 text-sm" : undefined}>
      <p role="status">{visibleLaunch?.state === "expired" ? "Link expired" : ""}</p>
      {visibleLaunch?.state === "expired" && <Button variant="outline" disabled={blocked} onClick={() => authorize(visibleLaunch.service)}>Get a new link</Button>}
    </div>
    <WorkspaceVmResources workspaceId={workspaceId} organizationId={organizationId} vm={vm} />
    <Dialog open={panel !== null} onOpenChange={open => { if (!open) setPanel(null); }}>
      <DialogContent className="max-h-[90vh] max-w-2xl overflow-y-auto" onCloseAutoFocus={restoreFocus}>
        {panel === "resize" && <>
          <DialogHeader>
            <DialogTitle>Next-start size for {name}</DialogTitle>
            <DialogDescription>Applies the next time this VM starts; a running VM isn't resized.</DialogDescription>
          </DialogHeader>
          <form aria-label={`Resize VM ${vm.id}`} className="grid gap-3" onSubmit={event => {
            event.preventDefault();
            if (!selectionReady || !selection.imageId || !selection.planId) return;
            void run("Saving next-start settings…", async () => {
              const observation = await mutateWorkspaceVm(workspaceId, vm.id, organizationId, "resize", { generation: vm.generation, ...selection });
              if (mounted.current) setPanel(null);
              return observation;
            });
          }}>
            <WorkspaceSelectionFields workspaceId={workspaceId} organizationId={organizationId} value={selection} onChange={setSelection} onReadyChange={setSelectionReady} disabled={blocked} />
            <p className="text-xs text-muted-foreground">{imageChanges ? "This also changes the VM's image. " : ""}Next start: size {vm.nextProfile ?? vm.profile} · image {vm.nextImageId ?? vm.imageId} · plan {vm.nextPlanId ?? vm.planId}.</p>
            <PanelFeedback pending={pending} error={error} />
            <DialogFooter><Button variant="outline" disabled={blocked || !selectionReady || !selection.imageId || !selection.planId}>{imageChanges ? "Use this size and image at next start" : "Use this size at next start"}</Button></DialogFooter>
          </form>
        </>}
        {panel === "lease" && <>
          <DialogHeader>
            <DialogTitle>Task leases for {name}</DialogTitle>
            <DialogDescription>Keep-awake leases are bound to VM {vm.id}, generation {vm.generation}, and expire unless renewed. Completing or cancelling a task releases its lease, not the VM.</DialogDescription>
          </DialogHeader>
          <form aria-label={`Task lease for VM ${vm.id}`} className="space-y-3 text-sm" onSubmit={event => { event.preventDefault(); task(false); }}>
            <label className="block space-y-1">Task ID<Input required maxLength={128} value={taskId} disabled={blocked} onChange={event => setTaskId(event.target.value)} /></label>
            {(vm.taskLeases ?? []).map(lease => <p key={`${lease.taskId}:${lease.generation}`}>{lease.taskId}: {lease.status} · generation {lease.generation} · expires {lease.expiresAt}</p>)}
            {!vm.taskLeases?.length && <p className="text-muted-foreground">No task leases reported.</p>}
            {leaseResult && <p role="status">{leaseResult}</p>}
            <PanelFeedback pending={pending} error={error} />
            <div className="flex flex-wrap gap-2">
              <Button disabled={blocked || !taskId.trim() || !ready}>Renew keep-awake lease</Button>
              <Button type="button" variant="outline" disabled={blocked || !taskId.trim()} onClick={() => task(true)}>Complete task</Button>
              <Button type="button" variant="outline" disabled={blocked || !taskId.trim()} onClick={() => task(true, true)}>Cancel task</Button>
            </div>
          </form>
        </>}
        {panel === "details" && <>
          <DialogHeader>
            <DialogTitle>VM details for {name}</DialogTitle>
            <DialogDescription>{ready ? "The VM is running. Opening T3 or a preview never starts a VM." : "T3 and previews open once the VM is running and ready. Viewing this VM never starts it."}</DialogDescription>
          </DialogHeader>
          <dl className="grid gap-3 break-words text-sm sm:grid-cols-2">
            <div><dt className="text-muted-foreground">VM / generation</dt><dd>{vm.id} · Generation {vm.generation} · desired {vm.desiredState} · phase {vm.phase}</dd></div>
            <div><dt className="text-muted-foreground">Placement</dt><dd>{vm.profile} · {vm.sku} · {vm.region} · provider account {vm.providerAccountId}</dd></div>
            <div><dt className="text-muted-foreground">Cost estimates</dt><dd>{vm.computeHourlyRate.toFixed(4)} {currency}/hour · {vm.estimatedMonthlyCompute.toFixed(2)} {currency}/month</dd></div>
            <div><dt className="text-muted-foreground">Server</dt><dd>{vm.computeReserved ? "Allocated — charges may continue until it's deleted." : "Not allocated. This doesn't confirm provider deletion or a final bill."}</dd></div>
            <div><dt className="text-muted-foreground">Activity</dt><dd>{vm.lastActivityAt ? `Last activity: ${vm.lastActivityAt}` : "Waiting for first activity"} · Idle stop deadline: {vm.shutdownDeadline ?? "none reported"}</dd></div>
            <div><dt className="text-muted-foreground">Image / plan / server</dt><dd>{vm.imageId} · {vm.planId} · {vm.providerServerId ?? "none reported"}{vm.lastReadyAt && ` · Last ready: ${vm.lastReadyAt}`}</dd></div>
          </dl>
          <div className="space-y-2 break-words text-xs text-muted-foreground">
            {vm.kind === "primary" ? <p>Only files on this dev environment's storage are kept when the VM is replaced; other disk files are deleted. Storage is charged while the VM is stopped and isn't a backup.</p> : <>
              <p>{lossDisclosure}</p>
              <p>Repository: {vm.repository ?? "unreported"} · {vm.baseRef ?? "unreported"}@{vm.baseCommit ?? "unreported"}</p>
              <p>Work branch: {vm.workBranch ?? "pending"} · Git save: {vm.gitStatus}</p>
              {vm.finalHead && <p>Final local HEAD: {vm.finalHead}</p>}
              {vm.verifiedRemoteCommit && <p>Verified remote: {vm.verifiedRemoteRef ?? "unreported"}@{vm.verifiedRemoteCommit}</p>}
            </>}
            {awaitingGit && <p>Awaiting Git save · generation {vm.generation} · {vm.errorCode ?? vm.gitStatus}</p>}
          </div>
        </>}
      </DialogContent>
    </Dialog>
    <Dialog open={discardOpen && canManage} onOpenChange={open => { if (!inFlight.current) closeDiscard(open); }}>
      <DialogContent onCloseAutoFocus={restoreFocus}>
        <DialogHeader>
          <DialogTitle>Discard VM {vm.id} generation {vm.generation}</DialogTitle>
          <DialogDescription>Irreversibly delete this ephemeral generation's local data without a verified Git save. Ignored, untracked and unpushed data can be lost permanently. Sibling VMs and the primary retained volume are not affected.</DialogDescription>
        </DialogHeader>
        <form aria-label={`Discard VM ${vm.id} generation ${vm.generation}`} className="space-y-4" onSubmit={event => {
          event.preventDefault();
          if (!discardOpen || discardText !== confirmText || !acknowledged) return;
          void run("Discarding this generation…", async () => {
            await mutateWorkspaceVm(workspaceId, vm.id, organizationId, "discard", { generation: vm.generation, discardLocalData: true, acknowledgeIgnoredDataLoss: true });
            if (mounted.current) closeDiscard(false);
          });
        }}>
          <label className="block space-y-2 text-sm">Type {confirmText}<Input autoComplete="off" disabled={blocked} value={discardText} onChange={event => setDiscardText(event.target.value)} /></label>
          <label className="flex items-start gap-2 text-sm"><input type="checkbox" disabled={blocked} checked={acknowledged} onChange={event => setAcknowledged(event.target.checked)} />I acknowledge irreversible ignored, untracked and unpushed local data loss for VM {vm.id}, generation {vm.generation}.</label>
          <PanelFeedback pending={pending} error={error} />
          <DialogFooter>
            <Button type="button" variant="outline" disabled={busy} onClick={() => closeDiscard(false)}>Cancel discard</Button>
            <Button variant="destructive" disabled={blocked || discardText !== confirmText || !acknowledged}>Confirm irreversible discard</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  </Card>;
}

function PanelFeedback({ pending, error }: { pending: string | null; error: string | null }) {
  return <>
    {pending && <p role="status" className="text-sm">{pending}</p>}
    {error && <p role="alert" className="break-words text-sm text-destructive">{error}</p>}
  </>;
}
