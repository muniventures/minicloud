import { useState } from "react";
import { Button } from "@/components/ui/button";
import type { ProviderPlan } from "../../../api/providersApi";
import type { ApplicationEnvironment, SshAccessRequest } from "../../../models/application";
import { ApiError } from "../../../models/controlPlane";
import {
  buildSshAccessRequest,
  draftFromSshAccess,
  sshErrorMessage,
  sshModeLabel,
  sshRegistrationLabel,
  type SshAccessDraft,
  type SshDestinationAccount,
} from "../sshAccess";
import { SshAccessFields } from "./SshAccessFields";

// Environment settings view of the saved SSH selection. Saving only changes
// the selection for the next server allocation; it never touches a running VPS.
export function EnvironmentSshAccessPanel({
  env,
  plans,
  canEdit,
  onSave,
  onReload,
}: {
  env: ApplicationEnvironment;
  plans: ProviderPlan[];
  canEdit: boolean;
  onSave: (sshAccess: SshAccessRequest & { expectedRevision: number }) => Promise<void>;
  onReload: () => void;
}) {
  const access = env.sshAccess ?? null;
  const connection = env.sshConnection ?? null;
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState<SshAccessDraft>(() => draftFromSshAccess(access));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<{ message: string; conflict: boolean } | null>(null);

  const plan = plans.find(item => (env.planId && item.id === env.planId) || item.name === env.plan);
  const destination: SshDestinationAccount | null = plan
    ? { id: plan.providerAccountId, name: plan.providerName, providerType: plan.providerType }
    : access?.providerAccountId ? { id: access.providerAccountId } : null;
  const savedAccount = access?.providerAccountId
    ? plans.find(item => item.providerAccountId === access.providerAccountId)?.providerName ?? access.providerAccountId
    : null;

  async function save() {
    if (!access) return;
    const built = buildSshAccessRequest(draft, destination?.id ?? null);
    if (!built.request) {
      setError({ message: built.error, conflict: false });
      return;
    }
    setSaving(true);
    setError(null);
    try {
      await onSave({ ...built.request, expectedRevision: access.revision });
    } catch (saveError) {
      const conflict = saveError instanceof ApiError &&
        ["ssh_selection_revision_conflict", "ssh_selection_locked"].includes(saveError.code);
      setError({ message: sshErrorMessage(saveError, "Unable to save SSH access."), conflict });
    } finally {
      setSaving(false);
    }
  }

  if (!access) return <span className="text-xs text-muted-foreground">SSH access not reported</span>;

  return (
    <div className="grid min-w-0 gap-1 text-xs" aria-label={`SSH access for ${env.name}`}>
      <p className="text-sm">{sshModeLabel(access.mode)}</p>
      {access.mode !== "none" ? (
        <p className="break-all text-muted-foreground">
          {access.name ?? "Unnamed key"}{access.fingerprint ? ` · ${access.fingerprint}` : ""}{access.keyType ? ` · ${access.keyType}` : ""}
        </p>
      ) : null}
      {savedAccount ? <p className="text-muted-foreground">Provider account: {savedAccount}</p> : null}
      <p className="text-muted-foreground">Registration: {sshRegistrationLabel(access.registrationState)}</p>
      <p className="text-muted-foreground">Selection revision {access.revision}</p>
      {connection ? (
        <p>SSH login: <code className="break-all">{connection.username}@{connection.address}</code></p>
      ) : (
        <p className="text-muted-foreground">SSH login is shown after the server is created and its image login user is verified.</p>
      )}
      {access.selectionLocked ? (
        <p role="note" className="text-muted-foreground">
          SSH access is locked: a server allocation is in progress or a server exists. Allocated selections are immutable, and this form never updates keys on a running VPS.
        </p>
      ) : null}
      {canEdit && !access.selectionLocked && !editing ? (
        <Button size="sm" variant="outline" className="w-fit" onClick={() => { setDraft(draftFromSshAccess(access)); setError(null); setEditing(true); }}>
          Change SSH access
        </Button>
      ) : null}
      {canEdit && !access.selectionLocked && editing ? (
        <div className="grid gap-2 pt-1">
          <SshAccessFields idPrefix={`env-${env.id}`} organizationId={env.organizationId} account={destination} draft={draft} onChange={setDraft} disabled={saving} />
          <p className="text-muted-foreground">Changes apply to the next server allocation only.</p>
          {error ? (
            <div role="alert" className="grid gap-2 text-destructive">
              <p>{error.message}</p>
              {error.conflict ? <Button type="button" size="sm" variant="outline" className="w-fit" onClick={onReload}>Reload environment</Button> : null}
            </div>
          ) : null}
          <div className="flex gap-2">
            <Button size="sm" disabled={saving} onClick={() => void save()}>Save SSH access</Button>
            <Button size="sm" variant="outline" disabled={saving} onClick={() => { setEditing(false); setError(null); }}>Cancel</Button>
          </div>
        </div>
      ) : null}
    </div>
  );
}
