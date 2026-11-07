import { useAppServiceInventory } from "./useAppServiceInventory";
import { CollectionToolbar } from "@/core/components/CollectionToolbar";
import { useRecordCollection } from "@/core/components/useRecordCollection";
import { ServiceUrls } from "@/core/components/ServiceUrls";
import { NavigableTableRow } from "@/core/components/NavigableTableRow";
import { RecordToolbar } from "@/core/components/RecordToolbar";
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
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
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
  getApp,
  listApps,
  listAppServices,
  listAppServiceSecretServices,
  renameAppServiceSecretService,
} from "../../api/appsApi";
import { useDeploymentFeed } from "../Deployments/useDeploymentFeed";
import { useAuth } from "../../core/auth/AuthProvider";
import { CopyButton } from "../../core/components/CopyButton";
import { EmptyState } from "../../core/components/EmptyState";
import { PageScaffold } from "../../core/components/PageScaffold";
import { dashboardRoutes } from "../../core/routing/dashboardRoutes";
import { StatusBadge } from "../../core/components/StatusBadge";
import type {
  AppResponse,
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

import { DeleteAppDialog } from "./components/DeleteAppDialog";

export function AppDetailModule() {
  const { appId } = useParams();
  const navigate = useNavigate();
  const { selectedOrganization } = useAuth();
  const detailScope = JSON.stringify([selectedOrganization?.id, appId]);
  const currentScope = useRef(detailScope);
  currentScope.current = detailScope;
  const [app, setApp] = useState<AppResponse | null>(null);
  const {
    services,
    error: serviceRefreshError,
    loading: serviceLoading,
    now: observedNow,
    refresh: refreshServices,
  } = useAppServiceInventory(appId, selectedOrganization?.id);
  const [secretServices, setSecretServices] = useState<
    AppServiceSecretServiceResponse[]
  >([]);
  const { deployments, error: deploymentError, observed: deploymentsObserved, refresh: refreshDeployments } =
    useDeploymentFeed(selectedOrganization?.id, appId);
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
  const [deleteDialogOpen, setDeleteDialogOpen] = useState(false);

  useEffect(() => {
    if (!appId || !selectedOrganization) {
      return;
    }

    let live = true;
    setApp(null);
    setError(null);
    loadAppDetail(appId)
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
  }, [appId, selectedOrganization?.id]);

  const onDeletionRequested = useCallback(() => {
    setApp((current) =>
      current
        ? { ...current, deletionStatus: "queued", deletionError: null }
        : current,
    );
  }, []);

  const refreshRecord = useCallback(async () => {
    if (!appId || !selectedOrganization) return;
    setActionError(null);
    try {
      const [[nextApp, nextSecretServices]] = await Promise.all([
        loadAppDetail(appId),
        refreshDeployments(),
        refreshServices(),
      ]);
      if (currentScope.current !== detailScope) return;
      setApp(nextApp);
      setSecretServices(nextSecretServices);
    } catch (refreshError) {
      if (currentScope.current !== detailScope) return;
      setActionError(refreshError instanceof Error ? refreshError.message : "Unable to refresh app.");
    }
  }, [appId, selectedOrganization?.id, detailScope, refreshServices, refreshDeployments]);

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
        const remaining = await listApps(selectedOrganization.id);
        if (canceled) return;
        const exists = remaining.some(
          (item) =>
            item.id === app.id ||
            item.branches?.some((branch) => branch.id === app.id),
        );
        if (!exists) {
          navigate(dashboardRoutes.apps.path, { replace: true });
          return;
        }
        const next = await getApp(app.id);
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
  }, [app?.id, app?.deletionStatus, selectedOrganization, navigate]);

  const refreshSecretServices = useCallback(async () => {
    if (!appId) {
      return;
    }

    setSecretServices(await listAppServiceSecretServices(appId));
  }, [appId]);

  async function createSecretService() {
    if (!appId || !manualSecretsServiceName.trim()) {
      return;
    }

    setSecretServiceError(null);
    try {
      const created = await createAppServiceSecretService(
        appId,
        manualSecretsServiceName.trim(),
      );
      setManualSecretsServiceName("");
      await refreshSecretServices();
      setSecretsServiceName(created.serviceName);
    } catch (createError) {
      setSecretServiceError(
        createError instanceof Error
          ? createError.message
          : "Unable to add service.",
      );
    }
  }

  async function renameSecretService(serviceId: string) {
    if (!appId || !editingSecretServiceName.trim()) {
      return;
    }

    setBusySecretServiceId(serviceId);
    setSecretServiceError(null);
    try {
      await renameAppServiceSecretService(
        appId,
        serviceId,
        editingSecretServiceName.trim(),
      );
      setEditingSecretServiceId(null);
      setEditingSecretServiceName("");
      await refreshSecretServices();
    } catch (renameError) {
      setSecretServiceError(
        renameError instanceof Error
          ? renameError.message
          : "Unable to rename service.",
      );
    } finally {
      setBusySecretServiceId(null);
    }
  }

  async function deleteSecretService(serviceId: string) {
    if (
      !appId ||
      !window.confirm(
        "Remove this service from secrets management? Secrets already stored for this service remain unless deleted separately.",
      )
    ) {
      return;
    }

    setBusySecretServiceId(serviceId);
    setSecretServiceError(null);
    try {
      await deleteAppServiceSecretService(appId, serviceId);
      await refreshSecretServices();
    } catch (deleteError) {
      setSecretServiceError(
        deleteError instanceof Error
          ? deleteError.message
          : "Unable to remove service.",
      );
    } finally {
      setBusySecretServiceId(null);
    }
  }

  const serviceCollection = useRecordCollection(
    `${appId}:services`,
    services,
    {
      name: (item) => item.name,
      access: (item) => (item.public ? "Public" : "Private"),
    },
    (item) => item.name,
  );
  const branchCollection = useRecordCollection(
    `${appId}:branches`,
    app?.branches ?? [],
    { name: (item) => item.branchName, plan: (item) => item.plan },
    (item) => item.id,
  );
  const secretCollection = useRecordCollection(
    `${appId}:secret-services`,
    secretServices,
    { name: (item) => item.serviceName },
    (item) => item.id,
  );
  const deploymentCollection = useRecordCollection(
    `${appId}:history`,
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

  if (!app || app.id !== appId || app.organizationId !== selectedOrganization?.id) {
    return <p className="text-sm text-muted-foreground">Loading app...</p>;
  }

  const latestDeployment = deploymentsObserved
    ? [...deployments].sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt))[0] ?? null
    : app.latestDeployment;
  const publicServiceCount = services.filter(
    (service) => service.public,
  ).length;
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
      breadcrumbs={[
        { label: "Apps", to: dashboardRoutes.apps.path },
        { label: app.name },
      ]}
      header={
        <RecordToolbar
          title={app.name}
          back={{ to: dashboardRoutes.apps.path, label: "Back to apps" }}
          status={<StatusBadge status={latestDeployment?.status ?? "No deployments"} />}
          metadata={
            <>
              <span>{app.slug} · {app.plan} · {app.database}</span>
              <span>{services.length} services · {publicServiceCount} public · {activeDomainCount} active domains</span>
              <span>Runtime: {services.some((service) => runtimeFresh(service, observedNow) && service.runtime) ? `${runtimeRunningCount}/${services.length} observed running` : "Unavailable or stale"}</span>
            </>
          }
          refreshLabel={`Refresh ${app.name}`}
          onRefresh={refreshRecord}
          more={[[{
            id: "delete",
            label: app.deletionStatus === "failed" ? "Retry deletion" : "Delete application",
            icon: <Trash2 className="h-4 w-4" />,
            visible: !app.deletionStatus || app.deletionStatus === "failed",
            onClick: () => setDeleteDialogOpen(true),
          }]]}
        />
      }
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

      <Tabs onValueChange={setActiveTab} value={activeTab}>
        <TabsList className="flex w-full justify-start overflow-x-auto">
          <TabsTrigger
            onClick={() => setActiveTab("overview")}
            value="overview"
          >
            Overview
          </TabsTrigger>
          <TabsTrigger
            onClick={() => setActiveTab("services")}
            value="services"
          >
            Services
          </TabsTrigger>
          <TabsTrigger value="storage">Storage</TabsTrigger>
          <TabsTrigger value="domains">Domains</TabsTrigger>
          <TabsTrigger onClick={() => setActiveTab("secrets")} value="secrets">
            Secrets
          </TabsTrigger>
          <TabsTrigger
            onClick={() => setActiveTab("deployments")}
            value="deployments"
          >
            Deployments
          </TabsTrigger>
          <TabsTrigger onClick={() => setActiveTab("console")} value="console">
            Console
          </TabsTrigger>
          <TabsTrigger onClick={() => setActiveTab("logs")} value="logs">
            Application logs
          </TabsTrigger>
        </TabsList>

        <TabsContent value="storage">
          <AppStoragePanel key={app.id} appId={app.id} services={services} />
        </TabsContent>

        <TabsContent value="domains">
          <AppDomains key={app.id} organizationId={app.organizationId} appId={app.id} services={services} onSaved={refreshServices} onManageService={setManageDomainsServiceName} />
        </TabsContent>

        <TabsContent className="space-y-4" value="overview">
          <div className="grid gap-4 lg:grid-cols-[minmax(0,2fr)_minmax(16rem,1fr)]">
            <Card>
              <CardHeader>
                <CardTitle>Services</CardTitle>
                <p className="text-sm text-muted-foreground">
                  Public URLs and the latest observed runtime state.
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
                        <TableHead>URL</TableHead>
                        <TableHead>Runtime</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {services.map((service) => {
                        const urls = service.domains
                          .filter((domain) => domain.status !== "disabled")
                          .map((domain) => `https://${domain.hostname}`);

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
                            <TableCell data-label="URL">
                              <ServiceUrls
                                services={[{ urls }]}
                                emptyText={
                                  service.public
                                    ? "No URL assigned"
                                    : "Internal only"
                                }
                              />
                            </TableCell>
                            <TableCell data-label="Runtime">
                              <ServiceRuntimeStatus
                                service={service}
                                now={observedNow}
                              />
                            </TableCell>
                          </TableRow>
                        );
                      })}
                    </TableBody>
                  </Table>
                )}
              </CardContent>
            </Card>

            <Card>
              <CardHeader>
                <CardTitle>Application</CardTitle>
              </CardHeader>
              <CardContent className="space-y-5 text-sm">
                <div className="space-y-2">
                  <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                    Latest deployment
                  </p>
                  {latestDeployment ? (
                    <div className="space-y-2">
                      <StatusBadge status={latestDeployment.status} />
                      <p className="text-muted-foreground">
                        {latestDeployment.completedAt
                          ? `Completed ${formatDate(latestDeployment.completedAt)}`
                          : "In progress"}
                      </p>
                    </div>
                  ) : (
                    <p className="text-muted-foreground">No deployments yet.</p>
                  )}
                </div>
                <div className="space-y-2 border-t pt-4">
                  <DetailRow label="Plan" value={app.plan.toUpperCase()} />
                  <DetailRow label="Database" value={app.database} />
                  <DetailRow label="Services" value={String(services.length)} />
                </div>
              </CardContent>
            </Card>
          </div>
          {!app.parentAppId ? (
            <Card>
              <CardHeader>
                <CardTitle>Branches</CardTitle>
                <CollectionToolbar
                  label="branches"
                  {...branchCollection.toolbar}
                  onRefresh={() =>
                    getApp(app.id)
                      .then(setApp)
                      .catch((error) => setActionError(String(error)))
                  }
                />
              </CardHeader>
              <CardContent className="p-0">
                {(app.branches ?? []).length === 0 ? (
                  <div className="p-4">
                    <p className="text-sm text-muted-foreground">
                      No branch deployments.
                    </p>
                  </div>
                ) : (
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>Branch</TableHead>
                        <TableHead>Plan</TableHead>
                        <TableHead>Service URLs</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {branchCollection.records.map((branch) => (
                        <NavigableTableRow key={branch.id} href={`/apps/${branch.id}`}>
                          <TableCell data-label="Branch" className="font-medium">
                            <Link
                              className="text-primary hover:underline"
                              to={`/apps/${branch.id}`}
                            >
                              {branch.branchName}
                            </Link>
                          </TableCell>
                          <TableCell data-label="Plan">{branch.plan.toUpperCase()}</TableCell>
                          <TableCell data-label="Service URLs">
                            <ServiceUrls services={[{ urls: branch.urls }]} />
                          </TableCell>
                        </NavigableTableRow>
                      ))}
                    </TableBody>
                  </Table>
                )}
              </CardContent>
            </Card>
          ) : null}

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
                      <TableHead className="w-[40%]">URL</TableHead>
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
                            <TableCell data-label="URL">
                              {!service.public ? (
                                <span className="text-sm text-muted-foreground">
                                  Internal only
                                </span>
                              ) : enabledDomains.length === 0 ? (
                                <span className="text-sm text-muted-foreground">
                                  No URL assigned
                                </span>
                              ) : (
                                <div className="space-y-3">
                                  {enabledDomains.map((domain) => {
                                    const url = `https://${domain.hostname}`;
                                    return (
                                      <div
                                        key={domain.id}
                                        className="min-w-0 space-y-1"
                                      >
                                        <div className="flex min-w-0 items-center gap-2">
                                          <a
                                            className="min-w-0 break-all text-primary hover:underline"
                                            href={url}
                                            target="_blank"
                                            rel="noreferrer"
                                          >
                                            {url}
                                          </a>
                                          <CopyButton
                                            ariaLabel={`Copy ${service.name} URL`}
                                            label="Copy"
                                            value={url}
                                          />
                                        </div>
                                        <p className="text-xs text-muted-foreground">
                                          Domain {domain.status} · HTTPS{" "}
                                          {domain.sslStatus}
                                        </p>
                                        {domain.failureMessage ? (
                                          <p className="text-xs text-destructive">
                                            {domain.failureMessage}
                                          </p>
                                        ) : null}
                                      </div>
                                    );
                                  })}
                                </div>
                              )}
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
                                    onClick={() => setActiveTab("logs")}
                                  >
                                    <FileText className="mr-2 h-4 w-4" />
                                    <span>View service logs</span>
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
                                <div className="grid gap-4 px-2 py-3 text-sm sm:grid-cols-2 lg:grid-cols-4">
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
            app={app}
            serviceNames={services.map((service) => service.name)}
          />
        </TabsContent>
      </Tabs>
      {appId ? (
        <ManageServiceDomainsDialog
          onManageAppDomains={() => { setManageDomainsServiceName(null); setActiveTab("domains"); }}
          organizationId={app.organizationId}
          appId={appId}
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
      <DeleteAppDialog
        app={app}
        onRequested={onDeletionRequested}
        open={deleteDialogOpen}
        onOpenChange={setDeleteDialogOpen}
        showTrigger={false}
      />
      {appId ? (
        <AppServiceSecretsDialog
          appId={appId}
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
    getApp(appId),
    listAppServiceSecretServices(appId),
  ]);
}
