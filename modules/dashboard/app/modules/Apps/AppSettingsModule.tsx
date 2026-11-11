import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { EmptyState } from "@/core/components/EmptyState";
import { PageScaffold } from "@/core/components/PageScaffold";
import { StatusBadge } from "@/core/components/StatusBadge";
import {
  createEnvironment,
  deleteApplication,
  deleteEnvironment,
  getApplication,
  updateApplication,
  updateEnvironment,
} from "../../api/applicationsApi";
import { appsFixtureApplications, appsFixturesEnabled } from "../../api/applicationsFixtures";
import { useAuth } from "../../core/auth/AuthProvider";
import type { ProviderPlan } from "../../api/providersApi";
import type { ApplicationEnvironment, Application, SshAccessRequest } from "../../models/application";
import { planDescription, usePlanCatalog } from "../Providers/usePlanCatalog";
import { EnvironmentSshAccessPanel } from "./components/EnvironmentSshAccessPanel";
import { SshAccessFields } from "./components/SshAccessFields";
import { buildSshAccessRequest, defaultSshAccessDraft, sshErrorMessage, type SshAccessDraft } from "./sshAccess";
import { formatAge } from "./AppsModule";

// Application settings: app metadata, environment management, app deletion.
// Environment settings live per row below; settings links name their scope.
export function AppSettingsModule() {
  const { applicationId } = useParams();
  const { selectedOrganization } = useAuth();
  return <ScopedAppSettings key={`${selectedOrganization?.id}:${applicationId}`} />;
}

