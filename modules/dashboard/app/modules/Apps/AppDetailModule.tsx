import { useAppServiceInventory } from "./useAppServiceInventory";
import { CollectionToolbar } from "@/core/components/CollectionToolbar";
import { useRecordCollection } from "@/core/components/useRecordCollection";
import { NavigableTableRow } from "@/core/components/NavigableTableRow";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import {
  ChevronDown,
  FileText,
  Globe,
  KeyRound,
  MoreHorizontal,
  Trash2,
} from "lucide-react";
import { Fragment, useCallback, useEffect, useRef, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import {
  createAppServiceSecretService,
  deleteAppServiceSecretService,
  getEnvironmentDetail,
  listAppServices,
  listAppServiceSecretServices,
  renameAppServiceSecretService,
} from "../../api/environmentsApi";
import { useDeploymentFeed } from "../Deployments/useDeploymentFeed";
import { useAuth } from "../../core/auth/AuthProvider";
import { CopyButton } from "../../core/components/CopyButton";
import { EmptyState } from "../../core/components/EmptyState";
import { PageScaffold } from "../../core/components/PageScaffold";
import { dashboardRoutes } from "../../core/routing/dashboardRoutes";
import { StatusBadge } from "../../core/components/StatusBadge";
import type {
  EnvironmentDetailResponse,
  AppServiceInventoryResponse,
  AppServiceSecretServiceResponse,
} from "../../models/controlPlane";
import { AppStoragePanel } from "./AppStoragePanel";
import { AppConsolePanel } from "./AppConsolePanel";
import { AppTelemetryPanel } from "./AppTelemetryPanel";
import { AppDomains } from "./components/AppDomains";
import { ManageServiceDomainsDialog } from "./components/ManageServiceDomainsDialog";
import { AppServiceSecretsDialog } from "./components/AppServiceSecretsDialog";

import { ServiceRuntimeStatus } from "./components/ServiceRuntimeStatus";
import { runtimeFresh } from "./serviceRuntime";

import { EnvironmentScopeHeader } from "./EnvironmentScopeHeader";
import { appSettingsPath } from "./AppsModule";
import { deleteEnvironment, getApplication } from "../../api/applicationsApi";
import { appsFixtureApplications, appsFixturesEnabled } from "../../api/applicationsFixtures";
import type { ApplicationEnvironment, Application } from "../../models/application";

const OPERATIONAL_TABS = ["overview", "services", "storage", "domains", "secrets", "deployments", "console", "logs"];

export function AppDetailModule() {
  const { applicationId, environmentId } = useParams();
  const { selectedOrganization } = useAuth();
  return <ScopedAppDetail key={JSON.stringify([selectedOrganization?.id, applicationId, environmentId])} />;
}

function ScopedAppDetail() {
  const { applicationId, environmentId, tab } = useParams();
  const navigate = useNavigate();
  const { selectedOrganization } = useAuth();
  const detailScope = JSON.stringify([selectedOrganization?.id, applicationId, environmentId ?? null]);
  const currentScope = useRef(detailScope);
  currentScope.current = detailScope;
  const [application, setApplication] = useState<Application | null>(null);
  const [selectedEnvironment, setSelectedEnvironment] = useState<ApplicationEnvironment | null>(null);
  const [resolvedRuntimeAppId, setResolvedRuntimeAppId] = useState<string | null>(null);
  const runtimeAppId = resolvedRuntimeAppId;
  const [app, setApp] = useState<EnvironmentDetailResponse | null>(null);
  const {
    services,
    error: serviceRefreshError,
    loading: serviceLoading,
    now: observedNow,
    refresh: refreshServices,
  } = useAppServiceInventory(runtimeAppId ?? undefined, selectedOrganization?.id);
  const [secretServices, setSecretServices] = useState<
    AppServiceSecretServiceResponse[]
  >([]);
  const { deployments, error: deploymentError, observed: deploymentsObserved, refresh: refreshDeployments } =
    useDeploymentFeed(runtimeAppId ? selectedOrganization?.id : undefined, runtimeAppId ?? undefined);
  const [error, setError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [secretServiceError, setSecretServiceError] = useState<string | null>(
    null,
  );
  const [busySecretServiceId, setBusySecretServiceId] = useState<string | null>(
    null,
  );
  const [manageDomainsServiceName, setManageDomainsServiceName] = useState<
    string | null
  >(null);
  const [secretsServiceName, setSecretsServiceName] = useState<string | null>(
    null,
  );
  const [manualSecretsServiceName, setManualSecretsServiceName] = useState("");
  const [editingSecretServiceId, setEditingSecretServiceId] = useState<
    string | null
  >(null);
  const [editingSecretServiceName, setEditingSecretServiceName] = useState("");
  const [activeTab, setActiveTab] = useState("overview");
  const [expandedServiceName, setExpandedServiceName] = useState<string | null>(
    null,
  );
  // Scoped environment deletion target, captured at open (original-target
  // preservation): switching environments cannot retarget the pending write.
  const [envDeleteTarget, setEnvDeleteTarget] = useState<{
    applicationId: string;
    environmentId: string;
    appName: string;
    envName: string;
  } | null>(null);
  const [envDeleteConfirmation, setEnvDeleteConfirmation] = useState("");
  const [envDeleteBusy, setEnvDeleteBusy] = useState(false);
  const [envDeleteError, setEnvDeleteError] = useState<string | null>(null);

  // Tab selection is explicit (not URL-only) so uncontrolled Radix activation
  // quirks cannot desync the URL from the rendered tab. In scoped mode the
  // URL is the source of truth for reload/back/forward.
  const selectTab = useCallback(
    (next: string) => {
      setActiveTab(next);
      if (applicationId && environmentId) {
        navigate(`/apps/${encodeURIComponent(applicationId)}/environments/${encodeURIComponent(environmentId)}/${next}`);
      }
    },
    [applicationId, environmentId, navigate],
  );
  const [logsServiceFilter, setLogsServiceFilter] = useState<string | null>(null);

  // Scope switch: drop every previously scoped observation, dialog/form
  // selector, in-progress edit, and busy/error state so stale data — or an
  // unsubmitted form for the previous environment's services — can never
  // render or operate under the new header. Keying the dialogs alone does
  // not clear this parent state, so state is cleared explicitly. Pending
  // writes keep only their captured immutable targets (deleteTarget).
  useEffect(() => {
    currentScope.current = detailScope;
    setApp(null);
    setApplication(null);
    setSelectedEnvironment(null);
    setResolvedRuntimeAppId(null);
    setSecretServices([]);
    setError(null);
    setActionError(null);
    setSecretServiceError(null);
    setExpandedServiceName(null);
    setLogsServiceFilter(null);
    setEnvDeleteTarget(null);
    setEnvDeleteConfirmation("");
    setEnvDeleteBusy(false);
    setEnvDeleteError(null);
    setManageDomainsServiceName(null);
    setSecretsServiceName(null);
    setManualSecretsServiceName("");
    setEditingSecretServiceId(null);
    setEditingSecretServiceName("");
    setBusySecretServiceId(null);
    return () => { currentScope.current = "unmounted"; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [detailScope]);

  // Sync tab from the URL so deep links and browser back/forward preserve scope.
  useEffect(() => {
    if (tab && OPERATIONAL_TABS.includes(tab)) setActiveTab(tab);
  }, [tab, detailScope]);

  useEffect(() => {
    if (!selectedOrganization) return;
    {
      if (!applicationId || !environmentId || resolvedRuntimeAppId) return;
      let live = true;
      (async () => {
        try {
          const app = await loadScoping(applicationId);
          if (!live) return;
          if (app.id !== applicationId || app.organizationId !== selectedOrganization.id) {
            throw new Error("Application does not belong to the selected organization.");
          }
          const env = app.environments.find((e) => e.id === environmentId);
          if (!env || env.applicationId !== applicationId || env.organizationId !== selectedOrganization.id) {
            setError(`Environment ${environmentId} not found in application ${applicationId}.`);
            return;
          }
          setApplication(app);
          setSelectedEnvironment(env);
          // Only the selected, authorized environment can provide runtime data.
          if (env.id) setResolvedRuntimeAppId(env.id);
          else setError(`Environment ${env.id ?? "<unknown>"} has no resolvable identity.`);
        } catch (loadError) {
          if (live) {
            setError(
              loadError instanceof Error
                ? loadError.message
                : "Unable to load application scope.",
            );
          }
        }
      })();
      return () => { live = false; };
    }
  }, [applicationId, environmentId, resolvedRuntimeAppId, selectedOrganization?.id]);

  useEffect(() => {
    if (!runtimeAppId || !selectedOrganization) return;
    let live = true;
    loadAppDetail(runtimeAppId)
      .then(([nextApp, nextSecretServices]) => {
        if (!live) return;
        setApp(nextApp);
        setSecretServices(nextSecretServices);
      })
      .catch((loadError) =>
        live && setError(
          loadError instanceof Error
            ? loadError.message
            : "Unable to load app.",
        ),
      );
    return () => { live = false; };
  }, [runtimeAppId, selectedOrganization?.id]);

  const refreshRecord = useCallback(async () => {
    if (!runtimeAppId || !selectedOrganization) return;
    setActionError(null);
    try {
      const [[nextApp, nextSecretServices], , , nextApplication] = await Promise.all([
        loadAppDetail(runtimeAppId),
        refreshDeployments(),
        refreshServices(),
        applicationId ? loadScoping(applicationId) : Promise.resolve(null),
      ]);
      if (currentScope.current !== detailScope) return;
      if (nextApplication) {
        if (nextApplication.id !== applicationId || nextApplication.organizationId !== selectedOrganization.id) {
          throw new Error("Application does not belong to the selected organization.");
        }
        const nextEnvironment = nextApplication.environments.find(env => env.id === environmentId && env.applicationId === applicationId && env.organizationId === selectedOrganization.id);
        if (!nextEnvironment) throw new Error("The selected environment is no longer available in this application.");
        setApplication(nextApplication);
        setSelectedEnvironment(nextEnvironment);
      }
      setApp(nextApp);
      setSecretServices(nextSecretServices);
    } catch (refreshError) {
      if (currentScope.current !== detailScope) return;
      setActionError(refreshError instanceof Error ? refreshError.message : "Unable to refresh app.");
    }
  }, [runtimeAppId, applicationId, environmentId, selectedOrganization?.id, detailScope, refreshServices, refreshDeployments]);

  useEffect(() => {
    if (
      !app?.deletionStatus ||
      app.deletionStatus === "failed" ||
      !selectedOrganization
    )
      return;
    let canceled = false;
    let timer: ReturnType<typeof setTimeout>;
    const poll = async () => {
      try {
        const parent = await getApplication(applicationId!);
        if (canceled) return;
        if (!parent.environments.some((environment) => environment.id === app.id)) {
          navigate(dashboardRoutes.apps.path, { replace: true });
          return;
        }
        const next = await getEnvironmentDetail(app.id);
        if (!canceled) {
          setApp(next);
          setActionError(null);
        }
      } catch {
        if (!canceled)
          setActionError("Deletion status could not be refreshed. Retrying…");
      } finally {
        if (!canceled) timer = setTimeout(poll, 3000);
      }
    };
    void poll();
    return () => {
      canceled = true;
      clearTimeout(timer);
    };
  }, [app?.id, app?.deletionStatus, selectedOrganization, applicationId, navigate]);

  // Capture the immutable scope at operation start. Every post-await state
  // update below is checked against this identity: a response that lands
  // after the user switched environments is discarded — it can never
  // repopulate the new scope's UI or reopen dialogs bound to it.
  const scopeStillCurrent = (captured: string) =>
    currentScope.current === captured;

  const refreshSecretServices = useCallback(async () => {
    if (!runtimeAppId) {
      return;
    }
    const captured = currentScope.current;

    const next = await listAppServiceSecretServices(runtimeAppId);
    if (!scopeStillCurrent(captured)) return;
    setSecretServices(next);
  }, [runtimeAppId]);

  async function createSecretService() {
    if (!runtimeAppId || !manualSecretsServiceName.trim()) {
      return;
    }
    const captured = currentScope.current;
    const targetAppId = runtimeAppId;
    const targetName = manualSecretsServiceName.trim();

    setSecretServiceError(null);
    try {
      const created = await createAppServiceSecretService(
        targetAppId,
        targetName,
      );
      if (!scopeStillCurrent(captured)) return;
      setManualSecretsServiceName("");
      await refreshSecretServices();
      if (!scopeStillCurrent(captured)) return;
      setSecretsServiceName(created.serviceName);
    } catch (createError) {
      if (!scopeStillCurrent(captured)) return;
      setSecretServiceError(
        createError instanceof Error
          ? createError.message
          : "Unable to add service.",
      );
    }
  }

  async function renameSecretService(serviceId: string) {
    if (!runtimeAppId || !editingSecretServiceName.trim()) {
      return;
    }
    const captured = currentScope.current;
    const targetAppId = runtimeAppId;
    const targetServiceId = serviceId;
    const targetName = editingSecretServiceName.trim();

    setBusySecretServiceId(targetServiceId);
    setSecretServiceError(null);
    try {
      await renameAppServiceSecretService(
        targetAppId,
        targetServiceId,
        targetName,
      );
      if (!scopeStillCurrent(captured)) return;
      setEditingSecretServiceId(null);
      setEditingSecretServiceName("");
      await refreshSecretServices();
    } catch (renameError) {
      if (!scopeStillCurrent(captured)) return;
      setSecretServiceError(
        renameError instanceof Error
          ? renameError.message
          : "Unable to rename service.",
      );
    } finally {
      if (scopeStillCurrent(captured)) setBusySecretServiceId(null);
    }
  }

  async function deleteSecretService(serviceId: string) {
    if (
      !runtimeAppId ||
      !window.confirm(
        "Remove this service from secrets management? Secrets already stored for this service remain unless deleted separately.",
      )
    ) {
      return;
    }

    const captured = currentScope.current;

    setBusySecretServiceId(serviceId);
    setSecretServiceError(null);
    try {
      await deleteAppServiceSecretService(runtimeAppId, serviceId);
      if (!scopeStillCurrent(captured)) return;
      await refreshSecretServices();
    } catch (deleteError) {
      if (!scopeStillCurrent(captured)) return;
      setSecretServiceError(
        deleteError instanceof Error
          ? deleteError.message
          : "Unable to remove service.",
      );
    } finally {
      if (scopeStillCurrent(captured)) setBusySecretServiceId(null);
    }
  }

  const serviceCollection = useRecordCollection(
    `${runtimeAppId}:services`,
    services,
    {
      name: (item) => item.name,
      access: (item) => (item.public ? "Public" : "Private"),
    },
    (item) => item.name,
  );
  const secretCollection = useRecordCollection(
    `${runtimeAppId}:secret-services`,
    secretServices,
    { name: (item) => item.serviceName },
    (item) => item.id,
  );
  const deploymentCollection = useRecordCollection(
    `${runtimeAppId}:history`,
    deployments,
    {
      createdAt: (item) => item.createdAt,
      status: (item) => item.status,
      id: (item) => item.id,
    },
    (item) => item.id,
    "createdAt",
    "desc",
  );
  if (error) {
    return <p className="text-sm text-destructive">{error}</p>;
  }

  if (!app || app.id !== runtimeAppId || app.organizationId !== selectedOrganization?.id) {
    return <p className="text-sm text-muted-foreground">Loading app...</p>;
  }

  const scopedHeader = application && selectedEnvironment ? (
    <EnvironmentScopeHeader
      application={application}
      environment={selectedEnvironment}
      currentTab={activeTab}
      onSwitch={(nextEnvironmentId) => {
        if (!applicationId) return;
        navigate(`/apps/${encodeURIComponent(applicationId)}/environments/${encodeURIComponent(nextEnvironmentId)}/${activeTab}`);
      }}
      onRefresh={() => void refreshRecord()}
      loading={serviceLoading}
      refreshing={serviceLoading}
      canManage={["owner", "admin", "developer"].includes(selectedOrganization?.role?.toLowerCase() ?? "")}
      moreActions={
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button aria-label={`More actions for ${application.name} ${selectedEnvironment.name}`} size="icon" variant="ghost">
              <MoreHorizontal className="h-4 w-4" />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem asChild>
              <Link to={appSettingsPath(application.id)}>Application settings…</Link>
            </DropdownMenuItem>
            <DropdownMenuItem asChild>
              <Link to={`${appSettingsPath(application.id)}#environment-${selectedEnvironment.id}`}>
                {selectedEnvironment.name} environment settings…
              </Link>
            </DropdownMenuItem>
            {["owner", "admin", "developer"].includes(selectedOrganization?.role?.toLowerCase() ?? "") ? <DropdownMenuItem
              disabled={!!application.deletionStatus || (!!selectedEnvironment.deletionStatus && selectedEnvironment.deletionStatus !== "failed")}
              onClick={() => {
                // Capture the explicit environment target before opening the dialog.
                setEnvDeleteTarget({
                  applicationId: application.id,
                  environmentId: selectedEnvironment.id,
                  appName: application.name,
                  envName: selectedEnvironment.name,
                });
                setEnvDeleteConfirmation("");
                setEnvDeleteError(null);
              }}
            >
              <Trash2 className="mr-2 h-4 w-4" />
              <span>Delete {application.name} / {selectedEnvironment.name} environment…</span>
            </DropdownMenuItem> : null}
            <DropdownMenuItem asChild>
              <Link to={appSettingsPath(application.id)}>
                Delete application…
              </Link>
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      }
    />
  ) : null;

  const latestDeployment = deploymentsObserved
    ? [...deployments].sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt))[0] ?? null
    : app.latestDeployment;
  const activeDomainCount = services.reduce(
    (count, service) =>
      count +
      service.domains.filter((domain) => domain.status === "active").length,
    0,
  );
  const runtimeRunningCount = services.filter(
    (service) =>
      runtimeFresh(service, observedNow) &&
      service.runtime?.state === "running",
  ).length;

  return (
    <PageScaffold
      breadcrumbs={
        application && selectedEnvironment
          ? [
              { label: "Apps", to: dashboardRoutes.apps.path },
              { label: application.name },
              { label: selectedEnvironment.name },
            ]
          : [
              { label: "Apps", to: dashboardRoutes.apps.path },
              { label: app.name },
            ]
      }
      header={scopedHeader}
      title={app.name}
    >
      {app.deletionStatus ? (
        <p
          role="status"
          className={
            app.deletionStatus === "failed"
              ? "text-sm text-destructive"
              : "text-sm text-muted-foreground"
          }
        >
          {app.deletionError ??
            actionError ??
            "Deleting this application and its resources. You can leave this page while cleanup runs."}
        </p>
      ) : null}
      {deploymentError ? <p role="status" className="text-destructive">{deploymentError}</p> : null}
      {serviceRefreshError ? (
        <p role="status" className="text-destructive">
          {serviceRefreshError}
        </p>
      ) : null}

      <Tabs onValueChange={selectTab} value={activeTab}>
        <div className="sm:hidden">
          <Label htmlFor="section-menu">Section</Label>
          <Select value={activeTab} onValueChange={selectTab}>
            <SelectTrigger id="section-menu" aria-label="Section">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {OPERATIONAL_TABS.map((value) => (
                <SelectItem key={value} value={value}>
                  {value === "logs" ? "Logs" : value === "console" ? "Console" : value.charAt(0).toUpperCase() + value.slice(1)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <TabsList className="hidden w-full justify-start rounded-none border-b bg-transparent p-0 sm:flex">
          <TabsTrigger
            className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary data-[state=active]:bg-transparent data-[state=active]:shadow-none data-[state=active]:text-foreground" onClick={() => selectTab("overview")}
            value="overview"
          >
            Overview
          </TabsTrigger>
          <TabsTrigger
            className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary data-[state=active]:bg-transparent data-[state=active]:shadow-none data-[state=active]:text-foreground" onClick={() => selectTab("services")}
            value="services"
          >
            Services
          </TabsTrigger>
          <TabsTrigger className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary data-[state=active]:bg-transparent data-[state=active]:shadow-none data-[state=active]:text-foreground" onClick={() => selectTab("storage")} value="storage">Storage</TabsTrigger>
          <TabsTrigger className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary data-[state=active]:bg-transparent data-[state=active]:shadow-none data-[state=active]:text-foreground" onClick={() => selectTab("domains")} value="domains">Domains</TabsTrigger>
          <TabsTrigger className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary data-[state=active]:bg-transparent data-[state=active]:shadow-none data-[state=active]:text-foreground" onClick={() => selectTab("secrets")} value="secrets">
            Secrets
          </TabsTrigger>
          <TabsTrigger
            className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary data-[state=active]:bg-transparent data-[state=active]:shadow-none data-[state=active]:text-foreground" onClick={() => selectTab("deployments")}
            value="deployments"
          >
            Deployments
          </TabsTrigger>
          <TabsTrigger className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary data-[state=active]:bg-transparent data-[state=active]:shadow-none data-[state=active]:text-foreground" onClick={() => selectTab("console")} value="console">
            Console
          </TabsTrigger>
          <TabsTrigger className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary data-[state=active]:bg-transparent data-[state=active]:shadow-none data-[state=active]:text-foreground" onClick={() => selectTab("logs")} value="logs">
            Logs
          </TabsTrigger>
        </TabsList>

        <TabsContent value="storage">
          <AppStoragePanel key={app.id} appId={app.id} services={services} />
        </TabsContent>

        <TabsContent value="domains">
          <AppDomains key={app.id} organizationId={app.organizationId} appId={app.id} services={services} onSaved={refreshServices} onManageService={setManageDomainsServiceName} />
        </TabsContent>

        <TabsContent className="space-y-4" value="overview">
          <div className="grid gap-4 md:grid-cols-3">
            <Card>
              <CardHeader>
                <CardTitle>Runtime</CardTitle>
              </CardHeader>
              <CardContent className="space-y-2 text-sm">
                <p className="text-muted-foreground">
                  {runtimeRunningCount}/{services.length} observed running
                </p>
                <p className="text-xs text-muted-foreground">
                  {services.some((service) => runtimeFresh(service, observedNow) && service.runtime)
                    ? "Fresh observation"
                    : serviceRefreshError
                      ? "Could not refresh. Showing last observation."
                      : "No fresh observation"}
                </p>
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Latest deployment</CardTitle>
              </CardHeader>
              <CardContent className="space-y-2 text-sm">
                {latestDeployment ? (
                  <>
                    <StatusBadge status={latestDeployment.status} />
                    <p className="text-muted-foreground">
                      {latestDeployment.completedAt
                        ? `Completed ${formatDate(latestDeployment.completedAt)}`
                        : "In progress"}
                    </p>
                    <button
                      type="button"
                      className="text-primary hover:underline"
                      onClick={() => {
                        setActiveTab("deployments");
                        if (applicationId && environmentId) {
                          navigate(`/apps/${encodeURIComponent(applicationId)}/environments/${encodeURIComponent(environmentId)}/deployments`);
                        }
                      }}
                    >
                      View deployment
                    </button>
                  </>
                ) : (
                  <p className="text-muted-foreground">No deployments yet.</p>
                )}
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Domains</CardTitle>
              </CardHeader>
              <CardContent className="space-y-2 text-sm">
                <p className="text-muted-foreground">
                  {activeDomainCount} {activeDomainCount === 1 ? "hostname" : "hostnames"}
                </p>
                <button
                  type="button"
                  className="text-primary hover:underline"
                  onClick={() => {
                    setActiveTab("domains");
                    if (applicationId && environmentId) {
                      navigate(`/apps/${encodeURIComponent(applicationId)}/environments/${encodeURIComponent(environmentId)}/domains`);
                    }
                  }}
                >
                  View domains
                </button>
              </CardContent>
            </Card>
          </div>
          <div className="grid gap-4">
            <Card>
              <CardHeader>
                <CardTitle>Services</CardTitle>
                <p className="text-sm text-muted-foreground">
                  Preferred address and the latest observed runtime state.
                </p>
              </CardHeader>
              <CardContent className="p-0">
                {services.length === 0 ? (
                  <div className="p-4">
                    <EmptyState title="No services">
                      Deploy a service to see its access and runtime state.
                    </EmptyState>
                  </div>
                ) : (
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>Service</TableHead>
                        <TableHead>Runtime</TableHead>
                        <TableHead>Address</TableHead>
                        <TableHead className="text-right">Details</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {services.map((service) => {
                        const enabledDomains = service.domains.filter((domain) => domain.status !== "disabled");
                        const primary =
                          enabledDomains.find((domain) => domain.isPrimary) ??
                          enabledDomains.find((domain) => domain.kind === "custom_domain") ??
                          enabledDomains[0];
                        const address = primary
                          ? `https://${primary.hostname}`
                          : service.public
                            ? "No URL assigned"
                            : "Internal only";
                        const extra = enabledDomains.length - (primary ? 1 : 0);
                        return (
                          <TableRow key={service.name}>
                            <TableCell data-label="Service">
                              <div className="space-y-1">
                                <div className="font-medium">{service.name}</div>
                                <span className="text-xs text-muted-foreground">
                                  {service.public ? "Public" : "Private"}
                                </span>
                              </div>
                            </TableCell>
                            <TableCell data-label="Runtime">
                              <ServiceRuntimeStatus
                                service={service}
                                now={observedNow}
                              />
                            </TableCell>
                            <TableCell data-label="Address">
                              {primary ? (
                                <a
                                  className="break-all text-sm text-primary hover:underline"
                                  href={address}
                                  target="_blank"
                                  rel="noreferrer"
                                >
                                  {address}
                                </a>
                              ) : (
                                <span className="text-sm text-muted-foreground">{address}</span>
                              )}
                              {extra > 0 ? (
                                <p className="text-xs text-muted-foreground">+{extra} {extra === 1 ? "hostname" : "hostnames"}</p>
                              ) : null}
                            </TableCell>
                            <TableCell data-label="Details" className="text-right">
                              <button
                                type="button"
                                className="text-primary hover:underline"
                                onClick={() => {
                                  setExpandedServiceName(service.name);
                                  setActiveTab("services");
                                  if (applicationId && environmentId) {
                                    navigate(`/apps/${encodeURIComponent(applicationId)}/environments/${encodeURIComponent(environmentId)}/services`);
                                  }
                                  requestAnimationFrame(() => {
                                    document.getElementById(`service-details-${service.name}`)?.scrollIntoView({ block: "center" });
                                  });
                                }}
                              >
                                Details
                              </button>
                            </TableCell>
                          </TableRow>
                        );
                      })}
                    </TableBody>
                  </Table>
                )}
              </CardContent>
            </Card>

          </div>

        </TabsContent>

        <TabsContent className="space-y-4" value="services">
          <Card>
            <CardHeader>
              <CardTitle>Service routing</CardTitle>
              <CollectionToolbar
                label="services"
                {...serviceCollection.toolbar}
                onRefresh={() => refreshServices()}
                loading={serviceLoading}
              />
              {serviceRefreshError ? (
                <p role="status" className="text-sm text-destructive">
                  {serviceRefreshError}
                </p>
              ) : null}
            </CardHeader>
            <CardContent className="p-0">
              {actionError ? (
                <p className="px-4 pb-3 text-sm text-destructive">
                  {actionError}
                </p>
              ) : null}
              {services.length === 0 ? (
                <div className="p-4">
                  <EmptyState title="No services">
                    Deploy public services to create service subdomains.
                  </EmptyState>
                </div>
              ) : (
                <Table className="table-fixed">
                  <TableHeader>
                    <TableRow>
                      <TableHead className="w-[22%]">Service</TableHead>
                      <TableHead className="w-[10%]">Access</TableHead>
                      <TableHead className="w-[40%]">Primary address</TableHead>
                      <TableHead className="w-[23%]">Runtime</TableHead>
                      <TableHead className="w-[5%]" />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {serviceCollection.records.map((service) => {
                      const enabledDomains = service.domains.filter(
                        (domain) => domain.status !== "disabled",
                      );
                      const serviceDetailsId = `service-details-${service.name}`;
                      const serviceExpanded =
                        expandedServiceName === service.name;

                      return (
                        <Fragment key={service.name}>
                          <TableRow>
                            <TableCell data-label="Service">
                              <Button
                                aria-controls={serviceDetailsId}
                                aria-expanded={serviceExpanded}
                                className="h-auto w-full justify-between px-0 py-1 text-left font-medium hover:bg-transparent"
                                onClick={() =>
                                  setExpandedServiceName((current) =>
                                    current === service.name
                                      ? null
                                      : service.name,
                                  )
                                }
                                variant="ghost"
                              >
                                <span className="min-w-0 truncate">
                                  {service.name}
                                </span>
                                <ChevronDown
                                  className={`h-4 w-4 shrink-0 transition-transform ${
                                    serviceExpanded ? "rotate-180" : ""
                                  }`}
                                />
                              </Button>
                            </TableCell>
                            <TableCell data-label="Access">
                              <span
                                className={`inline-flex items-center rounded px-2 py-0.5 text-xs font-medium ${
                                  service.public
                                    ? "bg-info-surface text-info"
                                    : "bg-muted text-muted-foreground"
                                }`}
                              >
                                {service.public ? "Public" : "Private"}
                              </span>
                            </TableCell>
                            <TableCell data-label="Primary address">
                              {!service.public ? (
                                <span className="text-sm text-muted-foreground">
                                  Internal only
                                </span>
                              ) : enabledDomains.length === 0 ? (
                                <span className="text-sm text-muted-foreground">
                                  No URL assigned
                                </span>
                              ) : (() => {
                                const primary =
                                  enabledDomains.find((domain) => domain.isPrimary) ??
                                  enabledDomains.find((domain) => domain.kind === "custom_domain") ??
                                  enabledDomains[0];
                                const url = `https://${primary.hostname}`;
                                const extra = enabledDomains.length - 1;
                                return (
                                  <div className="min-w-0 space-y-1">
                                    <div className="flex min-w-0 items-center gap-2">
                                      <a
                                        aria-label={`${service.name} primary address ${url}`}
                                        className="min-w-0 break-all text-primary hover:underline"
                                        href={url}
                                        target="_blank"
                                        rel="noreferrer"
                                      >
                                        {primary.hostname}
                                      </a>
                                      <CopyButton
                                        ariaLabel={`Copy ${service.name} primary URL`}
                                        label="Copy"
                                        value={url}
                                      />
                                    </div>
                                    {extra > 0 ? (
                                      <p className="text-xs text-muted-foreground">
                                        +{extra} {extra === 1 ? "hostname" : "hostnames"}
                                      </p>
                                    ) : null}
                                  </div>
                                );
                              })()}
                            </TableCell>
                            <TableCell data-label="Runtime">
                              <ServiceRuntimeStatus
                                service={service}
                                now={observedNow}
                              />
                            </TableCell>
                            <TableCell
                              data-label="Actions"
                              className="text-right"
                            >
                              <DropdownMenu>
                                <DropdownMenuTrigger asChild>
                                  <Button
                                    aria-label={`Actions for ${service.name}`}
                                    size="icon"
                                    variant="ghost"
                                    className="h-8 w-8 text-muted-foreground hover:text-foreground"
                                  >
                                    <MoreHorizontal className="h-4 w-4" />
                                  </Button>
                                </DropdownMenuTrigger>
                                <DropdownMenuContent align="end">
                                  {service.public ? (
                                    <DropdownMenuItem
                                      onClick={() =>
                                        setManageDomainsServiceName(
                                          service.name,
                                        )
                                      }
                                    >
                                      <Globe className="mr-2 h-4 w-4" />
                                      <span>Manage domains...</span>
                                    </DropdownMenuItem>
                                  ) : null}
                                  <DropdownMenuItem
                                    onClick={() =>
                                      setSecretsServiceName(service.name)
                                    }
                                  >
                                    <KeyRound className="mr-2 h-4 w-4" />
                                    <span>Service secrets...</span>
                                  </DropdownMenuItem>
                                  {!service.public ? (
                                    <DropdownMenuItem
                                      onClick={() =>
                                        void navigator.clipboard.writeText(
                                          `http://${service.name}:${service.port}`,
                                        )
                                      }
                                    >
                                      Copy internal DNS
                                    </DropdownMenuItem>
                                  ) : null}
                                  <DropdownMenuItem
                                    onClick={() => {
                                      setLogsServiceFilter(service.name);
                                      setActiveTab("logs");
                                      if (applicationId && environmentId) {
                                        navigate(`/apps/${encodeURIComponent(applicationId)}/environments/${encodeURIComponent(environmentId)}/logs`);
                                      }
                                    }}
                                  >
                                    <FileText className="mr-2 h-4 w-4" />
                                    <span>View application logs</span>
                                  </DropdownMenuItem>
                                </DropdownMenuContent>
                              </DropdownMenu>
                            </TableCell>
                          </TableRow>
                          {serviceExpanded ? (
                            <TableRow
                              className="bg-muted/20 hover:bg-muted/20"
                              id={serviceDetailsId}
                            >
                              <TableCell
                                colSpan={5}
                                data-label="Service configuration"
                              >
                                <div className="space-y-3 px-2 py-3">
                                  <div>
                                    <p className="text-xs font-medium text-muted-foreground">
                                      Addresses
                                    </p>
                                    {service.domains.length === 0 ? (
                                      <p className="text-xs text-muted-foreground">
                                        {service.public ? "No URLs assigned." : "Internal only."}
                                      </p>
                                    ) : (
                                      <ul className="space-y-2">
                                        {service.domains.map((domain) => (
                                          <li key={domain.id} className="text-xs">
                                            <span className="flex flex-wrap items-center gap-2">
                                              <a
                                                className="break-all text-primary hover:underline"
                                                href={`https://${domain.hostname}`}
                                                target="_blank"
                                                rel="noreferrer"
                                              >
                                                {domain.hostname}
                                              </a>
                                              <CopyButton
                                                ariaLabel={`Copy ${domain.hostname}`}
                                                label="Copy"
                                                value={`https://${domain.hostname}`}
                                              />
                                              <span className="text-muted-foreground">
                                                Routing: {domain.dnsStatus} · {domain.applyStatus} · HTTPS: {domain.sslStatus}
                                              </span>
                                            </span>
                                            {domain.failureMessage ? (
                                              <p className="text-destructive">{domain.failureMessage}</p>
                                            ) : null}
                                          </li>
                                        ))}
                                      </ul>
                                    )}
                                  </div>
                                </div>
                                <div className="grid gap-4 px-2 pb-3 text-sm sm:grid-cols-2 lg:grid-cols-4">
                                  <div className="sm:col-span-2 lg:col-span-2">
                                    <p className="text-xs font-medium text-muted-foreground">
                                      Image reference
                                    </p>
                                    <p className="break-all font-mono text-xs">
                                      {service.image}
                                    </p>
                                  </div>
                                  <div>
                                    <p className="text-xs font-medium text-muted-foreground">
                                      Container port
                                    </p>
                                    <p>{service.port}</p>
                                  </div>
                                  <div>
                                    <p className="text-xs font-medium text-muted-foreground">
                                      Routing path
                                    </p>
                                    <p>{service.path || "Not configured"}</p>
                                  </div>
                                  <div>
                                    <p className="text-xs font-medium text-muted-foreground">
                                      Health-check path
                                    </p>
                                    <p>
                                      {service.healthPath || "Not configured"}
                                    </p>
                                  </div>
                                  {Object.keys(service.storage ?? {}).length > 0 ? (
                                    <div className="sm:col-span-2">
                                      <p className="text-xs font-medium text-muted-foreground">Durable storage</p>
                                      {Object.entries(service.storage ?? {}).map(([name, mount]) => (
                                        <p key={name} className="break-all text-xs">
                                          {name} · {mount.sizeGb} GB · <code>{mount.mountPath}</code>
                                        </p>
                                      ))}
                                    </div>
                                  ) : null}
                                  {service.domains.some(
                                    (domain) => domain.status === "disabled",
                                  ) ? (
                                    <div>
                                      <p className="text-xs font-medium text-muted-foreground">
                                        Disabled URLs
                                      </p>
                                      <p>
                                        {
                                          service.domains.filter(
                                            (domain) =>
                                              domain.status === "disabled",
                                          ).length
                                        }
                                      </p>
                                    </div>
                                  ) : null}
                                </div>
                              </TableCell>
                            </TableRow>
                          ) : null}
                        </Fragment>
                      );
                    })}
                  </TableBody>
                </Table>
              )}
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent className="space-y-4" value="secrets">
          <Card>
            <CardHeader>
              <CardTitle>Service secrets</CardTitle>
              <CollectionToolbar
                label="secret services"
                {...secretCollection.toolbar}
                onRefresh={() =>
                  refreshSecretServices().catch((error) =>
                    setSecretServiceError(String(error)),
                  )
                }
              />
            </CardHeader>
            <CardContent className="space-y-4">
              {secretServiceError ? (
                <p className="text-sm text-destructive">{secretServiceError}</p>
              ) : null}
              <div className="grid gap-3 sm:max-w-md sm:grid-cols-[1fr_auto]">
                <div className="space-y-2">
                  <Label htmlFor="service-secret-name">Service name</Label>
                  <Input
                    id="service-secret-name"
                    onChange={(event) =>
                      setManualSecretsServiceName(event.target.value)
                    }
                    placeholder="web"
                    value={manualSecretsServiceName}
                  />
                </div>
                <div className="flex items-end">
                  <Button
                    disabled={!manualSecretsServiceName.trim()}
                    onClick={() => void createSecretService()}
                    variant="outline"
                  >
                    Add service
                  </Button>
                </div>
              </div>
              {secretServices.length === 0 ? (
                <div className="rounded-md border border-dashed p-4 text-sm text-muted-foreground">
                  Add service names before the first deployment, then attach
                  secrets to each service.
                </div>
              ) : (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Service</TableHead>
                      <TableHead>Secrets</TableHead>
                      <TableHead>Updated</TableHead>
                      <TableHead className="text-right">Actions</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {secretCollection.records.map((service) => (
                      <TableRow key={service.id}>
                        <TableCell data-label="Service" className="font-medium">
                          {editingSecretServiceId === service.id ? (
                            <Input
                              aria-label={`Service name for ${service.serviceName}`}
                              className="max-w-56"
                              onChange={(event) =>
                                setEditingSecretServiceName(event.target.value)
                              }
                              value={editingSecretServiceName}
                            />
                          ) : (
                            service.serviceName
                          )}
                        </TableCell>
                        <TableCell data-label="Secrets">{service.secretCount}</TableCell>
                        <TableCell data-label="Updated">{formatDate(service.updatedAt)}</TableCell>
                        <TableCell data-label="Actions">
                          {editingSecretServiceId === service.id ? (
                            <div className="flex flex-wrap justify-end gap-2">
                              <Button
                                disabled={
                                  !editingSecretServiceName.trim() ||
                                  busySecretServiceId === service.id
                                }
                                onClick={() =>
                                  void renameSecretService(service.id)
                                }
                                size="sm"
                              >
                                Save
                              </Button>
                              <Button
                                onClick={() => {
                                  setEditingSecretServiceId(null);
                                  setEditingSecretServiceName("");
                                }}
                                size="sm"
                                variant="outline"
                              >
                                Cancel
                              </Button>
                            </div>
                          ) : (
                            <div className="flex flex-wrap justify-end gap-2">
                              <Button
                                onClick={() =>
                                  setSecretsServiceName(service.serviceName)
                                }
                                size="sm"
                                variant="outline"
                              >
                                <KeyRound className="mr-2 h-4 w-4" />
                                Secrets
                              </Button>
                              <Button
                                onClick={() => {
                                  setEditingSecretServiceId(service.id);
                                  setEditingSecretServiceName(
                                    service.serviceName,
                                  );
                                }}
                                size="sm"
                                variant="outline"
                              >
                                Rename
                              </Button>
                              <Button
                                disabled={busySecretServiceId === service.id}
                                onClick={() =>
                                  void deleteSecretService(service.id)
                                }
                                size="sm"
                                variant="destructive"
                              >
                                Remove
                              </Button>
                            </div>
                          )}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent className="space-y-4" value="deployments">
          <Card>
            <CardHeader>
              <CardTitle>Deployments</CardTitle>
              <CollectionToolbar
                label="deployment history"
                {...deploymentCollection.toolbar}
                onRefresh={() => refreshDeployments()}
              />
            </CardHeader>
            <CardContent className="p-0">
              {deployments.length === 0 ? (
                <div className="p-4">
                  <EmptyState title="No deployments">
                    Deploy published image refs with the CLI or public
                    deployments API.
                  </EmptyState>
                </div>
              ) : (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>ID</TableHead>
                      <TableHead>Deployment status</TableHead>
                      <TableHead>Created</TableHead>
                      <TableHead>Completed</TableHead>
                      <TableHead />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {deploymentCollection.records.map((deployment) => (
                      <NavigableTableRow key={deployment.id} href={`/apps/${app.id}/deployments/${deployment.id}`}>
                        <TableCell data-label="ID" className="font-mono text-xs">
                          {deployment.id}
                        </TableCell>
                        <TableCell data-label="Deployment status">
                          <StatusBadge status={deployment.status} />
                        </TableCell>
                        <TableCell data-label="Created">
                          {formatDate(deployment.createdAt)}
                        </TableCell>
                        <TableCell data-label="Completed">
                          {deployment.completedAt
                            ? formatDate(deployment.completedAt)
                            : "Pending"}
                        </TableCell>
                        <TableCell data-label="Actions" className="text-right">
                          <Button asChild size="sm" variant="outline">
                            <Link
                              to={`/apps/${app.id}/deployments/${deployment.id}`}
                            >
                              Open
                            </Link>
                          </Button>
                        </TableCell>
                      </NavigableTableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent className="space-y-4" value="console">
          <AppConsolePanel app={app} />
        </TabsContent>

        <TabsContent className="space-y-4" value="logs">
          <AppTelemetryPanel
            key={`${detailScope}:logs:${logsServiceFilter ?? ""}`}
            app={app}
            serviceNames={services.map((service) => service.name)}
            initialService={logsServiceFilter}
          />
        </TabsContent>
      </Tabs>
      {runtimeAppId ? (
        <ManageServiceDomainsDialog
          key={`domains:${detailScope}`}
          onManageAppDomains={() => { setManageDomainsServiceName(null); setActiveTab("domains"); }}
          organizationId={app.organizationId}
          appId={runtimeAppId}
          serviceName={manageDomainsServiceName}
          domains={
            services.find((s) => s.name === manageDomainsServiceName)
              ?.domains ?? []
          }
          open={manageDomainsServiceName !== null}
          onOpenChange={(open) => {
            if (!open) {
              setManageDomainsServiceName(null);
            }
          }}
          onSaved={refreshServices}
        />
      ) : null}
      {envDeleteTarget ? (
        <Dialog
          open={true}
          onOpenChange={(open) => {
            if (!open && !envDeleteBusy) {
              setEnvDeleteTarget(null);
              setEnvDeleteError(null);
            }
          }}
        >
          <DialogContent>
            <DialogHeader>
              <DialogTitle>
                Delete {envDeleteTarget.appName} / {envDeleteTarget.envName}?
              </DialogTitle>
              <DialogDescription>
                This deletes only the {envDeleteTarget.envName} environment of{" "}
                {envDeleteTarget.appName} and its services, domains, secrets
                and deployments. Other environments are unaffected. Durable
                storage volumes are retained in Hetzner and continue to incur
                charges until you delete them there.
              </DialogDescription>
            </DialogHeader>
            <div className="space-y-2">
              <Label htmlFor="delete-env-confirmation">
                Type {envDeleteTarget.envName} to confirm
              </Label>
              <Input
                id="delete-env-confirmation"
                autoComplete="off"
                disabled={envDeleteBusy}
                onChange={(event) => setEnvDeleteConfirmation(event.target.value)}
                value={envDeleteConfirmation}
              />
              {envDeleteError ? (
                <p role="alert" className="text-sm text-destructive">{envDeleteError}</p>
              ) : null}
            </div>
            <DialogFooter>
              <Button
                variant="outline"
                disabled={envDeleteBusy}
                onClick={() => {
                  setEnvDeleteTarget(null);
                  setEnvDeleteError(null);
                }}
              >
                Cancel
              </Button>
              <Button
                variant="destructive"
                disabled={envDeleteBusy || envDeleteConfirmation !== envDeleteTarget.envName}
                onClick={() => {
                  const target = envDeleteTarget;
                  // Original scope/operation identity: reject + finally
                  // paths are guarded too, so a failure landing after the
                  // user switched environments can never write the error
                  // into (or un-busy) the new scope's dialog.
                  const captured = currentScope.current;
                  setEnvDeleteBusy(true);
                  setEnvDeleteError(null);
                  deleteEnvironment(target.applicationId, target.environmentId, envDeleteConfirmation)
                    .then(() => {
                      if (!scopeStillCurrent(captured)) return;
                      setEnvDeleteTarget(null);
                      navigate(dashboardRoutes.apps.path);
                    })
                    .catch((e) => {
                      if (!scopeStillCurrent(captured)) return;
                      setEnvDeleteError(e instanceof Error ? e.message : "Unable to delete environment.");
                    })
                    .finally(() => {
                      if (!scopeStillCurrent(captured)) return;
                      setEnvDeleteBusy(false);
                    });
                }}
              >
                {envDeleteBusy ? "Deleting…" : "Delete environment"}
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      ) : null}
      {runtimeAppId ? (
        <AppServiceSecretsDialog
          key={`secrets:${detailScope}`}
          appId={runtimeAppId}
          serviceName={secretsServiceName}
          open={secretsServiceName !== null}
          onOpenChange={(open) => {
            if (!open) {
              setSecretsServiceName(null);
              void refreshSecretServices();
            }
          }}
        />
      ) : null}
    </PageScaffold>
  );
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(new Date(value));
}

function DetailRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between gap-4">
      <span className="text-muted-foreground">{label}</span>
      <span>{value}</span>
    </div>
  );
}

function loadAppDetail(appId: string) {
  return Promise.all([
    getEnvironmentDetail(appId),
    listAppServiceSecretServices(appId),
  ]);
}

async function loadScoping(applicationId: string): Promise<Application> {
  if (appsFixturesEnabled()) {
    return appsFixtureApplications().find((a) => a.id === applicationId)
      ?? (() => { throw new Error(`Application ${applicationId} not found.`); })();
  }
  return getApplication(applicationId);
}
