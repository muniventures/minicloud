import { ApiError } from "../../models/controlPlane";
import type { EnvironmentSshAccess, SshAccessRequest } from "../../models/application";

export type SshAccessMode = "none" | "existing" | "new";

// Unsaved client-side draft. keyAccountId records which provider account the
// selected existing key came from, so a key can never be sent for another account.
export type SshAccessDraft = {
  mode: SshAccessMode;
  keyId: string;
  keyAccountId: string | null;
  name: string;
  publicKey: string;
};

export type SshDestinationAccount = { id: string; name?: string | null; providerType?: string | null };

export const defaultSshAccessDraft: SshAccessDraft = { mode: "none", keyId: "", keyAccountId: null, name: "", publicKey: "" };

export const DEFERRED_REGISTRATION_TEXT = "This key will be installed when the server is created.";
export const MAX_PUBLIC_KEY_LENGTH = 16 * 1024;

const PRIVATE_KEY_PATTERN = /-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----|PuTTY-User-Key-File-|^\s*-----BEGIN SSH2 ENCRYPTED PRIVATE KEY-----/m;

export function looksLikePrivateKey(value: string) {
  return PRIVATE_KEY_PATTERN.test(value);
}

export function draftFromSshAccess(access: EnvironmentSshAccess | null | undefined): SshAccessDraft {
  if (access?.mode === "existing" && access.keyId) {
    return { ...defaultSshAccessDraft, mode: "existing", keyId: access.keyId, keyAccountId: access.providerAccountId };
  }
  if (access?.mode === "new") {
    return { ...defaultSshAccessDraft, mode: "new", name: access.name ?? "" };
  }
  return defaultSshAccessDraft;
}

// Account changes clear an incompatible existing-key selection; a new
// public-key draft may stay because it is registered with the destination account.
export function retargetSshDraft(draft: SshAccessDraft, accountId: string | null): SshAccessDraft {
  if (draft.keyId && draft.keyAccountId !== accountId) return { ...draft, keyId: "", keyAccountId: null };
  return draft;
}

export type SshRequestResult = { request: SshAccessRequest; error?: undefined } | { request?: undefined; error: string };

export function buildSshAccessRequest(draft: SshAccessDraft, accountId: string | null): SshRequestResult {
  if (draft.mode === "none") return { request: { mode: "none" } };
  if (!accountId) return { error: "The selected plan has no provider account for SSH keys. Choose Minicloud access only." };
  if (draft.mode === "existing") {
    if (!draft.keyId || draft.keyAccountId !== accountId) return { error: "Select an SSH key from the destination provider account." };
    return { request: { mode: "existing", keyId: draft.keyId } };
  }
  const name = draft.name.trim();
  const publicKey = draft.publicKey.trim();
  if (!name || name.length > 128 || /\p{Cc}/u.test(name)) return { error: "Enter an SSH key name of 1 to 128 characters." };
  if (looksLikePrivateKey(draft.publicKey)) return { error: "That is a private key. Paste only your public key (.pub file)." };
  if (!publicKey) return { error: "Paste an OpenSSH public key." };
  if (draft.publicKey.length > MAX_PUBLIC_KEY_LENGTH) return { error: "The public key is too long. Paste a single OpenSSH public key." };
  if (publicKey.split(/\r?\n/).filter(line => line.trim()).length > 1) return { error: "Paste exactly one public key." };
  if (!/^(ssh-ed25519|ssh-rsa)\s+\S+/.test(publicKey)) {
    return { error: "Paste one OpenSSH Ed25519 or RSA public key starting with ssh-ed25519 or ssh-rsa." };
  }
  return { request: { mode: "new", name, publicKey } };
}

export function sshErrorMessage(error: unknown, fallback = "Operation failed.") {
  if (error instanceof ApiError) {
    switch (error.code) {
      case "ssh_selection_locked":
        return "SSH access is locked because server allocation is in progress or a server already exists. Allocated selections are immutable and this form never updates a running VPS.";
      case "ssh_selection_revision_conflict":
        return "SSH access changed since this page loaded. Reload the environment and review the current selection before saving again.";
      case "ssh_key_changed":
        return "The selected SSH key changed or is no longer available at the provider. Refresh the key list and select a key again.";
      case "ssh_key_invalid":
        return `The SSH public key was rejected: ${error.message} Paste one OpenSSH Ed25519 or RSA (3072-bit minimum) public key.`;
      case "ssh_key_not_found":
        return "The selected SSH key was not found in the destination provider account. Refresh the key list and choose another key.";
      case "ssh_inventory_unavailable":
        return "The provider SSH key inventory is temporarily unavailable. Retry later, or choose Minicloud access only or a new public key.";
      case "ssh_registration_failed":
        return "The provider rejected the SSH key registration. Check the public key or choose another key, then retry.";
      case "ssh_selection_account_mismatch":
        return "The new plan uses a different provider account. Save SSH access for that account (or Minicloud access only) before changing the plan.";
    }
    if (error.status === 403) {
      return `Permission denied: ${error.message} Selecting an existing key requires provider read access; adding a new key requires an owner or admin (providers:write).`;
    }
  }
  return error instanceof Error ? error.message : fallback;
}

export function sshModeLabel(mode: string | null | undefined) {
  switch (mode) {
    case "existing": return "Existing provider key";
    case "new": return "New public key";
    case "none": return "Minicloud access only";
    default: return mode ?? "Unknown";
  }
}

export function sshRegistrationLabel(state: string | null | undefined) {
  switch (state) {
    case "none": return "No personal key";
    case "not_registered": return "Not registered yet — installed when the server is created";
    case "discovered": return "Existing provider key";
    case "pending": return "Registration pending";
    case "registering": return "Registering with provider";
    case "ready": return "Registered with provider";
    case "failed": return "Registration failed";
    case "unknown": return "Registration outcome unknown — awaiting reconciliation";
    default: return state ?? "Unknown";
  }
}
