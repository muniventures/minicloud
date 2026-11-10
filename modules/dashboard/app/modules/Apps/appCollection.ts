import { compareRecords } from "@/core/components/collectionQuery";
import type { ApplicationEnvironment, Application } from "@/models/application";

// Collection rules for the Apps list (UX-02). No runtime or plan predicates
// exist here: those belong inside the selected environment. An application
// matches when at least one environment satisfies ALL selected environment
// predicates together; predicates are never combined across environments.

export type EnvFilter = { kind?: string; deployment?: string };

export function orderedEnvironments(app: Application): ApplicationEnvironment[] {
  const persistents = app.environments.filter((env) => env.kind !== "preview");
  const previews = app.environments.filter((env) => env.kind === "preview");
  const byName = (a: ApplicationEnvironment, b: ApplicationEnvironment) =>
    compareRecords(a.name, b.name, "asc", a.id, b.id);
  return [...persistents.sort(byName), ...previews.sort(byName)];
}

export function needsAttention(env: ApplicationEnvironment): boolean {
  return (
    env.latestDeployment?.status === "failed" ||
    env.deletionStatus === "failed" ||
    (env.latestDeployment != null && env.runtimeObservation.stale)
  );
}

export function matchesSearch(app: Application, search: string): boolean {
  const term = search.trim().toLowerCase();
  if (!term) return true;
  const haystack = [
    app.name,
    app.slug,
    ...app.environments.flatMap((env) => [
      env.name,
      ...env.domains.map((d) => `${d.hostname} ${d.url}`),
    ]),
  ]
    .join(" ")
    .toLowerCase();
  return haystack.includes(term);
}

function envSatisfiesAll(env: ApplicationEnvironment, f: EnvFilter): boolean {
  if (f.kind && env.kind !== f.kind) return false;
  if (f.deployment) {
    if (f.deployment === "none") return env.latestDeployment == null;
    if ((env.latestDeployment?.status ?? "none") !== f.deployment) return false;
  }
  return true;
}

export function matchedEnvironment(
  app: Application,
  f: EnvFilter,
): ApplicationEnvironment | null {
  if (!f.kind && !f.deployment) return null;
  return (
    orderedEnvironments(app).find((env) => envSatisfiesAll(env, f)) ?? null
  );
}

export function appMatchesFilters(app: Application, f: EnvFilter): boolean {
  if (!f.kind && !f.deployment) return true;
  return matchedEnvironment(app, f) != null;
}

export type VisibleEnvironments = {
  visible: ApplicationEnvironment[];
  hiddenPreviewCount: number;
};

export function visibleEnvironments(
  app: Application,
  opts: { previewsExpanded?: boolean; matchedId?: string | null },
): VisibleEnvironments {
  const ordered = orderedEnvironments(app);
  if (opts.previewsExpanded) return { visible: ordered, hiddenPreviewCount: 0 };
  const visible = ordered.filter(
    (env) =>
      env.kind !== "preview" ||
      env.id === opts.matchedId ||
      needsAttention(env),
  );
  const hiddenPreviewCount = ordered.length - visible.length;
  // Keep ordering: persistents first, then previews (already sorted).
  return { visible, hiddenPreviewCount };
}

export function sortApplications(
  apps: Application[],
  sort: string,
  direction: "asc" | "desc",
): Application[] {
  return [...apps].sort((a, b) =>
    sort === "name"
      ? compareRecords(a.name, b.name, direction, a.id, b.id)
      : compareRecords(
          a.updatedAt,
          b.updatedAt,
          direction,
          a.id,
          b.id,
        ),
  );
}
