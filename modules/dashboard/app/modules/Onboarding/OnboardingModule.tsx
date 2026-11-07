import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Loader2 } from "lucide-react";
import { FormEvent, useState } from "react";
import { Navigate, useNavigate } from "react-router-dom";
import { createOrganization } from "../../api/organizationsApi";
import { AuthenticatedArea } from "../../core/auth/AuthenticatedArea";
import { useAuth } from "../../core/auth/AuthProvider";

export function OnboardingModule() {
  return (
    <AuthenticatedArea>
      <OnboardingForm />
    </AuthenticatedArea>
  );
}

function OnboardingForm() {
  const { profile, refreshProfile } = useAuth();
  const [name, setName] = useState("");
  const [slug, setSlug] = useState("");
  const [isSlugEdited, setIsSlugEdited] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const navigate = useNavigate();

  if (profile && profile.organizations.length > 0) {
    return <Navigate replace to="/apps" />;
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setIsSubmitting(true);
    try {
      await createOrganization({ name, slug: slug || slugify(name) });
      await refreshProfile();
      navigate("/apps", { replace: true });
    } catch (submitError) {
      setError(submitError instanceof Error ? submitError.message : "Organization creation failed.");
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
    <main className="flex min-h-screen items-center justify-center bg-background px-4">
      <Card className="w-full max-w-lg">
        <CardHeader>
          <CardTitle>Create organization</CardTitle>
          <CardDescription>Your organization owns apps, deployments, and API keys.</CardDescription>
        </CardHeader>
        <CardContent>
          <form className="space-y-4" onSubmit={submit}>
            <div className="space-y-2">
              <Label htmlFor="organization-name">Name</Label>
              <Input id="organization-name" onChange={(event) => updateName(event.target.value)} required value={name} />
            </div>
            <div className="space-y-2">
              <Label htmlFor="organization-slug">Slug</Label>
              <Input
                id="organization-slug"
                onChange={(event) => updateSlug(event.target.value)}
                required
                value={slug}
              />
            </div>
            {error ? <p className="text-sm text-destructive">{error}</p> : null}
            <Button className="w-full" disabled={isSubmitting} type="submit">
              {isSubmitting ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
              Create organization
            </Button>
          </form>
        </CardContent>
      </Card>
    </main>
  );
}

function slugify(value: string) {
  return value
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
}