function ScopedAppSettings() {
  const { applicationId } = useParams();
  const { selectedOrganization } = useAuth();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const [app, setApp] = useState<Application | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Mutation errors are inline (not the full-page load error) so a failed
  // save keeps the form mounted with the user's input intact.
  const [actionError, setActionError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);

  // Org + app scoped load: switching organizations or applications aborts
  // pending reads and resets state; a response from another organization
  // fails closed instead of rendering.
  const abortRef = useRef<AbortController | null>(null);
  const settingsScope = JSON.stringify([selectedOrganization?.id, applicationId]);
  useEffect(() => {
    abortRef.current?.abort();
    setApp(null);
    setError(null);
    setActionError(null);
    return () => { abortRef.current?.abort(); };
  }, [settingsScope]);

  const load = useCallback(async () => {
    if (!applicationId || !selectedOrganization) return;
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;
    const scopeOrg = selectedOrganization.id;
    setLoading(true);
    setError(null);
    try {
      const next = appsFixturesEnabled()
        ? appsFixtureApplications().find((a) => a.id === applicationId) ?? null
        : await getApplication(applicationId, controller.signal);
      if (controller.signal.aborted) return;
      if (!next) throw new Error(`Application ${applicationId} not found.`);
      if (next.organizationId !== scopeOrg) {
        throw new Error(`Application ${applicationId} does not belong to the selected organization.`);
      }
      setApp(next);
    } catch (e) {
      if (!controller.signal.aborted) {
        setError(e instanceof Error ? e.message : "Unable to load application settings.");
      }
    } finally {
      if (!controller.signal.aborted) setLoading(false);
    }
  }, [applicationId, selectedOrganization?.id]);

  useEffect(() => {
    void load();
  }, [load]);

  const canManage = ["owner", "admin", "developer"].includes(
    selectedOrganization?.role?.toLowerCase() ?? "",
  );
  const catalog = usePlanCatalog(
    canManage && !app?.deletionStatus ? app?.organizationId : undefined,
  );

  if (loading) return <p role="status" className="text-sm text-muted-foreground">Loading application settings…</p>;
  if (error) {
    return (
      <PageScaffold breadcrumbs={[{ label: "Apps", to: "/apps" }]} title="Application settings">
        <div role="alert" className="text-destructive">
          <p>{error}</p>
          <Button variant="outline" size="sm" onClick={() => void load()}>Retry</Button>
        </div>
      </PageScaffold>
    );
  }
  if (!app) return null;

  // Mutation errors stay inline and forms stay mounted: failure keeps the
  // user's input; success reloads. Resolves true/false for callers that
  // should only close editors on success.
  async function act(fn: () => Promise<unknown>, done?: () => void): Promise<boolean> {
    setBusy(true);
    setActionError(null);
    try {
      await fn();
      if (abortRef.current?.signal.aborted) return false;
      if (done) done();
      else await load();
      return true;
    } catch (e) {
      setActionError(sshErrorMessage(e));
      return false;
    } finally {
      setBusy(false);
    }
  }

  return (
    <PageScaffold
      breadcrumbs={[{ label: "Apps", to: "/apps" }, { label: app.name }, { label: "Settings" }]}
      title={`${app.name} settings`}
    >
      <div className="grid max-w-3xl gap-4">
        <Card>
          <CardHeader><CardTitle>Application</CardTitle></CardHeader>
          <CardContent className="space-y-3 text-sm">
            <p className="text-muted-foreground">
              {app.slug} · {app.environmentCount} {app.environmentCount === 1 ? "environment" : "environments"}
              {app.deletionStatus ? <span> · <StatusBadge status={app.deletionStatus} /></span> : null}
            </p>
            {canManage && !app.deletionStatus ? <RenameApp app={app} busy={busy} onSave={(name) => act(() => updateApplication(app.id, { name }))} /> : null}
            {app.deletionError ? <p role="alert" className="text-destructive">{app.deletionError}</p> : null}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Environments</CardTitle>
            <p className="text-sm text-muted-foreground">
              Plan, database, services, domains and runtime belong to each environment. New persistent environments start with empty secrets; nothing is copied from another environment.
            </p>
          </CardHeader>
          <CardContent className="space-y-4">
            {actionError ? <p role="alert" className="text-sm text-destructive">{actionError}</p> : null}
            {app.deletionStatus ? (
              <p role="status" className="text-sm text-muted-foreground">
                {app.deletionStatus === "failed" ? "Application deletion failed. Retry deletion below." : "Deleting this application."} New environments and plan changes are blocked.
              </p>
            ) : null}
            {app.environments.length === 0 ? (
              <EmptyState title="No environments">
                This application has no environments and cannot deploy until one is created.
              </EmptyState>
            ) : (
              <Table aria-label="Environments">
                <TableHeader>
                  <TableRow>
                    <TableHead>Environment</TableHead>
                    <TableHead>Kind</TableHead>
                    <TableHead>Plan</TableHead>
                    <TableHead>Database</TableHead>
                    <TableHead>SSH access</TableHead>
                    <TableHead>Latest deployment</TableHead>
                    <TableHead className="text-right">Actions</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {app.environments.map((env) => (
                    <EnvironmentRow
                      key={`${app.id}:${env.id}`}
                      app={app}
                      env={env}
                      busy={busy}
                      canManage={canManage}
                      mutationsBlocked={!!app.deletionStatus}
                      catalog={catalog}
                      onRename={(name) => act(() => updateEnvironment(app.id, env.id, { name }))}
                      onSetPlan={(plan) => act(() => updateEnvironment(app.id, env.id, { plan }))}
                      onSaveSsh={async (sshAccess) => {
                        await updateEnvironment(app.id, env.id, { sshAccess });
                        if (!abortRef.current?.signal.aborted) await load();
                      }}
                      onReload={() => void load()}
                      onSetDefault={() => act(() => updateApplication(app.id, { defaultEnvironmentId: env.id }))}
                      onDelete={(confirmation) => act(() => deleteEnvironment(app.id, env.id, confirmation))}
                    />
                  ))}
                </TableBody>
              </Table>
            )}
            {canManage && !app.deletionStatus ? (
              <AddEnvironmentForm
                app={app}
                busy={busy}
                startOpen={searchParams.get("addEnvironment") === "1"}
                onAdded={() => setSearchParams({}, { replace: true })}
                onCreate={(body) => act(() => createEnvironment(app.id, body))}
              />
            ) : null}
          </CardContent>
        </Card>

        <Card>
          <CardHeader><CardTitle>Delete application</CardTitle></CardHeader>
          <CardContent className="space-y-3 text-sm">
            <p className="text-muted-foreground">
              This permanently deletes {app.name} and all {app.environmentCount} environments, including previews.
              Durable storage volumes are retained in Hetzner and continue to incur charges until you delete them there.
            </p>
            {app.environments.length > 0 ? (
              <p className="text-xs text-muted-foreground">
                Affected environments: {app.environments.map((env) => `${env.name} (${env.kind === "preview" ? "preview" : "persistent"})`).join(", ")}.
              </p>
            ) : null}
            {canManage ? (
              <DeleteAppBlock
                app={app}
                busy={busy}
                onDelete={(confirmation) => act(() => deleteApplication(app.id, confirmation), () => navigate("/apps"))}
              />
            ) : null}
          </CardContent>
        </Card>
      </div>
    </PageScaffold>
  );
}

