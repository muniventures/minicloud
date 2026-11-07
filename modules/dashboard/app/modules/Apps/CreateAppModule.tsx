import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Loader2 } from "lucide-react";
import { FormEvent, useState } from "react";
import { useNavigate } from "react-router-dom";
import { createApp } from "../../api/appsApi";
import { useAuth } from "../../core/auth/AuthProvider";
import { PageScaffold } from "../../core/components/PageScaffold";
import { dashboardRoutes } from "../../core/routing/dashboardRoutes";
import type { AppPlan, DatabaseMode } from "../../models/controlPlane";

const planOptions: { value: AppPlan; label: string; description: string }[] = [
  { value: "p0-h-0", label: "P0-H-0", description: "Hetzner CX23, 2 vCPU, 4 GB RAM" },
  { value: "p0-v-0", label: "P0-V-0", description: "Vultr $2.50 IPv6-only, 1 vCPU, 0.5 GB RAM" },
  { value: "p0-v-1", label: "P0-V-1", description: "Vultr $3.50, 1 vCPU, 0.5 GB RAM" },
  { value: "p0-v-2", label: "P0-V-2", description: "Vultr $5.00, 1 vCPU, 1 GB RAM" },
  { value: "p0-d-0", label: "P0-D-0", description: "DigitalOcean $4, 1 vCPU, 512 MiB RAM" },
  { value: "p0-d-1", label: "P0-D-1", description: "DigitalOcean $6, 1 vCPU, 1 GiB RAM" },
  { value: "p0-d-2", label: "P0-D-2", description: "DigitalOcean $12, 1 vCPU, 2 GiB RAM" }
];

export function CreateAppModule() {
  const { selectedOrganization } = useAuth();
  const [name, setName] = useState("");
  const [slug, setSlug] = useState("");
  const [isSlugEdited, setIsSlugEdited] = useState(false);
  const [plan, setPlan] = useState<AppPlan>("p0-h-0");
  const [database, setDatabase] = useState<DatabaseMode>("sqlite");
  const [error, setError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const navigate = useNavigate();

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!selectedOrganization) {
      return;
    }

    setError(null);
    setIsSubmitting(true);
    try {
      const app = await createApp({
        organizationId: selectedOrganization.id,
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
            <div className="grid gap-2 sm:grid-cols-2">
              <div className="grid gap-2">
                <Label>Plan</Label>
                <Select onValueChange={(value) => setPlan(value as AppPlan)} value={plan}>
                  <SelectTrigger>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {planOptions.map((option) => (
                      <SelectItem key={option.value} value={option.value}>
                        <span className="flex flex-col">
                          <span>{option.label}</span>
                          <span className="text-xs text-muted-foreground">{option.description}</span>
                        </span>
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
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
            <Button className="w-fit" disabled={isSubmitting} type="submit">
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
