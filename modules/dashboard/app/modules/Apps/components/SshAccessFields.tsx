import { useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { listProviderSshKeys } from "../../../api/sshKeysApi";
import type { ProviderSshKey } from "../../../models/application";
import { ApiError } from "../../../models/controlPlane";
import {
  DEFERRED_REGISTRATION_TEXT,
  MAX_PUBLIC_KEY_LENGTH,
  looksLikePrivateKey,
  retargetSshDraft,
  type SshAccessDraft,
  type SshAccessMode,
  type SshDestinationAccount,
} from "../sshAccess";

type InventoryState =
  | { status: "idle" }
  | { status: "loading" }
  | { status: "ready"; keys: ProviderSshKey[] }
  | { status: "denied"; message: string }
  | { status: "unavailable"; message: string }
  | { status: "error"; message: string };

type ScopedInventory = { scope: string; state: InventoryState };

// Each request is tagged with its organization/account scope and a sequence
// number; responses from a superseded request are ignored even if the
// transport does not honour the abort signal.
export function useSshKeyInventory(organizationId: string | undefined, accountId: string | null, enabled: boolean) {
  const scope = enabled && organizationId && accountId ? `${organizationId}:${accountId}` : "";
  const [inventory, setInventory] = useState<ScopedInventory>({ scope: "", state: { status: "idle" } });
  const [attempt, setAttempt] = useState(0);
  const sequence = useRef(0);
  useEffect(() => {
    const requestId = ++sequence.current;
    if (!scope || !organizationId || !accountId) {
      setInventory({ scope: "", state: { status: "idle" } });
      return;
    }
    const controller = new AbortController();
    const current = () => !controller.signal.aborted && sequence.current === requestId;
    setInventory({ scope, state: { status: "loading" } });
    listProviderSshKeys(organizationId, accountId, controller.signal)
      .then(keys => {
        if (!current()) return;
        const scoped = Array.isArray(keys) ? keys.filter(key => key.providerAccountId === accountId) : [];
        setInventory({ scope, state: { status: "ready", keys: scoped } });
      })
      .catch(error => {
        if (!current()) return;
        setInventory({ scope, state: classifyInventoryError(error) });
      });
    return () => controller.abort();
  }, [scope, organizationId, accountId, attempt]);
  const state: InventoryState = !scope ? { status: "idle" } : inventory.scope === scope ? inventory.state : { status: "loading" };
  return { state, retry: () => setAttempt(value => value + 1) };
}

function classifyInventoryError(error: unknown): InventoryState {
  if (error instanceof ApiError && error.status === 403) {
    return { status: "denied", message: "You do not have permission to list SSH keys for this provider account (providers:read is required)." };
  }
  if (error instanceof ApiError && error.status !== 503 && error.code !== "ssh_inventory_unavailable") {
    return { status: "error", message: error.message };
  }
  return { status: "unavailable", message: "The provider SSH key inventory is temporarily unavailable. This does not mean the account has no keys." };
}

export function SshAccessFields({
  idPrefix,
  organizationId,
  account,
  draft,
  onChange,
  disabled,
}: {
  idPrefix: string;
  organizationId: string | undefined;
  account: SshDestinationAccount | null;
  draft: SshAccessDraft;
  onChange: (draft: SshAccessDraft) => void;
  disabled?: boolean;
}) {
  const accountId = account?.id ?? null;
  const inventory = useSshKeyInventory(organizationId, accountId, draft.mode === "existing");
  const [privateKeyRejected, setPrivateKeyRejected] = useState(false);
  const latestDraft = useRef(draft);
  latestDraft.current = draft;
  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;

  useEffect(() => {
    const next = retargetSshDraft(latestDraft.current, accountId);
    if (next !== latestDraft.current) onChangeRef.current(next);
  }, [accountId]);

  const setMode = (mode: SshAccessMode) => onChange({ ...draft, mode });
  const updatePublicKey = (value: string) => {
    if (looksLikePrivateKey(value)) {
      setPrivateKeyRejected(true);
      onChange({ ...draft, publicKey: "" });
      return;
    }
    setPrivateKeyRejected(false);
    onChange({ ...draft, publicKey: value });
  };
  const modes: { value: SshAccessMode; label: string }[] = [
    { value: "none", label: "Minicloud access only" },
    { value: "existing", label: "Use existing key" },
    { value: "new", label: "Add new public key" },
  ];
  const accountLabel = account ? [account.name || account.id, account.providerType].filter(Boolean).join(" · ") : null;

  return (
    <fieldset className="grid min-w-0 gap-3 rounded-md border p-3" disabled={disabled}>
      <legend className="px-1 text-sm font-medium">SSH access</legend>
      <p className="text-xs text-muted-foreground">
        {accountLabel ? <>Destination provider account: <span className="font-medium text-foreground">{accountLabel}</span></> : "Select a plan to choose a provider account for personal SSH keys."}
      </p>
      <div role="radiogroup" aria-label="SSH access" className="flex flex-wrap gap-4 text-sm">
        {modes.map(option => (
          <label key={option.value} className="inline-flex items-center gap-2">
            <input
              type="radio"
              name={`${idPrefix}-ssh-mode`}
              value={option.value}
              checked={draft.mode === option.value}
              disabled={option.value !== "none" && !account}
              onChange={() => setMode(option.value)}
            />
            {option.label}
          </label>
        ))}
      </div>
      {draft.mode === "none" ? (
        <p className="text-xs text-muted-foreground">Minicloud keeps its own deployment access. No personal key is added to the server.</p>
      ) : null}
      {draft.mode === "existing" && account ? (
        <ExistingKeyPicker idPrefix={idPrefix} state={inventory.state} retry={inventory.retry} draft={draft} accountId={account.id} onChange={onChange} />
      ) : null}
      {draft.mode === "new" && account ? (
        <div className="grid gap-3">
          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}-ssh-key-name`}>SSH key name</Label>
            <Input id={`${idPrefix}-ssh-key-name`} maxLength={128} value={draft.name} onChange={event => onChange({ ...draft, name: event.target.value })} />
          </div>
          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}-ssh-public-key`}>Public key</Label>
            <Textarea
              id={`${idPrefix}-ssh-public-key`}
              className="font-mono text-xs"
              rows={3}
              spellCheck={false}
              autoComplete="off"
              maxLength={MAX_PUBLIC_KEY_LENGTH}
              placeholder="ssh-ed25519 AAAA… you@example.com"
              value={draft.publicKey}
              onChange={event => updatePublicKey(event.target.value)}
            />
            <p className="text-xs text-muted-foreground">Paste your public key, for example from ~/.ssh/id_ed25519.pub. Keep your private key on your computer.</p>
            {privateKeyRejected ? (
              <p role="alert" className="text-xs text-destructive">That looks like a private key and was not kept. Paste only the public key (.pub file).</p>
            ) : null}
          </div>
          <p className="text-xs text-muted-foreground">Adding a new key requires an owner or admin (providers:write).</p>
        </div>
      ) : null}
      {draft.mode !== "none" && account ? (
        <p className="text-xs text-muted-foreground">{DEFERRED_REGISTRATION_TEXT} Saving does not register the key or create a server.</p>
      ) : null}
    </fieldset>
  );
}