function RenameApp({
  app,
  busy,
  onSave,
}: {
  app: Application;
  busy: boolean;
  onSave: (name: string) => void;
}) {
  const [name, setName] = useState(app.name);
  return (
    <div className="flex max-w-md flex-wrap items-end gap-2">
      <div className="grid gap-2">
        <Label htmlFor="app-display-name">Display name</Label>
        <Input id="app-display-name" value={name} onChange={(e) => setName(e.target.value)} />
      </div>
      <Button
        size="sm"
        variant="outline"
        disabled={busy || !name.trim() || name.trim() === app.name}
        onClick={() => onSave(name.trim())}
      >
        Save name
      </Button>
    </div>
  );
}

function EnvironmentRow({
  app,
  env,
  busy,
  canManage,
  mutationsBlocked,
  catalog,
  onRename,
  onSetPlan,
  onSaveSsh,
  onReload,
  onSetDefault,
  onDelete,
}: {
  app: Application;
  env: ApplicationEnvironment;
  busy: boolean;
  canManage: boolean;
  mutationsBlocked: boolean;
  catalog: { plans: ProviderPlan[]; loading: boolean; error: string | null };
  onRename: (name: string) => Promise<boolean>;
  onSetPlan: (plan: string) => Promise<boolean>;
  onSaveSsh: (sshAccess: SshAccessRequest & { expectedRevision: number }) => Promise<void>;
  onReload: () => void;
  onSetDefault: () => Promise<boolean>;
  onDelete: (confirmation: string) => Promise<boolean>;
}) {
  const [renaming, setRenaming] = useState(false);
  const [name, setName] = useState(env.name);
  const [editingPlan, setEditingPlan] = useState(false);
  const [plan, setPlan] = useState(env.plan);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [confirmation, setConfirmation] = useState("");
  const isDefault = app.defaultEnvironmentId === env.id;
  const canEdit = canManage && !mutationsBlocked && !env.deletionStatus;
  const canDelete = canManage && !mutationsBlocked && (!env.deletionStatus || env.deletionStatus === "failed");
  return (
    <TableRow id={`environment-${env.id}`}>
      <TableCell data-label="Environment">
        <div className="space-y-1">
          <Link
            className="font-medium text-primary hover:underline"
            to={`/apps/${encodeURIComponent(app.id)}/environments/${encodeURIComponent(env.id)}/overview`}
          >
            {env.name}
          </Link>
          {isDefault ? <span className="ml-2 rounded bg-muted px-1.5 py-0.5 text-xs">Dashboard default</span> : null}
          {env.deletionStatus ? <span className="ml-2"><StatusBadge status={env.deletionStatus === "failed" ? "Deletion failed" : "Deleting"} /></span> : null}
          {env.deletionError ? <p className="text-xs text-destructive">{env.deletionError}</p> : null}
          {renaming ? (
            <div className="flex max-w-64 flex-wrap items-center gap-2 pt-1">
              <Input
                aria-label={`New name for ${env.name}`}
                value={name}
                onChange={(e) => setName(e.target.value)}
                pattern="[A-Za-z0-9][A-Za-z0-9_-]*"
              />
              <Button size="sm" disabled={busy || !name.trim()} onClick={() => void onRename(name.trim()).then((ok) => { if (ok) setRenaming(false); })}>
                Save
              </Button>
              <Button size="sm" variant="outline" onClick={() => { setRenaming(false); setName(env.name); }}>
                Cancel
              </Button>
            </div>
          ) : null}
        </div>
      </TableCell>
      <TableCell data-label="Kind">
        <div>
          <span>{env.kind === "preview" ? "Preview" : "Persistent"}</span>
          {env.sourceBranch ? (
            <p className="text-xs text-muted-foreground">Branch: {env.sourceBranch}</p>
          ) : null}
        </div>
      </TableCell>
      <TableCell data-label="Plan">
        <div className="space-y-2">
          <span>{env.plan}</span>
          {env.placementLocked ? (
            <p className="text-xs text-muted-foreground">Placement locked</p>
          ) : null}
          {canEdit && !env.placementLocked && !editingPlan ? (
            <Button size="sm" variant="outline" onClick={() => { setPlan(env.plan); setEditingPlan(true); }}>
              Change plan
            </Button>
          ) : null}
          {canEdit && !env.placementLocked && editingPlan ? (
            <div className="flex max-w-56 flex-wrap items-center gap-2 pt-1">
              {catalog.plans.length > 0 ? (
                <select
                  aria-label={`Plan for ${env.name}`}
                  className="h-10 w-full min-w-0 max-w-full rounded-md border bg-background px-3 text-sm"
                  disabled={catalog.loading}
                  value={plan}
                  onChange={(e) => setPlan(e.target.value)}
                >
                  <option value="">Select a plan</option>
                  {catalog.plans.map((option) => (
                    <option key={option.id} value={option.name}>
                      {option.name}
                    </option>
                  ))}
                </select>
              ) : (
                <p className="text-xs text-muted-foreground">
                  {catalog.loading
                    ? "Loading plans…"
                    : catalog.error
                      ? `Plans unavailable: ${catalog.error}`
                      : "No enabled plans in the catalog to select."}
                </p>
              )}
              <Button size="sm" disabled={busy || !plan.trim() || plan.trim() === env.plan} onClick={() => void onSetPlan(plan.trim()).then((ok) => { if (ok) setEditingPlan(false); })}>
                Save
              </Button>
              <Button size="sm" variant="outline" onClick={() => { setEditingPlan(false); setPlan(env.plan); }}>
                Cancel
              </Button>
            </div>
          ) : null}
        </div>
      </TableCell>
      <TableCell data-label="Database">{env.database}</TableCell>
      <TableCell data-label="SSH access">
        <EnvironmentSshAccessPanel env={env} plans={catalog.plans} canEdit={canEdit} onSave={onSaveSsh} onReload={onReload} />
      </TableCell>
      <TableCell data-label="Latest deployment">
        {env.latestDeployment ? (
          <span className="inline-flex flex-wrap items-center gap-2">
            <span className="whitespace-nowrap"><StatusBadge status={env.latestDeployment.status} /></span>
            <span className="text-xs text-muted-foreground">
              {env.latestDeployment.completedAt ? formatAge(env.latestDeployment.completedAt) : "In progress"}
            </span>
          </span>
        ) : (
          <span className="text-sm text-muted-foreground">No deployments yet</span>
        )}
      </TableCell>
      <TableCell data-label="Actions">
        {confirmingDelete ? (
          <div className="flex flex-col items-end gap-2">
            <p className="text-xs text-muted-foreground">
              Delete {app.name} / {env.name}? Type the environment name to confirm. Only {env.name} is affected.
            </p>
            <Input
              aria-label={`Confirm deletion of ${env.name}`}
              value={confirmation}
              onChange={(e) => setConfirmation(e.target.value)}
              autoComplete="off"
            />
            <div className="flex gap-2">
              <Button size="sm" variant="outline" onClick={() => { setConfirmingDelete(false); setConfirmation(""); }}>
                Cancel
              </Button>
              <Button
                size="sm"
                variant="destructive"
                disabled={busy || confirmation !== env.name}
                onClick={() => void onDelete(confirmation).then((ok) => { if (ok) { setConfirmingDelete(false); setConfirmation(""); } })}
              >
                Delete environment
              </Button>
            </div>
          </div>
        ) : canManage && !mutationsBlocked ? (
          <div className="flex flex-wrap justify-end gap-2">
            {canEdit ? <Button size="sm" variant="outline" disabled={busy} onClick={() => setRenaming((r) => !r)}>Rename</Button> : null}
            {canEdit && !isDefault ? (
              <Button size="sm" variant="outline" disabled={busy} onClick={() => void onSetDefault()}>Set as default</Button>
            ) : null}
            <Button
              size="sm"
              variant="destructive"
              disabled={busy || !canDelete}
              onClick={() => setConfirmingDelete(true)}
            >
              {env.deletionStatus === "failed" ? "Retry deletion" : "Delete"}
            </Button>
          </div>
        ) : (
          <span className="text-xs text-muted-foreground">Read-only</span>
        )}
      </TableCell>
    </TableRow>
  );
}

