import { useEffect, useState } from "react";
import { useParams, useSearchParams } from "react-router-dom";
import type { DeploymentRef } from "@/api/deploymentsApi";
import { useAuth } from "@/core/auth/AuthProvider";
import { deploymentOrganizationParam, isDeploymentNumber } from "./deploymentRoutes";

export type DeploymentScope =
  | { status: "resolving"; number?: string; ref?: undefined }
  | { status: "not_found"; number?: string; ref?: undefined }
  | { status: "ready"; number: string; ref: DeploymentRef };

// Resolves the organization for a number-based deep link. A URL hint is only honored when it
// matches a loaded membership and is selected before any request; the number never implies a tenant.
export function useDeploymentScope(): DeploymentScope {
  const { deploymentNumber } = useParams();
  const [searchParams, setSearchParams] = useSearchParams();
  const hint = searchParams.get(deploymentOrganizationParam);
  const { profile, selectedOrganization, selectOrganization } = useAuth();
  const memberships = profile?.organizations;
  const hintAllowed = !hint || !memberships || memberships.some((organization) => organization.id === hint);
  const [appliedHint, setAppliedHint] = useState<string | null>(null);
  const selectedId = selectedOrganization?.id ?? null;
  const hintPending = Boolean(hint && hintAllowed && appliedHint !== hint && selectedId !== hint);

  useEffect(() => {
    if (!hint || !memberships || !hintAllowed || appliedHint === hint) return;
    if (selectedId === hint || selectOrganization(hint)) setAppliedHint(hint);
  }, [hint, memberships, hintAllowed, appliedHint, selectedId, selectOrganization]);

  // Keep the shareable URL scoped to the organization that actually owns the requests.
  useEffect(() => {
    if (!selectedId || hintPending || !hintAllowed || hint === selectedId) return;
    if (hint && appliedHint !== hint) return;
    setSearchParams((current) => {
      const next = new URLSearchParams(current);
      next.set(deploymentOrganizationParam, selectedId);
      return next;
    }, { replace: true });
  }, [hint, hintAllowed, hintPending, appliedHint, selectedId, setSearchParams]);

  if (!isDeploymentNumber(deploymentNumber)) return { status: "not_found", number: deploymentNumber };
  if (!memberships) return { status: "resolving", number: deploymentNumber };
  if (!hintAllowed) return { status: "not_found", number: deploymentNumber };
  if (hintPending || !selectedId) return { status: selectedId ? "resolving" : "not_found", number: deploymentNumber };
  return { status: "ready", number: deploymentNumber, ref: { organizationId: selectedId, number: deploymentNumber } };
}
