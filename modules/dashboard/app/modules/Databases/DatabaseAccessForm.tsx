import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { createDatabaseNetwork, deleteDatabaseNetwork, listDatabaseNetworks, listDatabases, type Database, type DatabaseCapabilities, type DatabaseNetwork } from "../../api/databasesApi";
import { usePlanCatalog, planDescription } from "../Providers/usePlanCatalog";
import { selectClass } from "./BackupPolicyForm";
import { errorMessage } from "./DatabasesModule";

type AccessInput = { mode: string; allowedCidrs: string[]; networkId?: string };
export function DatabaseAccessForm({ database, capabilities, organizationId, busy, onSave }: { database: Database; capabilities: DatabaseCapabilities; organizationId: string; busy: boolean; onSave: (input: AccessInput) => void }) {
  const [mode, setMode] = useState(database.accessMode);
  const [networkId, setNetworkId] = useState(database.networkId ?? "");
  const [networkUse, setNetworkUse] = useState<string[]>([]);
  const [inventoryLoaded, setInventoryLoaded] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleteNotice, setDeleteNotice] = useState<string | null>(null);
  const [networks, setNetworks] = useState<DatabaseNetwork[]>([]);
  const [networkError, setNetworkError] = useState<string | null>(null);
  const [createOpen, setCreateOpen] = useState(false);
  const [creating, setCreating] = useState(false);
  const scope = useRef(organizationId); scope.current = organizationId;
  const { plans, error: planError } = usePlanCatalog(organizationId);
  const supported = capabilities.accessModes.includes("private");
  const refresh = useCallback(async (signal?: AbortSignal) => {
    if (!supported) return;
    try { const [value, inventory] = await Promise.all([listDatabaseNetworks(organizationId, signal), listDatabases(organizationId, signal)]); if (!signal?.aborted && scope.current === organizationId) { setNetworks(value); setNetworkUse(inventory.filter(d => d.status !== "deleted").flatMap(d => d.networkId ? [d.networkId] : [])); setInventoryLoaded(true); setNetworkError(null); } }
    catch (e) { if (!signal?.aborted && scope.current === organizationId) setNetworkError(errorMessage(e)); }
  }, [organizationId, supported]);
  useEffect(() => { const c = new AbortController(); setNetworks([]); setNetworkUse([]); setInventoryLoaded(false); setDeleteNotice(null); setCreateOpen(false); setNetworkId(database.networkId ?? ""); setMode(database.accessMode); void refresh(c.signal); const timer = setInterval(() => void refresh(c.signal), 10000); return () => { c.abort(); clearInterval(timer); }; }, [database.id, organizationId, refresh]);
  const compatible = networks.filter(n => n.providerAccountId === capabilities.providerAccountId && (!capabilities.networkZone || n.networkZone === capabilities.networkZone));
  const ready = compatible.filter(n => n.status === "ready");
  const selectedReady = ready.some(n => n.id === networkId);
  const availablePlans = plans.filter(p => p.providerType === "hetzner" && p.providerAccountId === capabilities.providerAccountId && (!capabilities.region || p.region === capabilities.region));
  const submit = (e: FormEvent<HTMLFormElement>) => { e.preventDefault(); const fields = new FormData(e.currentTarget); if (mode === "private" && !selectedReady) return; onSave({ mode, allowedCidrs: mode === "public" ? String(fields.get("cidrs") ?? "").split(/[\s,]+/).filter(Boolean) : [], ...(mode === "private" ? { networkId } : {}) }); };
  const create = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault(); const fields = new FormData(e.currentTarget); const requestOrganization = organizationId;
    setCreating(true); setNetworkError(null);
    try { await createDatabaseNetwork({ organizationId, name: String(fields.get("name")), plan: String(fields.get("plan")), ipRange: String(fields.get("ipRange")), subnetRange: String(fields.get("subnetRange")) }); if (scope.current === requestOrganization) { setCreateOpen(false); await refresh(); } }
    catch (e) { if (scope.current === requestOrganization) setNetworkError(errorMessage(e)); }
    finally { if (scope.current === requestOrganization) setCreating(false); }
  };
  const removeNetwork = async (network: DatabaseNetwork) => {
    if (!inventoryLoaded || networkUse.includes(network.id) || database.networkId === network.id || !["ready", "failed"].includes(network.status)) return;
    if (!window.confirm(`Delete provider network ${network.name}? It must be unused. This removes the provider network after the worker verifies cleanup.`)) return;
    const requestOrganization = organizationId;
    setDeleting(true); setNetworkError(null); setDeleteNotice(null);
    try { await deleteDatabaseNetwork(network.id); if (scope.current === requestOrganization) { setDeleteNotice("Deletion requested. The observed network status will update when the worker processes it."); await refresh(); } }
    catch (e) { if (scope.current === requestOrganization) setNetworkError(errorMessage(e)); }
    finally { if (scope.current === requestOrganization) setDeleting(false); }
  };
  return <div className="mt-4 space-y-3"><form className="space-y-3" onSubmit={submit}>
    <Label htmlFor="access-mode">Access mode</Label><select id="access-mode" name="mode" className={selectClass} value={mode} onChange={e => setMode(e.target.value)}>{capabilities.accessModes.map(value => <option value={value} key={value}>{value === "internal" ? "Internal only" : value === "private" ? "Provider private network" : "Public (TLS)"}</option>)}</select>
    {mode === "public" && <><Label htmlFor="cidrs">Allowed public IPs / CIDRs</Label><Input id="cidrs" name="cidrs" defaultValue={JSON.parse(database.allowedCidrsJson || "[]").join(", ")} /><p className="text-sm text-muted-foreground">An empty public allowlist denies all clients.</p></>}
    {mode === "private" && <><Label htmlFor="database-network">Provider network</Label><select id="database-network" className={selectClass} value={networkId} onChange={e => setNetworkId(e.target.value)} required><option value="">Select a ready compatible network</option>{ready.map(n => <option key={n.id} value={n.id}>{n.name} · {n.subnetRange}</option>)}</select><p className="text-sm text-muted-foreground">Only ready networks in this database's provider account and compatible network zone are selectable. Managed internal connections remain separate.</p>{compatible.map(n => <div key={n.id} className="flex flex-wrap items-center gap-2 text-sm"><p>{n.name} · {n.status} · {n.ipRange}</p><Button type="button" size="sm" variant="outline" disabled={busy || deleting || !inventoryLoaded || networkUse.includes(n.id) || database.networkId === n.id || !["ready", "failed"].includes(n.status)} onClick={() => void removeNetwork(n)}>Delete network {n.name}</Button>{(networkUse.includes(n.id) || database.networkId === n.id) && <span className="text-muted-foreground">In use by a database</span>}</div>)}{deleteNotice && <p role="status" className="text-sm">{deleteNotice}</p>}{database.privateIp && <p className="text-sm">Observed private address: {database.privateIp}</p>}<Button type="button" variant="outline" disabled={busy || !availablePlans.length} onClick={() => setCreateOpen(true)}>Create provider network</Button>{!availablePlans.length && <p className="text-sm text-muted-foreground">A Hetzner plan in this database's provider account and region is required to create a network here.</p>}</>}
    {networkError && <p role="alert" className="text-destructive">{networkError}</p>}
    <p className="text-sm text-muted-foreground">Unsupported network modes are not offered. Changes are applied asynchronously and are not active until verified.</p><Button variant="outline" disabled={busy || !capabilities.accessModes.includes(mode) || (mode === "private" && !selectedReady)}>Apply access rules</Button>
  </form><Dialog open={createOpen} onOpenChange={open => { if (!creating) setCreateOpen(open); }}><DialogContent><DialogHeader><DialogTitle>Create provider network</DialogTitle><DialogDescription>Choose a Hetzner plan to identify the provider account and region. Review provider networking charges before creating.</DialogDescription></DialogHeader><form className="space-y-3" onSubmit={e => void create(e)}>
    <Label htmlFor="network-name">Network name</Label><Input id="network-name" name="name" required />
    <Label htmlFor="network-plan">Network plan</Label><select id="network-plan" name="plan" className={selectClass} required><option value="">Select a Hetzner plan</option>{availablePlans.map(p => <option key={p.id} value={p.name}>{p.name} · {planDescription(p)}</option>)}</select>
    <Label htmlFor="network-range">Network IP range</Label><Input id="network-range" name="ipRange" defaultValue="10.72.0.0/16" required />
    <Label htmlFor="subnet-range">Subnet range</Label><Input id="subnet-range" name="subnetRange" defaultValue="10.72.1.0/24" required />
    {(networkError || planError) && <p role="alert" className="text-destructive">{networkError || planError}</p>}<Button type="submit" disabled={creating}>Create network</Button>
  </form></DialogContent></Dialog></div>;
}
