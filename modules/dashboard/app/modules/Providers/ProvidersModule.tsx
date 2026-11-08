import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { createPlan, createProvider, deletePlan, deleteProvider, listPlans, listProviders, listProviderTypes, replaceProviderCredential, updatePlan, updateProvider, validateProvider, type PlanInput, type ProviderAccount, type ProviderPlan, type ProviderType } from "../../api/providersApi";
import { useAuth } from "../../core/auth/AuthProvider";
import { PageScaffold } from "../../core/components/PageScaffold";
import { planDescription } from "./usePlanCatalog";

const selectClass = "h-10 w-full rounded-md border bg-background px-3 text-sm";
const message = (error: unknown) => error instanceof Error ? error.message : "Unable to save changes.";
type Editor = { kind: "provider"; account?: ProviderAccount } | { kind: "credential"; account: ProviderAccount } | { kind: "plan"; account: ProviderAccount; plan?: ProviderPlan; clone?: boolean } | { kind: "delete-provider"; account: ProviderAccount } | { kind: "delete-plan"; plan: ProviderPlan };

export function ProvidersModule() {
  const { selectedOrganization } = useAuth();
  const organizationId = selectedOrganization?.id;
  const currentOrganization = useRef(organizationId);
  currentOrganization.current = organizationId;
  const canManage = ["owner", "admin"].includes(selectedOrganization?.role?.toLowerCase() ?? "");
  const [accounts, setAccounts] = useState<ProviderAccount[]>([]);
  const [plans, setPlans] = useState<ProviderPlan[]>([]);
  const [types, setTypes] = useState<ProviderType[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [busy, setBusy] = useState(false);
  const refresh = useCallback(async (signal?: AbortSignal) => {
    if (!organizationId) { setLoading(false); return; }
    setLoading(true); setError(null);
    try {
      const [nextAccounts, nextPlans, nextTypes] = await Promise.all([listProviders(organizationId, signal), listPlans(organizationId, signal), listProviderTypes(signal)]);
      if (!signal?.aborted && currentOrganization.current === organizationId) { setAccounts(nextAccounts); setPlans(nextPlans); setTypes(nextTypes); }
    } catch (e) { if (!signal?.aborted && currentOrganization.current === organizationId) setError(message(e)); }
    finally { if (!signal?.aborted && currentOrganization.current === organizationId) setLoading(false); }
  }, [organizationId]);
  useEffect(() => {
    const controller = new AbortController();
    setAccounts([]); setPlans([]); setTypes([]); setEditor(null);
    void refresh(controller.signal);
    return () => controller.abort();
  }, [refresh]);
  async function run(action: () => Promise<unknown>, close = false) {
    const requestOrganization = organizationId;
    setBusy(true); setError(null);
    try { await action(); if (currentOrganization.current === requestOrganization) { if (close) setEditor(null); await refresh(); } }
    catch (e) { if (currentOrganization.current === requestOrganization) setError(message(e)); }
    finally { setBusy(false); }
  }
  return <PageScaffold title="Providers" breadcrumbs={[{ label: "Providers" }]} actions={<>
    <Button variant="outline" disabled={loading || busy} onClick={() => void refresh()}>Refresh</Button>
    {canManage && <Button disabled={!organizationId || !types.length || busy} onClick={() => setEditor({ kind: "provider" })}>Add provider</Button>}
  </>}>
    <p className="text-sm text-muted-foreground">Connect cloud accounts, then create named plans to use in the CLI and minicloud.yml.</p>
    {error && !editor && <p role="alert" className="text-sm text-destructive">{error}</p>}
    {loading && <p role="status">Loading providers…</p>}
    {!loading && !error && !accounts.length && <Card><CardHeader><CardTitle>No providers</CardTitle></CardHeader><CardContent>Add a cloud account and a plan before creating an app.{!canManage && " An organization owner or admin can configure providers."}</CardContent></Card>}
    {accounts.map(account => <Card key={account.id}>
      <CardHeader><div className="flex flex-wrap items-start justify-between gap-3"><div><CardTitle>{account.name}</CardTitle><p className="mt-1 text-sm text-muted-foreground">{types.find(type => type.type === account.providerType)?.name ?? account.providerType} · {account.enabled ? "Enabled" : "Disabled"} · {account.planCount} plans</p></div>
        {canManage && <div className="flex flex-wrap gap-2">
          <Button size="sm" variant="outline" disabled={busy} onClick={() => setEditor({ kind: "provider", account })}>Edit provider</Button>
          <Button size="sm" variant="outline" disabled={busy} onClick={() => setEditor({ kind: "credential", account })}>Replace credential</Button>
          <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => validateProvider(account.id))}>Check connection</Button>
          <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => updateProvider(account.id, { enabled: !account.enabled, revision: account.revision }))}>{account.enabled ? "Disable provider" : "Enable provider"}</Button>
          <Button size="sm" variant="outline" disabled={busy} onClick={() => setEditor({ kind: "delete-provider", account })}>Delete provider</Button>
        </div>}
      </div></CardHeader>
      <CardContent className="space-y-4">
        <p className="text-sm">Connection: {account.validationStatus.replaceAll("_", " ")}{account.lastValidatedAt ? ` · Checked ${new Date(account.lastValidatedAt).toLocaleString()}` : " · Not checked yet"}</p>
        {account.projectId && <p className="text-sm text-muted-foreground">Project: {account.projectId}</p>}
        {plans.filter(plan => plan.providerAccountId === account.id).map(plan => <div key={plan.id} className="flex flex-wrap items-center justify-between gap-3 border-t pt-3">
          <div className="min-w-0"><p className="break-words font-medium">{plan.name}</p><p className="text-sm text-muted-foreground">{planDescription(plan)} · {plan.buildPlatform} · {plan.enabled ? "Enabled" : "Disabled"}</p>{plan.provisioningLocked && <p className="text-xs text-muted-foreground">Mapping is in use. Duplicate this plan to change its placement.</p>}</div>
          {canManage && <div className="flex flex-wrap gap-2">
            <Button size="sm" variant="outline" disabled={busy} onClick={() => setEditor({ kind: "plan", account, plan })}>Edit plan</Button>
            <Button size="sm" variant="outline" disabled={busy} onClick={() => setEditor({ kind: "plan", account, plan, clone: true })}>Duplicate plan</Button>
            <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => updatePlan(plan.id, { enabled: !plan.enabled, revision: plan.revision }))}>{plan.enabled ? "Disable plan" : "Enable plan"}</Button>
            <Button size="sm" variant="outline" disabled={busy} onClick={() => setEditor({ kind: "delete-plan", plan })}>Delete plan</Button>
          </div>}
        </div>)}
        {!plans.some(plan => plan.providerAccountId === account.id) && <p className="text-sm text-muted-foreground">No plans configured for this account.</p>}
        {canManage && <Button variant="outline" disabled={busy || !account.enabled} onClick={() => setEditor({ kind: "plan", account })}>Add plan</Button>}
      </CardContent>
    </Card>)}
    <Dialog open={!!editor} onOpenChange={open => { if (!open && !busy) { setEditor(null); setError(null); } }}>
      <DialogContent className="max-h-[90vh] overflow-y-auto">
        <DialogHeader><DialogTitle>{editor?.kind === "provider" ? editor.account ? "Edit provider" : "Add provider" : editor?.kind === "credential" ? "Replace credential" : editor?.kind === "plan" ? editor.plan && !editor.clone ? "Edit plan" : "Add plan" : editor?.kind === "delete-provider" ? "Delete provider?" : "Delete plan?"}</DialogTitle><DialogDescription>{editor?.kind === "credential" || editor?.kind === "provider" ? "Credentials are stored in the secret store and cannot be read back." : editor?.kind === "plan" ? "Reference the plan name in the CLI or minicloud.yml. Names are unique across this organization." : "Resources are not deleted by this action. Referenced providers and plans cannot be removed."}</DialogDescription></DialogHeader>
        {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
        {editor?.kind === "provider" && organizationId && <ProviderForm account={editor.account} types={types} busy={busy} onSave={input => run(() => editor.account ? updateProvider(editor.account.id, { name: input.name, revision: editor.account.revision }) : createProvider({ ...input, organizationId }), true)} />}
        {editor?.kind === "credential" && <CredentialForm busy={busy} onSave={credential => run(() => replaceProviderCredential(editor.account.id, credential, editor.account.revision), true)} />}
        {editor?.kind === "plan" && organizationId && <PlanForm key={`${editor.plan?.id ?? "new"}-${editor.clone}`} types={types} account={editor.account} plan={editor.plan} clone={editor.clone} busy={busy} onSave={input => run(() => editor.plan && !editor.clone ? updatePlan(editor.plan.id, editor.plan.provisioningLocked ? { name: input.name, revision: editor.plan.revision } : { ...input, revision: editor.plan.revision }) : createPlan({ ...input, organizationId, providerAccountId: editor.account.id }), true)} />}
        {(editor?.kind === "delete-provider" || editor?.kind === "delete-plan") && <div className="flex justify-end gap-2"><Button variant="outline" disabled={busy} onClick={() => setEditor(null)}>Cancel</Button><Button variant="destructive" disabled={busy} onClick={() => void run(() => editor.kind === "delete-provider" ? deleteProvider(editor.account.id) : deletePlan(editor.plan.id), true)}>Delete</Button></div>}
      </DialogContent>
    </Dialog>
  </PageScaffold>;
}

