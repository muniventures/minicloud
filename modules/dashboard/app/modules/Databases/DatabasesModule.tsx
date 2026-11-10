import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import * as api from "../../api/databasesApi";
import { listApplications } from "../../api/applicationsApi";
import type { ApplicationEnvironment } from "../../models/application";
import { useAuth } from "../../core/auth/AuthProvider";
import { PageScaffold } from "../../core/components/PageScaffold";
import { usePlanCatalog, planDescription } from "../Providers/usePlanCatalog";
import { BackupPolicyFields, defaultPolicy, selectClass } from "./BackupPolicyForm";
export const errorMessage = (e: unknown) => e instanceof Error ? e.message : "The database request failed.";
export function DatabasesModule() {
  const { selectedOrganization } = useAuth();
  const organizationId = selectedOrganization?.id;
  const current = useRef(organizationId); current.current = organizationId;
  const canManage = ["owner", "admin", "developer"].includes(selectedOrganization?.role?.toLowerCase() ?? "");
  const { plans, error: planError } = usePlanCatalog(organizationId);
  const [databases, setDatabases] = useState<api.Database[]>([]);
  const [servers, setServers] = useState<api.DatabaseServer[]>([]);
  const [repositories, setRepositories] = useState<api.Repository[]>([]);
  const [apps, setApps] = useState<ApplicationEnvironment[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [editor, setEditor] = useState<"server" | "database" | "enroll" | "repository" | null>(null);
  const [repositoryToDelete, setRepositoryToDelete] = useState<api.Repository | null>(null);
  const [repositoryConfirmation, setRepositoryConfirmation] = useState("");
  const refresh = useCallback(async (signal?: AbortSignal) => {
    if (!organizationId) { setLoading(false); return; }
    try {
      const [d, s, r, a] = await Promise.all([api.listDatabases(organizationId, signal), api.listDatabaseServers(organizationId, signal), api.listRepositories(organizationId, signal), listApplications(organizationId, signal)]);
      if (!signal?.aborted && current.current === organizationId) { setDatabases(d); setServers(s); setRepositories(r); setApps(a.flatMap(application => application.environments.map(environment => ({ ...environment, name: `${application.name} / ${environment.name}` })))); setError(null); }
    } catch (e) { if (!signal?.aborted && current.current === organizationId) setError(errorMessage(e)); }
    finally { if (!signal?.aborted && current.current === organizationId) setLoading(false); }
  }, [organizationId]);
  useEffect(() => { const c = new AbortController(); setDatabases([]); setServers([]); setRepositories([]); setApps([]); setEditor(null); setRepositoryToDelete(null); setRepositoryConfirmation(""); setLoading(true); void refresh(c.signal); const timer = setInterval(() => void refresh(c.signal), 10000); return () => { c.abort(); clearInterval(timer); }; }, [refresh]);
  const save = useCallback(async (action: () => Promise<unknown>) => { const org = organizationId; setBusy(true); setError(null); try { await action(); if (current.current === org) { setEditor(null); await refresh(); } } catch (e) { if (current.current === org) setError(errorMessage(e)); } finally { setBusy(false); } }, [organizationId, refresh]);
  return <PageScaffold title="Databases" breadcrumbs={[{ label: "Databases" }]} actions={<><Button variant="outline" disabled={busy} onClick={() => void refresh()}>Refresh</Button>{canManage && <Button onClick={() => setEditor("database")}>Create database</Button>}</>}>
    <p className="text-sm text-muted-foreground">Manage application-hosted databases, shared servers and dedicated servers. Backups remain available in every placement.</p>
    {(error || planError) && <p role="alert" className="text-destructive">{error || planError}</p>}{loading && <p role="status">Loading databases…</p>}
    {!loading && !databases.length && <p>No managed databases yet.</p>}
    {databases.map(d => <Card key={d.id}><CardHeader><CardTitle><Link className="underline" to={`/databases/${encodeURIComponent(d.id)}`}>{d.name}</Link></CardTitle></CardHeader><CardContent><p>{d.engine} {d.engineVersion} · {d.placement} · {d.status}</p>{d.status === "deleted" && <p className="text-sm text-muted-foreground">Source deleted · Backups retained</p>}{d.appId && <Link className="text-sm underline" to={`/apps/${encodeURIComponent(d.appId)}`}>View application</Link>}</CardContent></Card>)}
    {canManage && <Button variant="outline" onClick={() => setEditor("enroll")}>Add existing application database</Button>}
    <div className="flex flex-wrap items-center justify-between gap-3"><h2 className="text-xl font-medium">Database servers</h2>{canManage && <Button variant="outline" onClick={() => setEditor("server")}>Create database server</Button>}</div>
    {servers.map(s => <Card key={s.id}><CardHeader><CardTitle>{s.name}</CardTitle></CardHeader><CardContent>{s.mode} · {s.engine} {s.engineVersion} · {s.status}<p className="text-sm text-muted-foreground">{databases.filter(d => d.serverId === s.id && d.status !== "deleted").length} databases</p>{canManage && <Button variant="outline" disabled={busy || databases.some(d => d.serverId === s.id && d.status !== "deleted")} onClick={() => { if (window.confirm(`Delete server ${s.name}? This removes its VPS. Retained backups remain subject to their retention policy.`)) void save(() => api.deleteDatabaseServer(s.id)); }}>Delete empty server</Button>}</CardContent></Card>)}
    {!loading && !servers.length && <p className="text-sm text-muted-foreground">Create a shared server for several databases or a dedicated server for one database.</p>}
    <h2 className="text-xl font-medium">Backup repositories</h2>{repositories.map(r => <div key={r.id} className="flex flex-wrap items-center gap-3"><p>{r.name}{r.deletionStatus && <span> · deletion {r.deletionStatus}</span>}</p>{canManage && <Button variant="outline" disabled={busy || ["queued", "preparing", "deleting"].includes(r.deletionStatus ?? "")} aria-label={`Delete unused repository ${r.name}`} onClick={() => { setRepositoryConfirmation(""); setRepositoryToDelete(r); }}>Delete unused repository</Button>}</div>)}{canManage && <Button variant="outline" onClick={() => setEditor("repository")}>Create backup repository</Button>}
    <Dialog open={!!repositoryToDelete} onOpenChange={open => { if (!open && !busy) setRepositoryToDelete(null); }}><DialogContent><DialogHeader><DialogTitle>Delete backup repository</DialogTitle><DialogDescription>This queues removal of the repository VPS and storage volume. Disable its backup policies and permanently remove retained backups first. Type {repositoryToDelete?.name} to confirm.</DialogDescription></DialogHeader>
      {error && <p role="alert" className="text-destructive">{error}</p>}
      <form className="space-y-4" onSubmit={(event: FormEvent<HTMLFormElement>) => { event.preventDefault(); const repository = repositoryToDelete; const org = organizationId; if (!repository || repositoryConfirmation !== repository.name) return; void save(async () => { await api.deleteRepository(repository.id, repositoryConfirmation); if (current.current === org) setRepositoryToDelete(null); }); }}><Label htmlFor="repository-delete-confirmation">Confirm repository name</Label><Input id="repository-delete-confirmation" value={repositoryConfirmation} onChange={event => setRepositoryConfirmation(event.target.value)} autoComplete="off" /><Button type="submit" variant="destructive" disabled={busy || !repositoryToDelete || repositoryConfirmation !== repositoryToDelete.name}>Delete repository</Button></form>
    </DialogContent></Dialog>
    <Dialog open={!!editor} onOpenChange={open => { if (!open && !busy) setEditor(null); }}><DialogContent className="max-h-[90vh] overflow-y-auto"><DialogHeader><DialogTitle>{editor === "repository" ? "Create backup repository" : editor === "server" ? "Create database server" : editor === "enroll" ? "Add application database" : "Create database"}</DialogTitle><DialogDescription>{editor === "enroll" ? "Register the current database without moving data or changing the application connection." : "Review your selected plan and repository costs before creating resources."}</DialogDescription></DialogHeader>
      {error && <p role="alert" className="text-destructive">{error}</p>}
      {organizationId && (editor === "server" || editor === "repository") && <form className="space-y-4" onSubmit={(e: FormEvent<HTMLFormElement>) => { e.preventDefault(); const f = new FormData(e.currentTarget); void save(() => editor === "repository" ? api.createRepository({ organizationId, name: String(f.get("name")), plan: String(f.get("plan")), sizeGb: Number(f.get("sizeGb")) }) : api.createDatabaseServer({ organizationId, name: String(f.get("name")), mode: String(f.get("mode")), plan: String(f.get("plan")) })); }}><Label htmlFor="server-name">{editor === "repository" ? "Repository name" : "Server name"}</Label><Input id="server-name" name="name" required />{editor === "repository" && <><Label htmlFor="repository-size">Storage (GB)</Label><Input id="repository-size" name="sizeGb" type="number" min={10} max={10000} defaultValue={20} required /></>}{editor === "server" && <><Label htmlFor="server-mode">Hosting mode</Label><select id="server-mode" name="mode" className={selectClass}><option value="shared">Shared — multiple databases</option><option value="dedicated">Dedicated — one database</option></select></>}<Label htmlFor="plan">Plan</Label><select id="plan" name="plan" required className={selectClass}><option value="">Select plan</option>{plans.filter(p => editor !== "repository" || p.providerType === "hetzner").map(p => <option key={p.id} value={p.name}>{p.name} · {planDescription(p)}</option>)}</select><p className="text-sm text-muted-foreground">{editor === "repository" ? "This creates an independent backup VPS and persistent volume billed under the selected plan." : "PostgreSQL 16. This creates a VPS billed under the selected provider plan."}</p><Button type="submit" disabled={busy || !plans.length}>{editor === "repository" ? "Create repository" : "Create server"}</Button></form>}
      {organizationId && editor && editor !== "server" && editor !== "repository" && <DatabaseForm servers={servers.filter(s => s.mode !== "dedicated" || !databases.some(d => d.serverId === s.id))} apps={apps.filter(a => ["postgres", "sqlite"].includes(a.database) && !databases.some(d => d.appId === a.id))} repositories={repositories.filter(r => !r.deletionStatus)} enroll={editor === "enroll"} busy={busy} onSave={input => save(() => api.createDatabase({ organizationId, ...input }))} />}
    </DialogContent></Dialog>
  </PageScaffold>;
}
function DatabaseForm({ servers, apps, repositories, enroll, busy, onSave }: { servers: api.DatabaseServer[]; apps: ApplicationEnvironment[]; repositories: api.Repository[]; enroll: boolean; busy: boolean; onSave: (input: { name: string; serverId?: string; appId?: string; backup?: api.BackupPolicy }) => void }) {
  const [backup, setBackup] = useState<api.BackupPolicy>(enroll ? { ...defaultPolicy, enabled: false } : defaultPolicy);
  return <form className="space-y-4" onSubmit={(e: FormEvent<HTMLFormElement>) => { e.preventDefault(); const f = new FormData(e.currentTarget); onSave({ name: String(f.get("name")), ...(enroll ? { appId: String(f.get("target")) } : { serverId: String(f.get("target")) }), backup }); }}><Label htmlFor="database-name">Database name</Label><Input id="database-name" name="name" required /><Label htmlFor="target">{enroll ? "Application" : "Database server"}</Label><select id="target" name="target" required className={selectClass}><option value="">Select {enroll ? "application" : "server"}</option>{enroll ? apps.map(a => <option key={a.id} value={a.id}>{a.name} · {a.database}</option>) : servers.filter(s => s.status === "ready").map(s => <option key={s.id} value={s.id}>{s.name} · {s.mode}</option>)}</select><BackupPolicyFields value={backup} onChange={setBackup} repositories={repositories} /><Button type="submit" disabled={busy || (backup.enabled && !backup.repositoryAppId)}>{enroll ? "Register database" : "Create database"}</Button></form>;
}
