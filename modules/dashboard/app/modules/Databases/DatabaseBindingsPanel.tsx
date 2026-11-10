import { useCallback, useEffect, useState, type FormEvent } from "react";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { listApps, listAppServices } from "../../api/appsApi";
import { bindDatabase, unbindDatabase, listDatabaseBindings, type DatabaseBinding } from "../../api/databasesApi";
import type { AppResponse, AppServiceInventoryResponse } from "../../models/controlPlane";
import { selectClass } from "./BackupPolicyForm";
import { errorMessage } from "./DatabasesModule";
export function DatabaseBindingsPanel({ databaseId, organizationId, canManage }: { databaseId: string; organizationId: string; canManage: boolean }) {
  const [apps, setApps] = useState<AppResponse[]>([]);
  const [bindings, setBindings] = useState<DatabaseBinding[]>([]);
  const [appId, setAppId] = useState("");
  const [services, setServices] = useState<AppServiceInventoryResponse[]>([]);
  const [selected, setSelected] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { const c = new AbortController(); setApps([]); setBindings([]); setAppId(""); setError(null); Promise.all([listApps(organizationId, c.signal), listDatabaseBindings(databaseId, c.signal)]).then(([a, b]) => { if (!c.signal.aborted) { setApps(a); setBindings(b); } }).catch(e => { if (!c.signal.aborted) setError(errorMessage(e)); }); return () => c.abort(); }, [databaseId, organizationId]);
  useEffect(() => { const c = new AbortController(); setServices([]); setSelected([]); if (appId) listAppServices(appId, c.signal).then(s => { if (!c.signal.aborted) setServices(s); }).catch(e => { if (!c.signal.aborted) setError(errorMessage(e)); }); return () => c.abort(); }, [appId]);
  const submit = useCallback(async (e: FormEvent) => { e.preventDefault(); setBusy(true); setError(null); try { await bindDatabase(databaseId, { appId, serviceNames: selected }); setBindings(await listDatabaseBindings(databaseId)); setSelected([]); } catch (e) { setError(errorMessage(e)); } finally { setBusy(false); } }, [appId, databaseId, selected]);
  return <section className="mt-4 space-y-3"><h3 className="font-medium">Application connections</h3>{error && <p role="alert" className="text-destructive">{error}</p>}{bindings.map(b => <p key={b.id} className="text-sm">{apps.find(a => a.id === b.appId)?.name ?? b.appId} / {b.serviceName} · {b.status}{canManage && ["applied", "pending", "revoking", "failed"].includes(b.status) && <Button className="ml-2" variant="outline" size="sm" disabled={busy} onClick={() => { if (window.confirm(`Disconnect ${b.serviceName}? Any partially configured connection will be revoked. Active database operations must finish first.`)) { setBusy(true); void unbindDatabase(databaseId, b.id).then(() => listDatabaseBindings(databaseId)).then(setBindings).catch(e => setError(errorMessage(e))).finally(() => setBusy(false)); } }}>{b.status === "applied" ? "Disconnect" : "Reconcile and disconnect"}</Button>}</p>)}{!bindings.length && <p className="text-sm text-muted-foreground">No application services are connected.</p>}{canManage && <form className="space-y-3" onSubmit={e => void submit(e)}><Label htmlFor="binding-app">Application</Label><select id="binding-app" className={selectClass} value={appId} onChange={e => setAppId(e.target.value)} required><option value="">Select application</option>{apps.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}</select>{services.map(s => <label key={s.name} className="flex items-center gap-2"><input type="checkbox" checked={selected.includes(s.name)} onChange={e => setSelected(value => e.target.checked ? [...value, s.name] : value.filter(n => n !== s.name))} />{s.name}</label>)}<p className="text-sm text-muted-foreground">Only selected services receive credentials. Connecting changes their database on the next deployment; existing data is not moved. Use migration when moving live data.</p><Button variant="outline" disabled={busy || !selected.length}>Connect selected services</Button></form>}</section>;
}