function ProviderForm({ account, types, busy, onSave }: { account?: ProviderAccount; types: ProviderType[]; busy: boolean; onSave: (input: { name: string; providerType: string; credential: string; projectId?: string; defaultOsId?: number }) => Promise<void> }) {
  const [name, setName] = useState(account?.name ?? "");
  const [type, setType] = useState(account?.providerType ?? types[0]?.type ?? "");
  const [credential, setCredential] = useState("");
  const [projectId, setProjectId] = useState(account?.projectId ?? "");
  const [osId, setOsId] = useState(String(account?.defaultOsId ?? types.find(item => item.type === type)?.defaultOsId ?? ""));
  async function submit(event: FormEvent) { event.preventDefault(); const value = credential; setCredential(""); await onSave({ name, providerType: type, credential: value, projectId: projectId || undefined, defaultOsId: osId ? Number(osId) : undefined }); }
  return <form className="grid gap-4" onSubmit={submit}>
    <div className="grid gap-2"><Label htmlFor="provider-name">Provider name</Label><Input id="provider-name" required maxLength={128} value={name} onChange={event => setName(event.target.value)} /></div>
    {!account && <><div className="grid gap-2"><Label htmlFor="provider-type">Provider type</Label><select className={selectClass} id="provider-type" value={type} onChange={event => { setType(event.target.value); setOsId(String(types.find(item => item.type === event.target.value)?.defaultOsId ?? "")); }}>{types.map(item => <option key={item.type} value={item.type}>{item.name}</option>)}</select></div>
      <div className="grid gap-2"><Label htmlFor="provider-credential">API token or key</Label><Input id="provider-credential" type="password" autoComplete="new-password" required value={credential} onChange={event => setCredential(event.target.value)} /></div>
      {type === "digital-ocean" && <div className="grid gap-2"><Label htmlFor="provider-project">Project ID</Label><Input id="provider-project" value={projectId} onChange={event => setProjectId(event.target.value)} /></div>}
      {type === "vultr" && <div className="grid gap-2"><Label htmlFor="provider-os">Default OS ID</Label><Input id="provider-os" type="number" min={1} value={osId} onChange={event => setOsId(event.target.value)} /></div>}
    </>}
    <Button type="submit" disabled={busy}>{busy ? "Saving…" : account ? "Save provider" : "Create provider"}</Button>
  </form>;
}

