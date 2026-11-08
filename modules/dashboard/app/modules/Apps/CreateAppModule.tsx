import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Loader2 } from "lucide-react";
import { FormEvent, useEffect, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { createApp } from "../../api/appsApi";
import { useAuth } from "../../core/auth/AuthProvider";
import { PageScaffold } from "../../core/components/PageScaffold";
import { dashboardRoutes } from "../../core/routing/dashboardRoutes";
import type { DatabaseMode } from "../../models/controlPlane";
import { planDescription, usePlanCatalog } from "../Providers/usePlanCatalog";

export function CreateAppModule() {
  const { selectedOrganization } = useAuth();
  const [environmentName, setEnvironmentName] = useState("production");
  const [name, setName] = useState("");
  const [slug, setSlug] = useState("");
  const [isSlugEdited, setIsSlugEdited] = useState(false);
  const [plan, setPlan] = useState("");
  const catalog = usePlanCatalog(selectedOrganization?.id);
  useEffect(() => { setPlan(""); }, [selectedOrganization?.id]);
  const [database, setDatabase] = useState<DatabaseMode>("sqlite");
  const [error, setError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const navigate = useNavigate();

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!selectedOrganization || !catalog.plans.some(item => item.name === plan)) {
      return;
    }

    setError(null);
    setIsSubmitting(true);
    try {
      const app = await createApp({
        organizationId: selectedOrganization.id,
        environmentName,
        name,
        slug: slug || slugify(name),
        plan,
        database
      });
      navigate(`/apps/${app.id}`);
    } catch (submitError) {
      setError(submitError instanceof Error ? submitError.message : "Unable to create app.");
    } finally {
      setIsSubmitting(false);
    }
  }

  function updateName(nextName: string) {
    setName(nextName);
    if (!isSlugEdited) {
      setSlug(slugify(nextName));
    }
  }

  function updateSlug(nextSlug: string) {
    setIsSlugEdited(true);
    setSlug(slugify(nextSlug));
  }

  return (
    <PageScaffold breadcrumbs={[{ label: "Apps", to: dashboardRoutes.apps.path }, { label: "Create app" }]} title="Create app">
      <Card className="max-w-2xl">
        <CardHeader>
          <CardTitle>App registration</CardTitle>
        </CardHeader>
        <CardContent>
          <form className="grid gap-4" onSubmit={submit}>
            <div className="grid gap-2">
              <Label htmlFor="name">Name</Label>
              <Input id="name" onChange={(event) => updateName(event.target.value)} required value={name} />
            </div>
            <div className="grid gap-2">
              <Label htmlFor="slug">Slug</Label>
              <Input id="slug" onChange={(event) => updateSlug(event.target.value)} value={slug} />
            </div>
            <div className="grid gap-2">
              <Label htmlFor="environmentName">Environment name</Label>
              <Input id="environmentName" required maxLength={63} pattern="[A-Za-z0-9][A-Za-z0-9_-]*" value={environmentName} onChange={(event) => setEnvironmentName(event.target.value)} />
            </div>
            <div className="grid gap-2 sm:grid-cols-2">
              <div className="grid gap-2">
                <Label htmlFor="app-plan">Plan</Label>
                <select id="app-plan" className="h-10 rounded-md border bg-background px-3 text-sm" required disabled={catalog.loading} value={plan} onChange={event => setPlan(event.target.value)}>
                  <option value="">{catalog.loading ? "Loading plans…" : "Select a plan"}</option>
                  {catalog.plans.map(option => <option key={option.id} value={option.name}>{option.name} — {planDescription(option)}</option>)}
                </select>
                {catalog.error && <p role="alert" className="text-sm text-destructive">{catalog.error}</p>}
                {!catalog.loading && !catalog.error && !catalog.plans.length && <p className="text-sm text-muted-foreground">No enabled plans. <Link className="underline" to={dashboardRoutes.providers.path}>Configure a provider and plan</Link> first.</p>}
              </div>
              <div className="grid gap-2">
                <Label>Database</Label>
                <Select onValueChange={(value) => setDatabase(value as DatabaseMode)} value={database}>
                  <SelectTrigger>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="sqlite">SQLite</SelectItem>
                    <SelectItem value="postgres">Postgres</SelectItem>
                  </SelectContent>
                </Select>
              </div>
            </div>
            {error ? <p className="text-sm text-destructive">{error}</p> : null}
            <Button className="w-fit" disabled={isSubmitting || catalog.loading || !catalog.plans.some(item => item.name === plan)} type="submit">
              {isSubmitting ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
              Create app
            </Button>
          </form>
        </CardContent>
      </Card>
    </PageScaffold>
  );
}

function slugify(value: string) {
  return value
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
}