function ExistingKeyPicker({
  idPrefix,
  state,
  retry,
  draft,
  accountId,
  onChange,
}: {
  idPrefix: string;
  state: InventoryState;
  retry: () => void;
  draft: SshAccessDraft;
  accountId: string;
  onChange: (draft: SshAccessDraft) => void;
}) {
  if (state.status === "idle" || state.status === "loading") {
    return <p role="status" className="text-xs text-muted-foreground">Loading SSH keys…</p>;
  }
  if (state.status !== "ready") {
    const title = state.status === "denied" ? "SSH key list denied" : state.status === "unavailable" ? "SSH key list unavailable" : "SSH key list failed";
    return (
      <div role="alert" data-inventory-state={state.status} className="grid gap-2 text-xs text-destructive">
        <p><span className="font-medium">{title}.</span> {state.message}</p>
        <p className="text-muted-foreground">You can still choose Minicloud access only or add a new public key.</p>
        {state.status !== "denied" ? <Button type="button" size="sm" variant="outline" className="w-fit" onClick={retry}>Retry</Button> : null}
      </div>
    );
  }
  if (state.keys.length === 0) {
    return (
      <p role="status" data-inventory-state="empty" className="text-xs text-muted-foreground">
        This provider account has no SSH keys yet. Add a new public key instead.
      </p>
    );
  }
  const selected = draft.keyAccountId === accountId ? draft.keyId : "";
  return (
    <div className="grid gap-2">
      <Label htmlFor={`${idPrefix}-ssh-key`}>SSH key</Label>
      <select
        id={`${idPrefix}-ssh-key`}
        className="h-10 w-full min-w-0 max-w-full rounded-md border bg-background px-3 text-sm"
        value={selected}
        onChange={event => onChange({ ...draft, keyId: event.target.value, keyAccountId: event.target.value ? accountId : null })}
      >
        <option value="">Select an SSH key</option>
        {selected && !state.keys.some(key => key.id === selected) ? <option value={selected}>Saved key (not in the current provider list)</option> : null}
        {state.keys.map(key => (
          <option key={key.id} value={key.id}>{key.name} — {key.fingerprint} ({key.keyType})</option>
        ))}
      </select>
    </div>
  );
}
