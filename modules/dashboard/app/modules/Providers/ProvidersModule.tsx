import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type FormEvent,
} from "react";
import { Navigate, useLocation, useNavigate } from "react-router-dom";
import { ChevronDown, ChevronRight, Lock, MoreHorizontal, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import {
  createPlan,
  createProvider,
  deletePlan,
  deleteProvider,
  listPlans,
  listProviders,
  listProviderTypes,
  replaceProviderCredential,
  updatePlan,
  updateProvider,
  validateProvider,
  type PlanInput,
  type ProviderAccount,
  type ProviderPlan,
  type ProviderType,
} from "../../api/providersApi";
import { useAuth } from "../../core/auth/AuthProvider";
import { CollectionToolbar } from "../../core/components/CollectionToolbar";
import {
  compareRecords,
  options,
  useCollectionQuery,
  type CollectionQuery,
} from "../../core/components/collectionQuery";
import { Disclosure } from "../../core/components/Disclosure";
import { EmptyState } from "../../core/components/EmptyState";
import { PageScaffold } from "../../core/components/PageScaffold";
import { ApiError } from "../../models/controlPlane";

const selectClass =
  "h-10 w-full rounded-md border border-input bg-background px-3 text-sm disabled:cursor-not-allowed disabled:opacity-60";

function getErrorMessage(error: unknown, fallback = "Unable to save changes.") {
  return error instanceof Error && error.message ? error.message : fallback;
}

function isConflictOrLockError(error: unknown) {
  if (error instanceof ApiError) {
    if (
      error.code === "revision_conflict" ||
      error.code === "plan_mapping_locked" ||
      error.status === 409
    ) {
      return (
        error.code === "revision_conflict" ||
        error.code === "plan_mapping_locked" ||
        /changed|reload|reserved for provisioning|locked/i.test(error.message)
      );
    }
  }
  if (error instanceof Error) {
    return /this item changed|reload it before saving|reserved for provisioning|placement is locked/i.test(
      error.message,
    );
  }
  return false;
}

function formatProviderName(type: string, types: ProviderType[]) {
  const match = types.find((item) => item.type === type);
  if (match) return match.name;
  if (type === "hetzner") return "Hetzner";
  if (type === "digital-ocean") return "DigitalOcean";
  if (type === "vultr") return "Vultr";
  return type;
}

function providerShortMark(type: string) {
  if (type === "hetzner") return { text: "H", className: "bg-destructive/15 text-destructive" };
  if (type === "digital-ocean") return { text: "DO", className: "bg-info-surface text-info" };
  if (type === "vultr") return { text: "V", className: "bg-secondary text-foreground" };
  return { text: type.slice(0, 1).toUpperCase() || "P", className: "bg-muted text-muted-foreground" };
}

function regionExample(type: string) {
  if (type === "hetzner") return "nbg1";
  if (type === "digital-ocean") return "nyc3";
  if (type === "vultr") return "ewr";
  return "nbg1";
}

function skuExample(type: string) {
  if (type === "hetzner") return "cx23";
  if (type === "digital-ocean") return "s-1vcpu-1gb";
  if (type === "vultr") return "vc2-1c-1gb";
  return "cx23";
}

function defaultImageForType(type: string, types: ProviderType[]) {
  if (type === "vultr") return "";
  return types.find((item) => item.type === type)?.defaultImage ?? "";
}

function resolveAccountEnabled(plan: ProviderPlan, accounts: ProviderAccount[]) {
  const account = accounts.find((item) => item.id === plan.providerAccountId);
  return account ? account.enabled : plan.accountEnabled;
}

function resolveAccountName(plan: ProviderPlan, accounts: ProviderAccount[]) {
  const account = accounts.find((item) => item.id === plan.providerAccountId);
  return account ? account.name : plan.providerName;
}

export type PlanStatusLabel = "Enabled" | "Disabled" | "Account disabled";

function resolvePlanStatus(plan: ProviderPlan, accounts: ProviderAccount[]): PlanStatusLabel {
  if (!plan.enabled) return "Disabled";
  if (!resolveAccountEnabled(plan, accounts)) return "Account disabled";
  return "Enabled";
}

function matchesPlanQuery(
  plan: ProviderPlan,
  query: CollectionQuery,
  accounts: ProviderAccount[],
  types: ProviderType[],
) {
  const accountName = resolveAccountName(plan, accounts);
  const providerLabel = formatProviderName(plan.providerType, types);
  const status = resolvePlanStatus(plan, accounts);
  const placement = plan.provisioningLocked ? "locked" : "unlocked";
  const search = query.search.trim().toLowerCase();
  if (search) {
    const haystack = [
      plan.name,
      accountName,
      plan.providerName,
      plan.providerType,
      providerLabel,
      plan.region,
      plan.sku,
    ]
      .join(" ")
      .toLowerCase();
    if (!haystack.includes(search)) return false;
  }
  const f = query.filters;
  if (f.provider && plan.providerType !== f.provider) return false;
  if (f.account && plan.providerAccountId !== f.account) return false;
  if (f.status && status !== f.status) return false;
  if (f.placement && placement !== f.placement) return false;
  return true;
}

function formatValidationStatus(status: string) {
  const clean = (status || "unchecked").replaceAll("_", " ").trim();
  return clean.charAt(0).toUpperCase() + clean.slice(1);
}

function formatRelativeTime(iso: string) {
  const timestamp = Date.parse(iso);
  if (Number.isNaN(timestamp)) return iso;
  const diffSeconds = Math.round((Date.now() - timestamp) / 1000);
  if (Math.abs(diffSeconds) < 45) return "just now";
  const minutes = Math.round(diffSeconds / 60);
  if (Math.abs(minutes) < 60) return `${Math.abs(minutes)}m ago`;
  const hours = Math.round(minutes / 60);
  if (Math.abs(hours) < 24) return `${Math.abs(hours)}h ago`;
  const days = Math.round(hours / 24);
  return `${Math.abs(days)}d ago`;
}

function formatFullTimestamp(iso: string) {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(date);
}

type EditorState =
  | { kind: "add-plan"; preferredAccountId?: string }
  | { kind: "duplicate-plan"; sourcePlan: ProviderPlan }
  | { kind: "edit-plan"; plan: ProviderPlan }
  | { kind: "delete-plan"; plan: ProviderPlan }
  | { kind: "add-account"; resumeAddPlan?: boolean }
  | { kind: "edit-account"; account: ProviderAccount }
  | { kind: "replace-credential"; account: ProviderAccount }
  | { kind: "delete-account"; account: ProviderAccount };

export function PlanStatusIndicator({ status }: { status: PlanStatusLabel | "Disabled" }) {
  const dotColor =
    status === "Enabled"
      ? "bg-success"
      : status === "Account disabled"
        ? "bg-warning"
        : "bg-muted-foreground";
  return (
    <span className="inline-flex items-center gap-2 text-sm">
      <span aria-hidden="true" className={`h-2 w-2 shrink-0 rounded-full ${dotColor}`} />
      <span>{status}</span>
    </span>
  );
}

function ProviderMark({ type, types }: { type: string; types: ProviderType[] }) {
  const mark = providerShortMark(type);
  const label = formatProviderName(type, types);
  return (
    <span
      aria-hidden="true"
      title={label}
      className={`inline-flex h-5 min-w-5 shrink-0 items-center justify-center rounded px-1 text-[11px] font-semibold ${mark.className}`}
    >
      {mark.text}
    </span>
  );
}

export function ProvidersModule({ view }: { view?: "plans" | "accounts" } = {}) {
  const { selectedOrganization } = useAuth();
  const organizationId = selectedOrganization?.id;
  const location = useLocation();
  const navigate = useNavigate();

  const activeView: "plans" | "accounts" =
    view ?? (location.pathname.startsWith("/plans/accounts") ? "accounts" : "plans");

  const canManage = ["owner", "admin"].includes(
    selectedOrganization?.role?.toLowerCase() ?? "",
  );

  const currentOrgRef = useRef(organizationId);
  currentOrgRef.current = organizationId;
  const activeFetchRef = useRef<AbortController | null>(null);

  const addPlanButtonRef = useRef<HTMLButtonElement>(null);
  const addAccountButtonRef = useRef<HTMLButtonElement>(null);
  const dialogTriggerRef = useRef<HTMLElement | null>(null);

  const [plansQuery, setPlansQuery] = useCollectionQuery(
    `${organizationId ?? "anonymous"}:plans`,
    "name",
    "asc",
  );
  const [accountsQuery, setAccountsQuery] = useCollectionQuery(
    `${organizationId ?? "anonymous"}:provider-accounts`,
    "name",
    "asc",
  );

  const [accounts, setAccounts] = useState<ProviderAccount[]>([]);
  const [plans, setPlans] = useState<ProviderPlan[]>([]);
  const [types, setTypes] = useState<ProviderType[]>([]);
  const [loading, setLoading] = useState(Boolean(organizationId));
  const [hasLoaded, setHasLoaded] = useState(false);
  const [readError, setReadError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [conflictError, setConflictError] = useState<string | null>(null);
  const [hiddenPlanNotice, setHiddenPlanNotice] = useState<string | null>(null);
  const [editor, setEditor] = useState<EditorState | null>(null);
  const [dialogBusy, setDialogBusy] = useState(false);
  const [pendingPlanIds, setPendingPlanIds] = useState<Record<string, string>>({});
  const [pendingAccountIds, setPendingAccountIds] = useState<Record<string, string>>({});

  const refresh = useCallback(
    async (externalSignal?: AbortSignal): Promise<{
      accounts: ProviderAccount[];
      plans: ProviderPlan[];
      types: ProviderType[];
    } | null> => {
      if (!organizationId) {
        setLoading(false);
        return null;
      }
      const reqOrg = organizationId;
      activeFetchRef.current?.abort();
      const controller = new AbortController();
      activeFetchRef.current = controller;
      if (externalSignal) {
        if (externalSignal.aborted) {
          controller.abort();
          return null;
        }
        externalSignal.addEventListener("abort", () => controller.abort(), { once: true });
      }

      setLoading(true);
      setReadError(null);
      try {
        const [nextAccounts, nextPlans, nextTypes] = await Promise.all([
          listProviders(reqOrg, controller.signal),
          listPlans(reqOrg, controller.signal),
          listProviderTypes(controller.signal),
        ]);
        if (!controller.signal.aborted && currentOrgRef.current === reqOrg) {
          setAccounts(nextAccounts);
          setPlans(nextPlans);
          setTypes(nextTypes);
          setHasLoaded(true);
          return { accounts: nextAccounts, plans: nextPlans, types: nextTypes };
        }
        return null;
      } catch (err) {
        if (!controller.signal.aborted && currentOrgRef.current === reqOrg) {
          setReadError(getErrorMessage(err, "Unable to load plans."));
        }
        return null;
      } finally {
        if (!controller.signal.aborted && currentOrgRef.current === reqOrg) {
          setLoading(false);
        }
      }
    },
    [organizationId],
  );

  useEffect(() => {
    activeFetchRef.current?.abort();
    setAccounts([]);
    setPlans([]);
    setTypes([]);
    setHasLoaded(false);
    setReadError(null);
    setActionError(null);
    setFormError(null);
    setConflictError(null);
    setHiddenPlanNotice(null);
    setEditor(null);
    setDialogBusy(false);
    setPendingPlanIds({});
    setPendingAccountIds({});
    if (!organizationId) {
      setLoading(false);
      return;
    }
    const controller = new AbortController();
    void refresh(controller.signal);
    return () => controller.abort();
  }, [organizationId, refresh]);

  const openEditor = useCallback((next: EditorState, trigger?: HTMLElement | null) => {
    dialogTriggerRef.current =
      trigger ??
      (document.activeElement instanceof HTMLElement ? document.activeElement : null);
    setFormError(null);
    setConflictError(null);
    setEditor(next);
  }, []);

  const closeEditor = useCallback(() => {
    if (dialogBusy) return;
    setEditor(null);
    setFormError(null);
    setConflictError(null);
  }, [dialogBusy]);

  const providerFilterOptions = useMemo(() => {
    const knownTypes = types.length
      ? types.map((t) => ({ value: t.type, label: t.name }))
      : [
          { value: "hetzner", label: "Hetzner" },
          { value: "digital-ocean", label: "DigitalOcean" },
          { value: "vultr", label: "Vultr" },
        ];
    return knownTypes;
  }, [types]);

  const accountFilterOptions = useMemo(() => {
    const map = new Map<string, string>();
    for (const account of accounts) {
      map.set(account.id, account.name);
    }
    for (const plan of plans) {
      if (!map.has(plan.providerAccountId)) {
        map.set(plan.providerAccountId, plan.providerName);
      }
    }
    return Array.from(map.entries()).map(([value, label]) => ({ value, label }));
  }, [accounts, plans]);

  const filteredPlans = useMemo(() => {
    return plans
      .filter((plan) => matchesPlanQuery(plan, plansQuery, accounts, types))
      .sort((a, b) => {
        const sortField = plansQuery.sort;
        const valA =
          sortField === "providerAccount"
            ? resolveAccountName(a, accounts)
            : sortField === "location"
              ? a.region
              : sortField === "serverType"
                ? a.sku
                : sortField === "status"
                  ? resolvePlanStatus(a, accounts)
                  : a.name;
        const valB =
          sortField === "providerAccount"
            ? resolveAccountName(b, accounts)
            : sortField === "location"
              ? b.region
              : sortField === "serverType"
                ? b.sku
                : sortField === "status"
                  ? resolvePlanStatus(b, accounts)
                  : b.name;
        return compareRecords(valA, valB, plansQuery.direction, a.id, b.id);
      });
  }, [plans, plansQuery, accounts, types]);

  const filteredAccounts = useMemo(() => {
    const search = accountsQuery.search.trim().toLowerCase();
    const f = accountsQuery.filters;
    return accounts
      .filter((account) => {
        const providerLabel = formatProviderName(account.providerType, types);
        const statusLabel = account.enabled ? "Enabled" : "Disabled";
        const validationLabel = formatValidationStatus(account.validationStatus);
        if (search) {
          const haystack = [
            account.name,
            account.providerType,
            providerLabel,
            statusLabel,
            validationLabel,
            account.projectId ?? "",
          ]
            .join(" ")
            .toLowerCase();
          if (!haystack.includes(search)) return false;
        }
        if (f.provider && account.providerType !== f.provider) return false;
        if (f.status && statusLabel !== f.status) return false;
        if (f.connection && account.validationStatus !== f.connection) return false;
        return true;
      })
      .sort((a, b) => {
        const sortField = accountsQuery.sort;
        const valA =
          sortField === "provider"
            ? formatProviderName(a.providerType, types)
            : sortField === "connection"
              ? formatValidationStatus(a.validationStatus)
              : sortField === "status"
                ? a.enabled
                  ? "Enabled"
                  : "Disabled"
                : sortField === "planCount"
                  ? a.planCount
                  : a.name;
        const valB =
          sortField === "provider"
            ? formatProviderName(b.providerType, types)
            : sortField === "connection"
              ? formatValidationStatus(b.validationStatus)
              : sortField === "status"
                ? b.enabled
                  ? "Enabled"
                  : "Disabled"
                : sortField === "planCount"
                  ? b.planCount
                  : b.name;
        return compareRecords(valA, valB, accountsQuery.direction, a.id, b.id);
      });
  }, [accounts, accountsQuery, types]);

  if (!view && location.pathname === "/providers") {
    return <Navigate to="/plans" replace />;
  }

  const enabledAccounts = accounts.filter((item) => item.enabled);

  async function handleTogglePlanEnabled(plan: ProviderPlan) {
    if (!organizationId || pendingPlanIds[plan.id]) return;
    const reqOrg = organizationId;
    setActionError(null);
    setPendingPlanIds((prev) => ({ ...prev, [plan.id]: "Saving…" }));
    try {
      await updatePlan(plan.id, { enabled: !plan.enabled, revision: plan.revision });
      if (currentOrgRef.current === reqOrg) {
        await refresh();
      }
    } catch (err) {
      if (currentOrgRef.current === reqOrg) {
        setActionError(getErrorMessage(err, "Unable to update plan."));
      }
    } finally {
      if (currentOrgRef.current === reqOrg) {
        setPendingPlanIds((prev) => {
          const next = { ...prev };
          delete next[plan.id];
          return next;
        });
      }
    }
  }

  async function handleCheckAccountConnection(account: ProviderAccount) {
    if (!organizationId || pendingAccountIds[account.id]) return;
    const reqOrg = organizationId;
    setActionError(null);
    setPendingAccountIds((prev) => ({ ...prev, [account.id]: "Checking…" }));
    try {
      await validateProvider(account.id);
      if (currentOrgRef.current === reqOrg) {
        await refresh();
      }
    } catch (err) {
      if (currentOrgRef.current === reqOrg) {
        setActionError(getErrorMessage(err, "Unable to validate provider connection."));
      }
    } finally {
      if (currentOrgRef.current === reqOrg) {
        setPendingAccountIds((prev) => {
          const next = { ...prev };
          delete next[account.id];
          return next;
        });
      }
    }
  }

  async function handleToggleAccountEnabled(account: ProviderAccount) {
    if (!organizationId || pendingAccountIds[account.id]) return;
    const reqOrg = organizationId;
    setActionError(null);
    setPendingAccountIds((prev) => ({ ...prev, [account.id]: "Saving…" }));
    try {
      await updateProvider(account.id, {
        enabled: !account.enabled,
        revision: account.revision,
      });
      if (currentOrgRef.current === reqOrg) {
        await refresh();
      }
    } catch (err) {
      if (currentOrgRef.current === reqOrg) {
        setActionError(getErrorMessage(err, "Unable to update provider account."));
      }
    } finally {
      if (currentOrgRef.current === reqOrg) {
        setPendingAccountIds((prev) => {
          const next = { ...prev };
          delete next[account.id];
          return next;
        });
      }
    }
  }

  async function handleSavePlan(
    mode: "add" | "duplicate" | "edit",
    targetPlan: ProviderPlan | undefined,
    input: PlanInput & { providerAccountId: string },
  ) {
    if (!organizationId) return;
    const reqOrg = organizationId;
    setDialogBusy(true);
    setFormError(null);
    setConflictError(null);
    try {
      if (mode === "edit" && targetPlan) {
        const payload = targetPlan.provisioningLocked
          ? { name: input.name, revision: targetPlan.revision }
          : {
              name: input.name,
              region: input.region,
              sku: input.sku,
              image: input.image,
              buildPlatform: input.buildPlatform,
              revision: targetPlan.revision,
            };
        await updatePlan(targetPlan.id, payload);
        if (currentOrgRef.current !== reqOrg) return;
        setEditor(null);
        await refresh();
      } else {
        const created = await createPlan({
          organizationId: reqOrg,
          providerAccountId: input.providerAccountId,
          name: input.name,
          region: input.region,
          sku: input.sku,
          image: input.image,
          buildPlatform: input.buildPlatform,
        });
        if (currentOrgRef.current !== reqOrg) return;
        setEditor(null);
        const refreshed = await refresh();
        const latestAccounts = refreshed?.accounts ?? accounts;
        const latestTypes = refreshed?.types ?? types;
        const effectivePlan: ProviderPlan =
          refreshed?.plans.find((p) => p.id === created.id || p.name === created.name) ?? created;
        if (!matchesPlanQuery(effectivePlan, plansQuery, latestAccounts, latestTypes)) {
          setHiddenPlanNotice(effectivePlan.name || input.name || "New plan");
        } else {
          setHiddenPlanNotice(null);
        }
      }
    } catch (err) {
      if (currentOrgRef.current !== reqOrg) return;
      if (mode === "edit" && isConflictOrLockError(err)) {
        setConflictError(
          getErrorMessage(
            err,
            "This plan changed while you were editing it. Reload current data before saving.",
          ),
        );
      } else {
        setFormError(getErrorMessage(err, "Unable to save plan."));
      }
    } finally {
      if (currentOrgRef.current === reqOrg) {
        setDialogBusy(false);
      }
    }
  }

  async function handleReloadConflictingPlan(planId: string): Promise<ProviderPlan | null> {
    if (!organizationId) return null;
    const reqOrg = organizationId;
    setDialogBusy(true);
    try {
      const refreshed = await refresh();
      if (currentOrgRef.current !== reqOrg || !refreshed) return null;
      const latest = refreshed.plans.find((p) => p.id === planId) ?? null;
      if (latest) {
        setEditor({ kind: "edit-plan", plan: latest });
        setConflictError(null);
        setFormError(null);
      } else {
        setFormError("This plan was removed. Close the dialog to view current plans.");
      }
      return latest;
    } finally {
      if (currentOrgRef.current === reqOrg) {
        setDialogBusy(false);
      }
    }
  }

  async function handleReloadConflictingAccount(
    accountId: string,
  ): Promise<ProviderAccount | null> {
    if (!organizationId) return null;
    const reqOrg = organizationId;
    setDialogBusy(true);
    try {
      const refreshed = await refresh();
      if (currentOrgRef.current !== reqOrg || !refreshed) return null;
      const latest = refreshed.accounts.find((a) => a.id === accountId) ?? null;
      if (latest) {
        setEditor({ kind: "edit-account", account: latest });
        setConflictError(null);
        setFormError(null);
      } else {
        setFormError("This provider account was removed.");
      }
      return latest;
    } finally {
      if (currentOrgRef.current === reqOrg) {
        setDialogBusy(false);
      }
    }
  }

  async function handleSaveAccount(
    existing: ProviderAccount | undefined,
    resumeAddPlan: boolean | undefined,
    input: {
      name: string;
      providerType: string;
      credential: string;
      projectId?: string;
      defaultOsId?: number;
    },
  ) {
    if (!organizationId) return;
    const reqOrg = organizationId;
    setDialogBusy(true);
    setFormError(null);
    setConflictError(null);
    try {
      if (existing) {
        await updateProvider(existing.id, {
          name: input.name,
          revision: existing.revision,
        });
        if (currentOrgRef.current !== reqOrg) return;
        setEditor(null);
        await refresh();
      } else {
        const created = await createProvider({
          organizationId: reqOrg,
          name: input.name,
          providerType: input.providerType,
          credential: input.credential,
          projectId: input.projectId,
          defaultOsId: input.defaultOsId,
        });
        if (currentOrgRef.current !== reqOrg) return;
        await refresh();
        if (resumeAddPlan) {
          setEditor({ kind: "add-plan", preferredAccountId: created.id });
        } else {
          setEditor(null);
        }
      }
    } catch (err) {
      if (currentOrgRef.current !== reqOrg) return;
      if (existing && isConflictOrLockError(err)) {
        setConflictError(
          getErrorMessage(
            err,
            "This provider account changed. Reload current data before saving.",
          ),
        );
      } else {
        setFormError(getErrorMessage(err, "Unable to save provider account."));
      }
    } finally {
      if (currentOrgRef.current === reqOrg) {
        setDialogBusy(false);
      }
    }
  }

  async function handleReplaceCredential(account: ProviderAccount, credential: string) {
    if (!organizationId) return;
    const reqOrg = organizationId;
    setDialogBusy(true);
    setFormError(null);
    try {
      await replaceProviderCredential(account.id, credential, account.revision);
      if (currentOrgRef.current !== reqOrg) return;
      setEditor(null);
      await refresh();
    } catch (err) {
      if (currentOrgRef.current !== reqOrg) return;
      setFormError(getErrorMessage(err, "Unable to replace credential."));
    } finally {
      if (currentOrgRef.current === reqOrg) {
        setDialogBusy(false);
      }
    }
  }

  async function handleDeletePlan(plan: ProviderPlan) {
    if (!organizationId) return;
    const reqOrg = organizationId;
    setDialogBusy(true);
    setFormError(null);
    try {
      await deletePlan(plan.id);
      if (currentOrgRef.current !== reqOrg) return;
      setEditor(null);
      await refresh();
    } catch (err) {
      if (currentOrgRef.current !== reqOrg) return;
      setFormError(getErrorMessage(err, "Unable to delete plan."));
    } finally {
      if (currentOrgRef.current === reqOrg) {
        setDialogBusy(false);
      }
    }
  }

  async function handleDeleteAccount(account: ProviderAccount) {
    if (!organizationId) return;
    const reqOrg = organizationId;
    setDialogBusy(true);
    setFormError(null);
    try {
      await deleteProvider(account.id);
      if (currentOrgRef.current !== reqOrg) return;
      setEditor(null);
      await refresh();
    } catch (err) {
      if (currentOrgRef.current !== reqOrg) return;
      setFormError(getErrorMessage(err, "Unable to delete provider account."));
    } finally {
      if (currentOrgRef.current === reqOrg) {
        setDialogBusy(false);
      }
    }
  }

  function handleViewPlansForAccount(account: ProviderAccount) {
    setPlansQuery({
      ...plansQuery,
      filters: {
        ...plansQuery.filters,
        account: account.id,
      },
    });
    navigate("/plans");
  }

  const hasActivePlanFilters =
    Boolean(plansQuery.search.trim()) ||
    Object.values(plansQuery.filters).some(Boolean);
  const hasActiveAccountFilters =
    Boolean(accountsQuery.search.trim()) ||
    Object.values(accountsQuery.filters).some(Boolean);

  return (
    <>
      {activeView === "plans" ? (
        <PageScaffold title="Plans">
          <CollectionToolbar
            label="plans"
            query={plansQuery}
            onChange={(next) => {
              setHiddenPlanNotice(null);
              setPlansQuery(next);
            }}
            onRefresh={() => refresh()}
            loading={loading}
            count={filteredPlans.length}
            filters={[
              {
                id: "provider",
                label: "Provider",
                options: providerFilterOptions,
              },
              {
                id: "account",
                label: "Provider account",
                options: accountFilterOptions,
              },
              {
                id: "status",
                label: "Status",
                options: options(["Enabled", "Disabled", "Account disabled"]),
              },
              {
                id: "placement",
                label: "Placement",
                options: [
                  { value: "locked", label: "Locked" },
                  { value: "unlocked", label: "Unlocked" },
                ],
              },
            ]}
            sorts={[
              { value: "name", label: "Name" },
              { value: "providerAccount", label: "Provider account" },
              { value: "location", label: "Location" },
              { value: "serverType", label: "Server type" },
              { value: "status", label: "Status" },
            ]}
            secondary={{
              id: "provider-accounts",
              label: "Provider accounts",
              onClick: () => navigate("/plans/accounts"),
            }}
            primary={{
              id: "add-plan",
              ref: addPlanButtonRef,
              label: "Add plan",
              icon: <Plus className="h-4 w-4" />,
              visible: canManage,
              disabled: !organizationId || (loading && !hasLoaded),
              onClick: () => openEditor({ kind: "add-plan" }, addPlanButtonRef.current),
            }}
          />

          {hiddenPlanNotice ? (
            <div
              role="status"
              className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-border bg-muted/40 px-4 py-2.5 text-sm"
            >
              <span>
                Plan <strong className="font-medium">{hiddenPlanNotice}</strong> was added,
                but is hidden by active filters.
              </span>
              <Button
                size="sm"
                variant="outline"
                onClick={() => {
                  setPlansQuery({ ...plansQuery, search: "", filters: {} });
                  setHiddenPlanNotice(null);
                }}
              >
                Clear filters
              </Button>
            </div>
          ) : null}

          {readError ? (
            <p role="alert" className="text-sm text-destructive">
              {readError}
            </p>
          ) : null}

          {actionError ? (
            <p role="alert" className="text-sm text-destructive">
              {actionError}
            </p>
          ) : null}

          {loading && !hasLoaded ? <p role="status">Loading plans…</p> : null}

          {!loading && !readError && hasLoaded && plans.length === 0 ? (
            <EmptyState
              title="No plans"
              action={
                canManage ? (
                  <Button
                    onClick={() =>
                      openEditor({ kind: "add-plan" }, addPlanButtonRef.current)
                    }
                  >
                    <Plus className="h-4 w-4" />
                    Add plan
                  </Button>
                ) : undefined
              }
            >
              {canManage
                ? "Create a named plan for your provider accounts to reference in the CLI and minicloud.yml."
                : "No deployment plans are configured yet. An organization owner or admin can connect a provider account and add plans."}
            </EmptyState>
          ) : null}

          {plans.length > 0 && filteredPlans.length === 0 ? (
            <Card>
              <CardContent className="flex flex-wrap items-center justify-between gap-3 py-6 text-sm text-muted-foreground">
                <span>
                  No plans match your search or filters. Adjust search or Filter &amp; sort.
                </span>
                {hasActivePlanFilters ? (
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() =>
                      setPlansQuery({ ...plansQuery, search: "", filters: {} })
                    }
                  >
                    Clear filters
                  </Button>
                ) : null}
              </CardContent>
            </Card>
          ) : null}

          {filteredPlans.length > 0 ? (
            <Card>
              <CardContent className="p-0">
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Name</TableHead>
                      <TableHead>Provider account</TableHead>
                      <TableHead>Location</TableHead>
                      <TableHead>Server type</TableHead>
                      <TableHead>Status</TableHead>
                      <TableHead className="text-right">Actions</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {filteredPlans.map((plan) => {
                      const accountName = resolveAccountName(plan, accounts);
                      const accountEnabled = resolveAccountEnabled(plan, accounts);
                      const status = resolvePlanStatus(plan, accounts);
                      const bothDisabled = !plan.enabled && !accountEnabled;
                      const pendingLabel = pendingPlanIds[plan.id];
                      const rowBusy = Boolean(pendingLabel) || dialogBusy;

                      return (
                        <TableRow key={plan.id}>
                          <TableCell data-label="Name" className="font-medium">
                            <div className="flex flex-wrap items-center gap-1.5 break-words">
                              <span className="break-words">{plan.name}</span>
                              {plan.provisioningLocked ? (
                                <span
                                  className="inline-flex items-center gap-1 text-xs font-normal text-muted-foreground"
                                  aria-label="Placement locked"
                                >
                                  <Lock className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
                                  <span className="sr-only">Placement locked</span>
                                </span>
                              ) : null}
                            </div>
                          </TableCell>
                          <TableCell data-label="Provider account">
                            <div className="flex flex-wrap items-center gap-2 break-words">
                              <ProviderMark type={plan.providerType} types={types} />
                              <span className="break-words">{accountName}</span>
                              <span className="sr-only">
                                ({formatProviderName(plan.providerType, types)})
                              </span>
                            </div>
                          </TableCell>
                          <TableCell data-label="Location">{plan.region}</TableCell>
                          <TableCell data-label="Server type">{plan.sku}</TableCell>
                          <TableCell data-label="Status">
                            <div className="flex flex-wrap items-center gap-2">
                              {bothDisabled ? (
                                <Disclosure
                                  label={<PlanStatusIndicator status="Disabled" />}
                                >
                                  Account disabled
                                </Disclosure>
                              ) : (
                                <PlanStatusIndicator status={status} />
                              )}
                              {pendingLabel ? (
                                <span
                                  role="status"
                                  className="text-xs text-muted-foreground"
                                >
                                  {pendingLabel}
                                </span>
                              ) : null}
                            </div>
                          </TableCell>
                          <TableCell data-label="Actions" className="text-right">
                            {canManage ? (
                              <div className="flex flex-wrap items-center justify-end gap-1.5">
                                <Button
                                  size="sm"
                                  variant="outline"
                                  disabled={rowBusy}
                                  onClick={(event) =>
                                    openEditor(
                                      { kind: "edit-plan", plan },
                                      event.currentTarget,
                                    )
                                  }
                                >
                                  Edit
                                </Button>
                                <DropdownMenu>
                                  <DropdownMenuTrigger asChild>
                                    <Button
                                      size="icon"
                                      variant="outline"
                                      className="h-8 w-8"
                                      aria-label={`Actions for ${plan.name}`}
                                      disabled={rowBusy}
                                    >
                                      <MoreHorizontal className="h-4 w-4" />
                                    </Button>
                                  </DropdownMenuTrigger>
                                  <DropdownMenuContent align="end">
                                    <DropdownMenuItem
                                      disabled={rowBusy}
                                      onSelect={() =>
                                        openEditor({
                                          kind: "duplicate-plan",
                                          sourcePlan: plan,
                                        })
                                      }
                                    >
                                      Duplicate plan
                                    </DropdownMenuItem>
                                    <DropdownMenuItem
                                      disabled={rowBusy}
                                      onSelect={() => void handleTogglePlanEnabled(plan)}
                                    >
                                      {plan.enabled ? "Disable plan" : "Enable plan"}
                                    </DropdownMenuItem>
                                    <DropdownMenuSeparator />
                                    <DropdownMenuItem
                                      className="text-destructive focus:text-destructive"
                                      disabled={rowBusy}
                                      onSelect={() =>
                                        openEditor({ kind: "delete-plan", plan })
                                      }
                                    >
                                      Delete plan
                                    </DropdownMenuItem>
                                  </DropdownMenuContent>
                                </DropdownMenu>
                              </div>
                            ) : null}
                          </TableCell>
                        </TableRow>
                      );
                    })}
                  </TableBody>
                </Table>
                <div className="border-t px-4 py-2.5 text-xs text-muted-foreground">
                  {hasActivePlanFilters || filteredPlans.length !== plans.length
                    ? `${filteredPlans.length} of ${plans.length} plans`
                    : `${plans.length} ${plans.length === 1 ? "plan" : "plans"}`}
                </div>
              </CardContent>
            </Card>
          ) : null}
        </PageScaffold>
      ) : (
        <PageScaffold
          breadcrumbs={[
            { label: "Plans", to: "/plans" },
            { label: "Provider accounts" },
          ]}
          title="Provider accounts"
        >
          <CollectionToolbar
            label="provider accounts"
            query={accountsQuery}
            onChange={setAccountsQuery}
            onRefresh={() => refresh()}
            loading={loading}
            count={filteredAccounts.length}
            filters={[
              {
                id: "provider",
                label: "Provider",
                options: providerFilterOptions,
              },
              {
                id: "status",
                label: "Enabled state",
                options: options(["Enabled", "Disabled"]),
              },
              {
                id: "connection",
                label: "Connection",
                options: options(
                  Array.from(
                    new Set(accounts.map((item) => item.validationStatus).filter(Boolean)),
                  ),
                ),
              },
            ]}
            sorts={[
              { value: "name", label: "Name" },
              { value: "provider", label: "Provider" },
              { value: "connection", label: "Connection" },
              { value: "status", label: "Enabled state" },
              { value: "planCount", label: "Plan count" },
            ]}
            secondary={{
              id: "back-to-plans",
              label: "Back to plans",
              onClick: () => navigate("/plans"),
            }}
            primary={{
              id: "add-account",
              ref: addAccountButtonRef,
              label: "Add account",
              icon: <Plus className="h-4 w-4" />,
              visible: canManage,
              disabled: !organizationId || (loading && !hasLoaded),
              onClick: () =>
                openEditor({ kind: "add-account" }, addAccountButtonRef.current),
            }}
          />

          {readError ? (
            <p role="alert" className="text-sm text-destructive">
              {readError}
            </p>
          ) : null}

          {actionError ? (
            <p role="alert" className="text-sm text-destructive">
              {actionError}
            </p>
          ) : null}

          {loading && !hasLoaded ? (
            <p role="status">Loading provider accounts…</p>
          ) : null}

          {!loading && !readError && hasLoaded && accounts.length === 0 ? (
            <EmptyState
              title="No provider accounts"
              action={
                canManage ? (
                  <Button
                    onClick={() =>
                      openEditor({ kind: "add-account" }, addAccountButtonRef.current)
                    }
                  >
                    <Plus className="h-4 w-4" />
                    Add account
                  </Button>
                ) : undefined
              }
            >
              {canManage
                ? "Connect a Hetzner, DigitalOcean, or Vultr account to create named deployment plans."
                : "No provider accounts are configured yet. An organization owner or admin can connect provider accounts."}
            </EmptyState>
          ) : null}

          {accounts.length > 0 && filteredAccounts.length === 0 ? (
            <Card>
              <CardContent className="flex flex-wrap items-center justify-between gap-3 py-6 text-sm text-muted-foreground">
                <span>
                  No provider accounts match your search or filters. Adjust search or
                  Filter &amp; sort.
                </span>
                {hasActiveAccountFilters ? (
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() =>
                      setAccountsQuery({
                        ...accountsQuery,
                        search: "",
                        filters: {},
                      })
                    }
                  >
                    Clear filters
                  </Button>
                ) : null}
              </CardContent>
            </Card>
          ) : null}

          {filteredAccounts.length > 0 ? (
            <Card>
              <CardContent className="p-0">
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Name</TableHead>
                      <TableHead>Provider</TableHead>
                      <TableHead>Connection</TableHead>
                      <TableHead>Enabled state</TableHead>
                      <TableHead>Plan count</TableHead>
                      <TableHead className="text-right">Actions</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {filteredAccounts.map((account) => {
                      const pendingLabel = pendingAccountIds[account.id];
                      const rowBusy = Boolean(pendingLabel) || dialogBusy;
                      const connectionStatus = formatValidationStatus(
                        account.validationStatus,
                      );
                      const fullTime = account.lastValidatedAt
                        ? formatFullTimestamp(account.lastValidatedAt)
                        : null;
                      const relativeTime = account.lastValidatedAt
                        ? formatRelativeTime(account.lastValidatedAt)
                        : null;

                      return (
                        <TableRow key={account.id}>
                          <TableCell data-label="Name" className="font-medium">
                            <div className="space-y-0.5 break-words">
                              <div>{account.name}</div>
                              {account.projectId ? (
                                <div className="text-xs font-normal text-muted-foreground">
                                  Project: {account.projectId}
                                </div>
                              ) : null}
                            </div>
                          </TableCell>
                          <TableCell data-label="Provider">
                            <div className="flex items-center gap-2">
                              <ProviderMark type={account.providerType} types={types} />
                              <span>
                                {formatProviderName(account.providerType, types)}
                              </span>
                            </div>
                          </TableCell>
                          <TableCell data-label="Connection">
                            {pendingLabel === "Checking…" ? (
                              <span role="status" className="text-sm text-muted-foreground">
                                Checking…
                              </span>
                            ) : account.lastValidatedAt && fullTime && relativeTime ? (
                              <Disclosure
                                label={`${connectionStatus} · Checked ${relativeTime}`}
                              >
                                <time dateTime={account.lastValidatedAt}>
                                  Checked {fullTime}
                                </time>
                              </Disclosure>
                            ) : (
                              <span className="text-sm text-muted-foreground">
                                {connectionStatus} · Not checked yet
                              </span>
                            )}
                          </TableCell>
                          <TableCell data-label="Enabled state">
                            <div className="flex flex-wrap items-center gap-2">
                              <PlanStatusIndicator
                                status={account.enabled ? "Enabled" : "Disabled"}
                              />
                              {pendingLabel && pendingLabel !== "Checking…" ? (
                                <span
                                  role="status"
                                  className="text-xs text-muted-foreground"
                                >
                                  {pendingLabel}
                                </span>
                              ) : null}
                            </div>
                          </TableCell>
                          <TableCell data-label="Plan count">
                            {account.planCount}{" "}
                            {account.planCount === 1 ? "plan" : "plans"}
                          </TableCell>
                          <TableCell data-label="Actions" className="text-right">
                            <div className="flex flex-wrap items-center justify-end gap-1.5">
                              {canManage ? (
                                <>
                                  <Button
                                    size="sm"
                                    variant="outline"
                                    disabled={rowBusy}
                                    onClick={(event) =>
                                      openEditor(
                                        { kind: "edit-account", account },
                                        event.currentTarget,
                                      )
                                    }
                                  >
                                    Edit
                                  </Button>
                                  <DropdownMenu>
                                    <DropdownMenuTrigger asChild>
                                      <Button
                                        size="icon"
                                        variant="outline"
                                        className="h-8 w-8"
                                        aria-label={`Actions for ${account.name}`}
                                        disabled={rowBusy}
                                      >
                                        <MoreHorizontal className="h-4 w-4" />
                                      </Button>
                                    </DropdownMenuTrigger>
                                    <DropdownMenuContent align="end">
                                      <DropdownMenuItem
                                        disabled={rowBusy}
                                        onSelect={() =>
                                          void handleCheckAccountConnection(account)
                                        }
                                      >
                                        Check connection
                                      </DropdownMenuItem>
                                      <DropdownMenuItem
                                        disabled={rowBusy}
                                        onSelect={() =>
                                          openEditor({
                                            kind: "replace-credential",
                                            account,
                                          })
                                        }
                                      >
                                        Replace credential
                                      </DropdownMenuItem>
                                      <DropdownMenuItem
                                        disabled={rowBusy}
                                        onSelect={() =>
                                          void handleToggleAccountEnabled(account)
                                        }
                                      >
                                        {account.enabled
                                          ? "Disable account"
                                          : "Enable account"}
                                      </DropdownMenuItem>
                                      <DropdownMenuItem
                                        disabled={rowBusy}
                                        onSelect={() =>
                                          handleViewPlansForAccount(account)
                                        }
                                      >
                                        View plans
                                      </DropdownMenuItem>
                                      <DropdownMenuSeparator />
                                      <DropdownMenuItem
                                        className="text-destructive focus:text-destructive"
                                        disabled={rowBusy}
                                        onSelect={() =>
                                          openEditor({
                                            kind: "delete-account",
                                            account,
                                          })
                                        }
                                      >
                                        Delete account
                                      </DropdownMenuItem>
                                    </DropdownMenuContent>
                                  </DropdownMenu>
                                </>
                              ) : (
                                <Button
                                  size="sm"
                                  variant="outline"
                                  onClick={() => handleViewPlansForAccount(account)}
                                >
                                  View plans
                                </Button>
                              )}
                            </div>
                          </TableCell>
                        </TableRow>
                      );
                    })}
                  </TableBody>
                </Table>
                <div className="border-t px-4 py-2.5 text-xs text-muted-foreground">
                  {hasActiveAccountFilters ||
                  filteredAccounts.length !== accounts.length
                    ? `${filteredAccounts.length} of ${accounts.length} provider accounts`
                    : `${accounts.length} ${accounts.length === 1 ? "provider account" : "provider accounts"}`}
                </div>
              </CardContent>
            </Card>
          ) : null}
        </PageScaffold>
      )}

      <Dialog
        open={Boolean(editor)}
        onOpenChange={(open) => {
          if (!open) closeEditor();
        }}
      >
        <DialogContent
          className="max-h-[90vh] overflow-y-auto"
          onCloseAutoFocus={(event) => {
            if (
              dialogTriggerRef.current &&
              document.body.contains(dialogTriggerRef.current)
            ) {
              event.preventDefault();
              dialogTriggerRef.current.focus();
            }
          }}
        >
          {editor?.kind === "add-plan" && enabledAccounts.length === 0 ? (
            <>
              <DialogHeader>
                <DialogTitle>Add plan</DialogTitle>
                <DialogDescription>
                  Connect an enabled provider account before adding a deployment plan.
                </DialogDescription>
              </DialogHeader>
              <div className="space-y-3 text-sm text-muted-foreground">
                <p>
                  {accounts.length > 0
                    ? "All configured provider accounts are currently disabled. Connect a new provider account or enable an existing account to create a plan."
                    : "No provider accounts are connected to this organization yet. Connect a Hetzner, DigitalOcean, or Vultr account first."}
                </p>
              </div>
              <DialogFooter className="gap-2 sm:justify-end">
                <Button
                  type="button"
                  variant="outline"
                  disabled={dialogBusy}
                  onClick={closeEditor}
                >
                  Cancel
                </Button>
                {accounts.length > 0 ? (
                  <Button
                    type="button"
                    variant="outline"
                    disabled={dialogBusy}
                    onClick={() => {
                      closeEditor();
                      navigate("/plans/accounts");
                    }}
                  >
                    Provider accounts
                  </Button>
                ) : null}
                {canManage ? (
                  <Button
                    type="button"
                    disabled={dialogBusy}
                    onClick={() => {
                      setFormError(null);
                      setEditor({ kind: "add-account", resumeAddPlan: true });
                    }}
                  >
                    Connect account
                  </Button>
                ) : null}
              </DialogFooter>
            </>
          ) : null}

          {(editor?.kind === "add-plan" && enabledAccounts.length > 0) ||
          editor?.kind === "duplicate-plan" ||
          editor?.kind === "edit-plan" ? (
            <>
              <DialogHeader>
                <DialogTitle>
                  {editor.kind === "edit-plan"
                    ? "Edit plan"
                    : editor.kind === "duplicate-plan"
                      ? "Duplicate plan"
                      : "Add plan"}
                </DialogTitle>
                <DialogDescription>
                  {editor.kind === "edit-plan"
                    ? "Update the plan configuration."
                    : "Reference the plan name in the CLI or minicloud.yml. Names are unique across this organization."}
                </DialogDescription>
              </DialogHeader>
              {formError ? (
                <p role="alert" className="text-sm text-destructive">
                  {formError}
                </p>
              ) : null}
              <PlanDialogForm
                key={
                  editor.kind === "edit-plan"
                    ? `edit-${editor.plan.id}`
                    : editor.kind === "duplicate-plan"
                      ? `dup-${editor.sourcePlan.id}`
                      : `add-${editor.preferredAccountId ?? "default"}`
                }
                mode={
                  editor.kind === "edit-plan"
                    ? "edit"
                    : editor.kind === "duplicate-plan"
                      ? "duplicate"
                      : "add"
                }
                plan={
                  editor.kind === "edit-plan"
                    ? editor.plan
                    : editor.kind === "duplicate-plan"
                      ? editor.sourcePlan
                      : undefined
                }
                preferredAccountId={
                  editor.kind === "add-plan" ? editor.preferredAccountId : undefined
                }
                accounts={accounts}
                types={types}
                busy={dialogBusy}
                conflictError={conflictError}
                onReloadConflict={
                  editor.kind === "edit-plan"
                    ? () => handleReloadConflictingPlan(editor.plan.id)
                    : undefined
                }
                onSwitchToDuplicate={(planToDuplicate) => {
                  setFormError(null);
                  setConflictError(null);
                  setEditor({ kind: "duplicate-plan", sourcePlan: planToDuplicate });
                }}
                onCancel={closeEditor}
                onSave={(input) =>
                  handleSavePlan(
                    editor.kind === "edit-plan"
                      ? "edit"
                      : editor.kind === "duplicate-plan"
                        ? "duplicate"
                        : "add",
                    editor.kind === "edit-plan" ? editor.plan : undefined,
                    input,
                  )
                }
              />
            </>
          ) : null}

          {editor?.kind === "delete-plan" ? (
            <>
              <DialogHeader>
                <DialogTitle>Delete plan?</DialogTitle>
                <DialogDescription>
                  Delete plan <strong className="font-medium text-foreground">{editor.plan.name}</strong>?
                  This removes the plan configuration from Minicloud and does not delete cloud
                  servers, volumes, or running services. Referenced plans cannot be removed.
                </DialogDescription>
              </DialogHeader>
              {formError ? (
                <div className="space-y-2">
                  <p role="alert" className="text-sm text-destructive">
                    {formError}
                  </p>
                  {editor.plan.enabled ? (
                    <div className="flex items-center justify-between gap-2 rounded-md border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
                      <span>
                        Disable this plan to prevent new deployments from selecting it.
                      </span>
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        disabled={dialogBusy}
                        onClick={() => {
                          const target = editor.plan;
                          closeEditor();
                          void handleTogglePlanEnabled(target);
                        }}
                      >
                        Disable plan
                      </Button>
                    </div>
                  ) : null}
                </div>
              ) : null}
              <DialogFooter className="gap-2 sm:justify-end">
                <Button
                  type="button"
                  variant="outline"
                  disabled={dialogBusy}
                  onClick={closeEditor}
                >
                  Cancel
                </Button>
                <Button
                  type="button"
                  variant="destructive"
                  disabled={dialogBusy}
                  onClick={() => void handleDeletePlan(editor.plan)}
                >
                  {dialogBusy ? "Saving…" : "Delete"}
                </Button>
              </DialogFooter>
            </>
          ) : null}

          {editor?.kind === "add-account" || editor?.kind === "edit-account" ? (
            <>
              <DialogHeader>
                <DialogTitle>
                  {editor.kind === "edit-account" ? "Edit account" : "Add account"}
                </DialogTitle>
                <DialogDescription>
                  Credentials are stored in the secret store and cannot be read back.
                </DialogDescription>
              </DialogHeader>
              {formError ? (
                <p role="alert" className="text-sm text-destructive">
                  {formError}
                </p>
              ) : null}
              <AccountDialogForm
                key={
                  editor.kind === "edit-account"
                    ? `edit-account-${editor.account.id}`
                    : "add-account"
                }
                account={
                  editor.kind === "edit-account" ? editor.account : undefined
                }
                types={types}
                busy={dialogBusy}
                conflictError={conflictError}
                onReloadConflict={
                  editor.kind === "edit-account"
                    ? () => handleReloadConflictingAccount(editor.account.id)
                    : undefined
                }
                onCancel={closeEditor}
                onSave={(input) =>
                  handleSaveAccount(
                    editor.kind === "edit-account" ? editor.account : undefined,
                    editor.kind === "add-account" ? editor.resumeAddPlan : false,
                    input,
                  )
                }
              />
            </>
          ) : null}

          {editor?.kind === "replace-credential" ? (
            <>
              <DialogHeader>
                <DialogTitle>Replace credential</DialogTitle>
                <DialogDescription>
                  Credentials are stored in the secret store and cannot be read back.
                </DialogDescription>
              </DialogHeader>
              {formError ? (
                <p role="alert" className="text-sm text-destructive">
                  {formError}
                </p>
              ) : null}
              <CredentialDialogForm
                accountName={editor.account.name}
                busy={dialogBusy}
                onCancel={closeEditor}
                onSave={(credential) =>
                  handleReplaceCredential(editor.account, credential)
                }
              />
            </>
          ) : null}

          {editor?.kind === "delete-account" ? (
            <>
              <DialogHeader>
                <DialogTitle>Delete account?</DialogTitle>
                <DialogDescription>
                  Delete provider account{" "}
                  <strong className="font-medium text-foreground">
                    {editor.account.name}
                  </strong>
                  ? This removes the account configuration and does not delete cloud
                  servers, volumes, or services. Referenced accounts cannot be removed.
                </DialogDescription>
              </DialogHeader>
              {formError ? (
                <div className="space-y-2">
                  <p role="alert" className="text-sm text-destructive">
                    {formError}
                  </p>
                  {editor.account.enabled ? (
                    <div className="flex items-center justify-between gap-2 rounded-md border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
                      <span>
                        Disable this account to block new resource allocations.
                      </span>
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        disabled={dialogBusy}
                        onClick={() => {
                          const target = editor.account;
                          closeEditor();
                          void handleToggleAccountEnabled(target);
                        }}
                      >
                        Disable account
                      </Button>
                    </div>
                  ) : null}
                </div>
              ) : null}
              <DialogFooter className="gap-2 sm:justify-end">
                <Button
                  type="button"
                  variant="outline"
                  disabled={dialogBusy}
                  onClick={closeEditor}
                >
                  Cancel
                </Button>
                <Button
                  type="button"
                  variant="destructive"
                  disabled={dialogBusy}
                  onClick={() => void handleDeleteAccount(editor.account)}
                >
                  {dialogBusy ? "Saving…" : "Delete"}
                </Button>
              </DialogFooter>
            </>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}

export function PlansModule() {
  return <ProvidersModule view="plans" />;
}

export function ProviderAccountsModule() {
  return <ProvidersModule view="accounts" />;
}

function PlanDialogForm({
  mode,
  plan,
  preferredAccountId,
  accounts,
  types,
  busy,
  conflictError,
  onReloadConflict,
  onSwitchToDuplicate,
  onCancel,
  onSave,
}: {
  mode: "add" | "duplicate" | "edit";
  plan?: ProviderPlan;
  preferredAccountId?: string;
  accounts: ProviderAccount[];
  types: ProviderType[];
  busy: boolean;
  conflictError: string | null;
  onReloadConflict?: () => Promise<ProviderPlan | null>;
  onSwitchToDuplicate: (plan: ProviderPlan) => void;
  onCancel: () => void;
  onSave: (input: PlanInput & { providerAccountId: string }) => Promise<void>;
}) {
  const enabledAccounts = useMemo(
    () => accounts.filter((item) => item.enabled),
    [accounts],
  );

  const [currentPlan, setCurrentPlan] = useState<ProviderPlan | undefined>(plan);

  const initialAccountId = useMemo(() => {
    if (mode === "edit" && currentPlan) {
      return currentPlan.providerAccountId;
    }
    if (mode === "duplicate" && currentPlan) {
      const sourceEnabled = enabledAccounts.some(
        (item) => item.id === currentPlan.providerAccountId,
      );
      if (sourceEnabled) return currentPlan.providerAccountId;
    }
    if (
      preferredAccountId &&
      enabledAccounts.some((item) => item.id === preferredAccountId)
    ) {
      return preferredAccountId;
    }
    return enabledAccounts[0]?.id ?? currentPlan?.providerAccountId ?? "";
  }, [mode, currentPlan, preferredAccountId, enabledAccounts]);

  const [name, setName] = useState(() =>
    mode === "edit" && currentPlan ? currentPlan.name : crypto.randomUUID(),
  );
  const [accountId, setAccountId] = useState(initialAccountId);

  const selectedAccount = useMemo(
    () => accounts.find((item) => item.id === accountId),
    [accounts, accountId],
  );
  const selectedProviderType =
    selectedAccount?.providerType ?? currentPlan?.providerType ?? types[0]?.type ?? "hetzner";

  const [region, setRegion] = useState(() => {
    if (
      mode === "duplicate" &&
      currentPlan &&
      initialAccountId !== currentPlan.providerAccountId
    ) {
      return "";
    }
    return currentPlan?.region ?? "";
  });
  const [sku, setSku] = useState(() => {
    if (
      mode === "duplicate" &&
      currentPlan &&
      initialAccountId !== currentPlan.providerAccountId
    ) {
      return "";
    }
    return currentPlan?.sku ?? "";
  });
  const [image, setImage] = useState(() => {
    if (
      mode === "duplicate" &&
      currentPlan &&
      initialAccountId !== currentPlan.providerAccountId
    ) {
      return defaultImageForType(selectedProviderType, types);
    }
    if (currentPlan) {
      return currentPlan.image ?? "";
    }
    return defaultImageForType(selectedProviderType, types);
  });
  const [platform, setPlatform] = useState(() => {
    if (
      mode === "duplicate" &&
      currentPlan &&
      initialAccountId !== currentPlan.providerAccountId
    ) {
      return "linux/amd64";
    }
    return currentPlan?.buildPlatform ?? "linux/amd64";
  });
  const [advancedOpen, setAdvancedOpen] = useState(false);

  const locked = mode === "edit" && Boolean(currentPlan?.provisioningLocked);

  function handleAccountChange(nextId: string) {
    setAccountId(nextId);
    const nextAcc = accounts.find((item) => item.id === nextId);
    const nextType = nextAcc?.providerType ?? "hetzner";
    setRegion("");
    setSku("");
    setImage(defaultImageForType(nextType, types));
    setPlatform("linux/amd64");
  }

  async function handleReload() {
    if (!onReloadConflict) return;
    const latest = await onReloadConflict();
    if (latest) {
      setCurrentPlan(latest);
      setRegion(latest.region);
      setSku(latest.sku);
      setImage(latest.image ?? "");
      setPlatform(latest.buildPlatform);
    }
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    if (conflictError) return;
    void onSave({
      providerAccountId: accountId,
      name,
      region,
      sku,
      image: image || null,
      buildPlatform: platform,
    });
  }

  return (
    <form className="grid gap-4" onSubmit={handleSubmit}>
      {conflictError ? (
        <div
          role="alert"
          className="space-y-2 rounded-md border border-destructive/40 bg-error-surface p-3 text-sm text-destructive"
        >
          <p>{conflictError}</p>
          <p className="text-xs">
            Your entered name has been kept. Reload current data before saving.
          </p>
          {onReloadConflict ? (
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={busy}
              onClick={() => void handleReload()}
            >
              Reload current data
            </Button>
          ) : null}
        </div>
      ) : null}

      <div className="grid gap-1.5">
        <Label htmlFor="plan-name">Name</Label>
        <Input
          id="plan-name"
          aria-describedby="plan-name-help"
          required
          maxLength={128}
          disabled={busy}
          value={name}
          onChange={(event) => setName(event.target.value)}
        />
        <p id="plan-name-help" className="text-xs text-muted-foreground">
          {mode === "edit"
            ? "After renaming, update the name in minicloud.yml and CLI commands. Existing app bindings are preserved."
            : "Unique across this organization. Used in the CLI and minicloud.yml."}
        </p>
      </div>

      <div className="grid gap-1.5">
        <Label htmlFor="plan-account">Provider account</Label>
        {mode === "edit" && currentPlan ? (
          <>
            <select
              id="plan-account"
              aria-describedby="plan-account-help"
              className={selectClass}
              disabled
              value={currentPlan.providerAccountId}
            >
              <option value={currentPlan.providerAccountId}>
                {resolveAccountName(currentPlan, accounts)} (
                {formatProviderName(currentPlan.providerType, types)})
              </option>
            </select>
            <p id="plan-account-help" className="text-xs text-muted-foreground">
              Provider account cannot be changed for an existing plan. Duplicate this plan
              to use another account.
            </p>
          </>
        ) : (
          <select
            id="plan-account"
            className={selectClass}
            required
            disabled={busy}
            value={accountId}
            onChange={(event) => handleAccountChange(event.target.value)}
          >
            {enabledAccounts.map((acc) => (
              <option key={acc.id} value={acc.id}>
                {acc.name} ({formatProviderName(acc.providerType, types)})
              </option>
            ))}
          </select>
        )}
      </div>

      {locked && currentPlan ? (
        <div className="flex flex-wrap items-center justify-between gap-2 rounded-md border bg-muted/40 p-3 text-sm text-muted-foreground">
          <p id="plan-locked-explanation">
            This plan&apos;s placement is locked. You can rename it or duplicate it to choose a
            different placement.
          </p>
          <Button
            type="button"
            size="sm"
            variant="outline"
            disabled={busy}
            onClick={() => onSwitchToDuplicate(currentPlan)}
          >
            Duplicate plan
          </Button>
        </div>
      ) : null}

      <div className="grid gap-4 sm:grid-cols-2">
        <div className="grid gap-1.5">
          <Label htmlFor="plan-region">Location</Label>
          <Input
            id="plan-region"
            aria-describedby={locked ? "plan-locked-explanation" : "plan-region-help"}
            required
            disabled={locked || busy}
            placeholder={regionExample(selectedProviderType)}
            value={region}
            onChange={(event) => setRegion(event.target.value)}
          />
          <p id="plan-region-help" className="text-xs text-muted-foreground">
            Example: {regionExample(selectedProviderType)}
          </p>
        </div>
        <div className="grid gap-1.5">
          <Label htmlFor="plan-sku">Server type</Label>
          <Input
            id="plan-sku"
            aria-describedby={locked ? "plan-locked-explanation" : "plan-sku-help"}
            required
            disabled={locked || busy}
            placeholder={skuExample(selectedProviderType)}
            value={sku}
            onChange={(event) => setSku(event.target.value)}
          />
          <p id="plan-sku-help" className="text-xs text-muted-foreground">
            Example: {skuExample(selectedProviderType)}
          </p>
        </div>
      </div>

      <div className="space-y-3">
        <button
          type="button"
          aria-expanded={advancedOpen}
          className="inline-flex items-center gap-1.5 text-sm font-medium text-foreground hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          onClick={() => setAdvancedOpen((prev) => !prev)}
        >
          {advancedOpen ? (
            <ChevronDown className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
          ) : (
            <ChevronRight className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
          )}
          <span>Advanced settings</span>
        </button>

        {advancedOpen ? (
          <div className="grid gap-4 rounded-md border p-3">
            <div className="grid gap-1.5">
              <Label htmlFor="plan-image">
                {selectedProviderType === "vultr"
                  ? "OS ID override (optional)"
                  : "Image"}
              </Label>
              <Input
                id="plan-image"
                aria-describedby={locked ? "plan-locked-explanation" : undefined}
                disabled={locked || busy}
                type={selectedProviderType === "vultr" ? "number" : "text"}
                min={selectedProviderType === "vultr" ? 1 : undefined}
                value={image}
                onChange={(event) => setImage(event.target.value)}
              />
            </div>
            <div className="grid gap-1.5">
              <Label htmlFor="plan-platform">Linux platform</Label>
              <select
                id="plan-platform"
                aria-describedby={locked ? "plan-locked-explanation" : undefined}
                className={selectClass}
                disabled={locked || busy}
                value={platform}
                onChange={(event) => setPlatform(event.target.value)}
              >
                <option value="linux/amd64">linux/amd64</option>
                <option value="linux/arm64">linux/arm64</option>
              </select>
            </div>
          </div>
        ) : null}
      </div>

      <DialogFooter className="gap-2 sm:justify-end">
        <Button type="button" variant="outline" disabled={busy} onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" disabled={busy || Boolean(conflictError)}>
          {busy ? "Saving…" : mode === "edit" ? "Save changes" : "Add plan"}
        </Button>
      </DialogFooter>
    </form>
  );
}

function AccountDialogForm({
  account,
  types,
  busy,
  conflictError,
  onReloadConflict,
  onCancel,
  onSave,
}: {
  account?: ProviderAccount;
  types: ProviderType[];
  busy: boolean;
  conflictError: string | null;
  onReloadConflict?: () => Promise<ProviderAccount | null>;
  onCancel: () => void;
  onSave: (input: {
    name: string;
    providerType: string;
    credential: string;
    projectId?: string;
    defaultOsId?: number;
  }) => Promise<void>;
}) {
  const providerOptions = types.length
    ? types
    : [
        { type: "hetzner", name: "Hetzner", supportsStorage: true, defaultImage: "ubuntu-24.04" },
        { type: "digital-ocean", name: "DigitalOcean", supportsStorage: false, defaultImage: "ubuntu-24-04-x64" },
        { type: "vultr", name: "Vultr", supportsStorage: false, defaultImage: null, defaultOsId: 2284 },
      ];

  const [name, setName] = useState(account?.name ?? "");
  const [type, setType] = useState(
    account?.providerType ?? providerOptions[0]?.type ?? "hetzner",
  );
  const [credential, setCredential] = useState("");
  const [projectId, setProjectId] = useState(account?.projectId ?? "");
  const [osId, setOsId] = useState(
    String(
      account?.defaultOsId ??
        providerOptions.find((item) => item.type === type)?.defaultOsId ??
        "",
    ),
  );

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    if (conflictError) return;
    const submittedCredential = credential;
    setCredential("");
    await onSave({
      name,
      providerType: type,
      credential: submittedCredential,
      projectId: projectId || undefined,
      defaultOsId: osId ? Number(osId) : undefined,
    });
  }

  return (
    <form className="grid gap-4" onSubmit={handleSubmit}>
      {conflictError ? (
        <div
          role="alert"
          className="space-y-2 rounded-md border border-destructive/40 bg-error-surface p-3 text-sm text-destructive"
        >
          <p>{conflictError}</p>
          {onReloadConflict ? (
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={busy}
              onClick={() => void onReloadConflict()}
            >
              Reload current data
            </Button>
          ) : null}
        </div>
      ) : null}

      <div className="grid gap-1.5">
        <Label htmlFor="provider-name">Account name</Label>
        <Input
          id="provider-name"
          required
          maxLength={128}
          disabled={busy}
          value={name}
          onChange={(event) => setName(event.target.value)}
        />
      </div>

      {account ? (
        <div className="grid gap-1.5">
          <Label htmlFor="provider-type">Provider</Label>
          <select id="provider-type" className={selectClass} disabled value={account.providerType}>
            <option value={account.providerType}>
              {formatProviderName(account.providerType, providerOptions)}
            </option>
          </select>
        </div>
      ) : (
        <>
          <div className="grid gap-1.5">
            <Label htmlFor="provider-type">Provider</Label>
            <select
              id="provider-type"
              className={selectClass}
              disabled={busy}
              value={type}
              onChange={(event) => {
                const nextType = event.target.value;
                setType(nextType);
                setOsId(
                  String(
                    providerOptions.find((item) => item.type === nextType)?.defaultOsId ??
                      "",
                  ),
                );
              }}
            >
              {providerOptions.map((item) => (
                <option key={item.type} value={item.type}>
                  {item.name}
                </option>
              ))}
            </select>
          </div>

          <div className="grid gap-1.5">
            <Label htmlFor="provider-credential">API token or key</Label>
            <Input
              id="provider-credential"
              type="password"
              autoComplete="new-password"
              required
              disabled={busy}
              value={credential}
              onChange={(event) => setCredential(event.target.value)}
            />
          </div>

          {type === "digital-ocean" ? (
            <div className="grid gap-1.5">
              <Label htmlFor="provider-project">Project ID (optional)</Label>
              <Input
                id="provider-project"
                disabled={busy}
                value={projectId}
                onChange={(event) => setProjectId(event.target.value)}
              />
            </div>
          ) : null}

          {type === "vultr" ? (
            <div className="grid gap-1.5">
              <Label htmlFor="provider-os">Default OS ID</Label>
              <Input
                id="provider-os"
                type="number"
                min={1}
                disabled={busy}
                value={osId}
                onChange={(event) => setOsId(event.target.value)}
              />
            </div>
          ) : null}
        </>
      )}

      <DialogFooter className="gap-2 sm:justify-end">
        <Button type="button" variant="outline" disabled={busy} onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" disabled={busy || Boolean(conflictError)}>
          {busy ? "Saving…" : account ? "Save changes" : "Add account"}
        </Button>
      </DialogFooter>
    </form>
  );
}

function CredentialDialogForm({
  accountName,
  busy,
  onCancel,
  onSave,
}: {
  accountName: string;
  busy: boolean;
  onCancel: () => void;
  onSave: (credential: string) => Promise<void>;
}) {
  const [credential, setCredential] = useState("");

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    const value = credential;
    setCredential("");
    void onSave(value);
  }

  return (
    <form className="grid gap-4" onSubmit={handleSubmit}>
      <p className="text-sm text-muted-foreground">
        Replace the API token or key for <strong className="font-medium text-foreground">{accountName}</strong>.
        Use credentials for the same cloud account and project. A different cloud account
        requires a new provider entry.
      </p>
      <div className="grid gap-1.5">
        <Label htmlFor="replacement-credential">New API token or key</Label>
        <Input
          id="replacement-credential"
          type="password"
          autoComplete="new-password"
          required
          disabled={busy}
          value={credential}
          onChange={(event) => setCredential(event.target.value)}
        />
      </div>
      <DialogFooter className="gap-2 sm:justify-end">
        <Button type="button" variant="outline" disabled={busy} onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" disabled={busy}>
          {busy ? "Saving…" : "Save credential"}
        </Button>
      </DialogFooter>
    </form>
  );
}