function CredentialForm({ busy, onSave }: { busy: boolean; onSave: (credential: string) => Promise<void> }) {
  const [credential, setCredential] = useState("");
  return <form className="grid gap-4" onSubmit={event => { event.preventDefault(); const value = credential; setCredential(""); void onSave(value); }}>
    <p className="text-sm text-muted-foreground">Use credentials for the same cloud account and project. A different account requires a new provider entry.</p>
    <div className="grid gap-2"><Label htmlFor="replacement-credential">New API token or key</Label><Input id="replacement-credential" required type="password" autoComplete="new-password" value={credential} onChange={event => setCredential(event.target.value)} /></div>
    <Button type="submit" disabled={busy}>{busy ? "Saving…" : "Save credential"}</Button>
  </form>;
}

function PlanForm({ account, plan, clone, types, busy, onSave }: { account: ProviderAccount; plan?: ProviderPlan; clone?: boolean; types: ProviderType[]; busy: boolean; onSave: (input: PlanInput) => Promise<void> }) {
  const [name, setName] = useState(() => plan && !clone ? plan.name : crypto.randomUUID());
  const [region, setRegion] = useState(plan?.region ?? "");
  const [sku, setSku] = useState(plan?.sku ?? "");
  const [image, setImage] = useState(plan?.image ?? types.find(type => type.type === account.providerType)?.defaultImage ?? "");
  const [platform, setPlatform] = useState(plan?.buildPlatform ?? "linux/amd64");
  const locked = !!plan?.provisioningLocked && !clone;
  function submit(event: FormEvent) { event.preventDefault(); void onSave({ name, region, sku, image: image || null, buildPlatform: platform }); }
  return <form className="grid gap-4" onSubmit={submit}>
    <div className="grid gap-2"><Label htmlFor="plan-name">Plan name</Label><Input id="plan-name" required maxLength={128} value={name} onChange={event => setName(event.target.value)} /></div>
    <p className="text-sm text-muted-foreground">Account: {account.name}</p>
    {plan && !clone && <p className="text-sm text-muted-foreground">After renaming, update the name in minicloud.yml and CLI commands. Existing app bindings are preserved.</p>}
    {locked && <p className="text-sm text-muted-foreground">This plan has been reserved for provisioning. Rename it here or duplicate it to change the mapping.</p>}
    <div className="grid gap-4 sm:grid-cols-2"><div className="grid gap-2"><Label htmlFor="plan-region">Region</Label><Input id="plan-region" required disabled={locked} value={region} onChange={event => setRegion(event.target.value)} /></div><div className="grid gap-2"><Label htmlFor="plan-sku">Provider SKU</Label><Input id="plan-sku" required disabled={locked} value={sku} onChange={event => setSku(event.target.value)} /></div></div>
    <div className="grid gap-2"><Label htmlFor="plan-image">{account.providerType === "vultr" ? "OS ID override (optional)" : "Image"}</Label><Input id="plan-image" disabled={locked} type={account.providerType === "vultr" ? "number" : "text"} min={account.providerType === "vultr" ? 1 : undefined} value={image} onChange={event => setImage(event.target.value)} /></div>
    <div className="grid gap-2"><Label htmlFor="plan-platform">Linux platform</Label><select id="plan-platform" className={selectClass} disabled={locked} value={platform} onChange={event => setPlatform(event.target.value)}><option value="linux/amd64">linux/amd64</option><option value="linux/arm64">linux/arm64</option></select></div>
    <Button type="submit" disabled={busy}>{busy ? "Saving…" : plan && !clone ? "Save plan" : "Create plan"}</Button>
  </form>;
}
