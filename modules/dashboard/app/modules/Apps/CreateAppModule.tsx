import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Loader2 } from "lucide-react";
import { FormEvent, useEffect, useRef, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { createApplication } from "../../api/applicationsApi";
import { useAuth } from "../../core/auth/AuthProvider";
import { PageScaffold } from "../../core/components/PageScaffold";
import { dashboardRoutes } from "../../core/routing/dashboardRoutes";
import type { DatabaseMode } from "../../models/controlPlane";
import { planDescription, usePlanCatalog } from "../Providers/usePlanCatalog";
import { SshAccessFields } from "./components/SshAccessFields";
import { buildSshAccessRequest, defaultSshAccessDraft, sshErrorMessage, type SshAccessDraft } from "./sshAccess";

export function CreateAppModule() {
  const { selectedOrganization } = useAuth();
  const currentOrganizationId = useRef(selectedOrganization?.id);
  currentOrganizationId.current = selectedOrganization?.id;
  const canCreate = ["owner", "admin", "developer"].includes(selectedOrganization?.role?.toLowerCase() ?? "");
  const [environmentName, setEnvironmentName] = useState("production");
  const [name, setName] = useState("");
  const [slug, setSlug] = useState("");
  const [isSlugEdited, setIsSlugEdited] = useState(false);
  const [plan, setPlan] = useState("");
  const catalog = usePlanCatalog(selectedOrganization?.id);
  useEffect(() => {
    currentOrganizationId.current = selectedOrganization?.id;
    setPlan("");
    setSshDraft(defaultSshAccessDraft);
    return () => { currentOrganizationId.current = undefined; };
  }, [selectedOrganization?.id]);
  const [database, setDatabase] = useState<DatabaseMode>("sqlite");
  const [sshDraft, setSshDraft] = useState<SshAccessDraft>(defaultSshAccessDraft);
  const selectedPlan = catalog.plans.find(item => item.name === plan);
  const sshAccount = selectedPlan ? { id: selectedPlan.providerAccountId, name: selectedPlan.providerName, providerType: selectedPlan.providerType } : null;
  const [error, setError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const navigate = useNavigate();

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!canCreate || !selectedOrganization || !selectedPlan) {
      return;
    }
    const ssh = buildSshAccessRequest(sshDraft, selectedPlan.providerAccountId);
    if (!ssh.request) {
      setError(ssh.error);
      return;
    }

    const organizationId = selectedOrganization.id;
    setError(null);
    setIsSubmitting(true);
    try {
      const app = await createApplication({
        organizationId,
        name,
        slug: slug || slugify(name),
        initialEnvironment: { name: environmentName, plan, database, sshAccess: ssh.request }
      });
      if (currentOrganizationId.current === organizationId) navigate(`/apps/${app.id}`);
    } catch (submitError) {
      if (currentOrganizationId.current === organizationId) setError(sshErrorMessage(submitError, "Unable to create app."));
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

  if (!canCreate) {
    return <PageScaffold title="Create app"><p role="alert">You do not have permission to create applications.</p></PageScaffold>;
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
            <div className="grid min-w-0 gap-2 sm:grid-cols-2 [&>*]:min-w-0">
              <div className="grid gap-2">
                <Label htmlFor="app-plan">Plan</Label>
                <select id="app-plan" className="h-10 w-full min-w-0 max-w-full rounded-md border bg-background px-3 text-sm" required disabled={catalog.loading} value={plan} onChange={event => setPlan(event.target.value)}>
                  <option value="">{catalog.loading ? "Loading plans…" : "Select a plan"}</option>
                  {catalog.plans.map(option => <option key={option.id} value={option.name}>{option.name} — {planDescription(option)}</option>)}
                </select>
                {catalog.error && <p role="alert" className="text-sm text-destructive">{catalog.error}</p>}
                {!catalog.loading && !catalog.error && !catalog.plans.length && <p className="text-sm text-muted-foreground">No enabled plans. <Link className="underline" to={dashboardRoutes.plans.path}>Configure a provider and plan</Link> first.</p>}
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
            {selectedPlan ? (
              <SshAccessFields idPrefix="app" organizationId={selectedOrganization?.id} account={sshAccount} draft={sshDraft} onChange={setSshDraft} disabled={isSubmitting} />
            ) : null}
            {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
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
