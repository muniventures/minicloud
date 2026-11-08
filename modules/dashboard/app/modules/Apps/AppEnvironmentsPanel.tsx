import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { createAppEnvironment, getApp, renameAppEnvironment } from "../../api/appsApi";
import type { AppPlan, AppResponse } from "../../models/controlPlane";
import { planOptions } from "./CreateAppModule";

export function AppEnvironmentsPanel({ app, onUpdated }: { app: AppResponse; onUpdated: () => Promise<void> }) {
  const [root, setRoot] = useState(app);
  const [name, setName] = useState("");
  const [currentName, setCurrentName] = useState(app.environmentName ?? "production");
  const [plan, setPlan] = useState<AppPlan>("p0-v-0");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    let live = true;
    setCurrentName(app.environmentName ?? "production");
    setError(null);
    setRoot(app);
    if (app.parentAppId) getApp(app.parentAppId).then(value => { if (live) setRoot(value); })
      .catch(e => { if (live) setError(e instanceof Error ? e.message : "Unable to load environments."); });
    return () => { live = false; };
  }, [app]);
  async function save(action: "create" | "rename") {
    setBusy(true); setError(null);
    try {
      if (action === "create") await createAppEnvironment(root.id, name, plan);
      else await renameAppEnvironment(app.id, currentName);
      setRoot(await getApp(root.id));
      setName("");
      await onUpdated();
    } catch (e) { setError(e instanceof Error ? e.message : "Unable to save environment."); }
    finally { setBusy(false); }
  }
  return <Card>
    <CardHeader><CardTitle>Environments</CardTitle></CardHeader>
    <CardContent className="space-y-4">
      {error && <p role="alert" className="text-destructive">{error}</p>}
      <div className="flex flex-wrap gap-4">
        {(root.environments ?? []).map(environment => <Link key={environment.id} to={`/apps/${environment.id}`} aria-current={environment.id === app.id ? "page" : undefined} className="underline">
          {environment.name} · {environment.plan.toUpperCase()}{environment.deletionStatus ? " (deleting)" : ""}
        </Link>)}
      </div>
      <form className="flex flex-wrap items-end gap-2" onSubmit={e => { e.preventDefault(); void save("rename"); }}>
        <div><Label htmlFor="current-environment-name">Current environment name</Label>
          <Input id="current-environment-name" required maxLength={63} pattern="[A-Za-z0-9][A-Za-z0-9_-]*" value={currentName} onChange={e => setCurrentName(e.target.value)} /></div>
        <Button type="submit" disabled={busy || !!app.deletionStatus}>Rename</Button>
      </form>
      <form className="flex flex-wrap items-end gap-2" onSubmit={e => { e.preventDefault(); void save("create"); }}>
        <div><Label htmlFor="new-environment-name">New environment name</Label>
          <Input id="new-environment-name" required maxLength={63} pattern="[A-Za-z0-9][A-Za-z0-9_-]*" value={name} onChange={e => setName(e.target.value)} /></div>
        <div><Label htmlFor="environment-plan">VPS plan</Label>
          <select id="environment-plan" className="block h-10 rounded-md border bg-background px-3 text-sm" value={plan} onChange={e => setPlan(e.target.value as AppPlan)}>
            {planOptions.map(option => <option key={option.value} value={option.value}>{option.label} — {option.description}</option>)}
          </select></div>
        <Button type="submit" disabled={busy || !!root.deletionStatus || root.id === app.id && !!app.parentAppId}>Add environment</Button>
      </form>
      <p className="text-sm text-muted-foreground">Each environment has its own VPS, deployments, database, domains and secrets.</p>
    </CardContent>
  </Card>;
}