function AddEnvironmentForm({
  app,
  busy,
  startOpen,
  onAdded,
  onCreate,
}: {
  app: Application;
  busy: boolean;
  startOpen: boolean;
  onAdded: () => void;
  onCreate: (body: { name: string; plan: string; database: string; sshAccess: SshAccessRequest }) => Promise<boolean>;
}) {
  const [open, setOpen] = useState(startOpen);
  const [name, setName] = useState("");
  const [plan, setPlan] = useState("");
  const [database, setDatabase] = useState("sqlite");
  const [sshDraft, setSshDraft] = useState<SshAccessDraft>(defaultSshAccessDraft);
  const [sshError, setSshError] = useState<string | null>(null);
  const catalog = usePlanCatalog(open ? app.organizationId : undefined);
  const selectedPlan = catalog.plans.find(item => item.name === plan);
  if (!open) {
    return (
      <Button variant="outline" onClick={() => setOpen(true)}>
        Add environment
      </Button>
    );
  }
  return (
    <form
      className="grid min-w-0 gap-3 rounded-md border p-3"
      onSubmit={(e) => {
        e.preventDefault();
        if (!name.trim() || catalog.loading || catalog.error || !selectedPlan) return;
        const ssh = buildSshAccessRequest(sshDraft, selectedPlan.providerAccountId);
        if (!ssh.request) { setSshError(ssh.error); return; }
        setSshError(null);
        void onCreate({ name: name.trim(), plan: plan.trim(), database, sshAccess: ssh.request }).then(ok => {
          if (ok) { setOpen(false); setName(""); setPlan(""); setSshDraft(defaultSshAccessDraft); onAdded(); }
        });
      }}
    >
      <p className="text-sm font-medium">Add environment to {app.name}</p>
      <div className="grid min-w-0 gap-2 sm:grid-cols-3 [&>*]:min-w-0">
        <div className="grid gap-2">
          <Label htmlFor="new-env-name">Name</Label>
          <Input
            id="new-env-name"
            required
            maxLength={63}
            pattern="[A-Za-z0-9][A-Za-z0-9_-]*"
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
        </div>
        <div className="grid gap-2">
          <Label htmlFor="new-env-plan">Plan</Label>
          {catalog.plans.length > 0 ? (
            <select
              id="new-env-plan"
              className="h-10 w-full min-w-0 max-w-full rounded-md border bg-background px-3 text-sm"
              required
              disabled={catalog.loading}
              value={plan}
              onChange={(e) => setPlan(e.target.value)}
            >
              <option value="">{catalog.loading ? "Loading plans…" : "Select a plan"}</option>
              {catalog.plans.map((option) => (
                <option key={option.id} value={option.name}>
                  {option.name} — {planDescription(option)}
                </option>
              ))}
            </select>
          ) : (
            <p className="text-xs text-muted-foreground">
              {catalog.loading
                ? "Loading plans…"
                : catalog.error
                  ? `Plans unavailable: ${catalog.error}`
                  : "No enabled plans in catalog. Configure a provider and plan first."}
            </p>
          )}
          {catalog.error ? (
            <p role="alert" className="text-xs text-destructive">{catalog.error}</p>
          ) : null}
        </div>
        <div className="grid gap-2">
          <Label htmlFor="new-env-db">Database</Label>
          <Select value={database} onValueChange={setDatabase}>
            <SelectTrigger id="new-env-db"><SelectValue /></SelectTrigger>
            <SelectContent>
              <SelectItem value="sqlite">SQLite</SelectItem>
              <SelectItem value="postgres">Postgres</SelectItem>
            </SelectContent>
          </Select>
        </div>
      </div>
      {selectedPlan ? (
        <SshAccessFields
          idPrefix="new-env"
          organizationId={app.organizationId}
          account={{ id: selectedPlan.providerAccountId, name: selectedPlan.providerName, providerType: selectedPlan.providerType }}
          draft={sshDraft}
          onChange={setSshDraft}
          disabled={busy}
        />
      ) : null}
      {sshError ? <p role="alert" className="text-xs text-destructive">{sshError}</p> : null}
      <div className="flex gap-2">
        <Button type="submit" size="sm" disabled={busy || !name.trim() || catalog.loading || !!catalog.error || !catalog.plans.some(item => item.name === plan)}>Create environment</Button>
        <Button type="button" size="sm" variant="outline" onClick={() => setOpen(false)}>Cancel</Button>
      </div>
    </form>
  );
}

function DeleteAppBlock({
  app,
  busy,
  onDelete,
}: {
  app: Application;
  busy: boolean;
  onDelete: (confirmation: string) => void;
}) {
  const [confirmation, setConfirmation] = useState("");
  const blocked = !!app.deletionStatus && app.deletionStatus !== "failed";
  return (
    <div className="grid max-w-md gap-2">
      <Label htmlFor="delete-app-confirmation">Type {app.slug} to confirm</Label>
      <Input
        id="delete-app-confirmation"
        value={confirmation}
        onChange={(e) => setConfirmation(e.target.value)}
        autoComplete="off"
        disabled={busy || blocked}
      />
      <Button
        variant="destructive"
        disabled={busy || blocked || confirmation !== app.slug}
        onClick={() => onDelete(confirmation)}
      >
        {blocked ? "Deleting…" : "Permanently delete application"}
      </Button>
    </div>
  );
}
