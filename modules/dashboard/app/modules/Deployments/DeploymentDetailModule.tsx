import { getEnvironmentDetail } from "@/api/environmentsApi";
import { CollectionToolbar } from "@/core/components/CollectionToolbar";
import { RecordToolbar } from "@/core/components/RecordToolbar";
import { useRecordCollection } from "@/core/components/useRecordCollection";
import { options } from "@/core/components/collectionQuery";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { RotateCw } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { useParams } from "react-router-dom";
import { PageScaffold } from "../../core/components/PageScaffold";
import {
  dashboardRoutes,
} from "../../core/routing/dashboardRoutes";
import { StatusBadge } from "../../core/components/StatusBadge";
import { isActiveDeployment } from "./deploymentDetailPresentation";
import { deploymentAttemptTiming } from "./deploymentProgressPresentation";
import type {
  DeploymentEventResponse,
  DeploymentResponse,
} from "@/models/controlPlane";
import { useDeploymentDetail } from "./useDeploymentDetail";
import { DeploymentProgressPanel } from "./components/DeploymentProgressPanel";
import { DeploymentServicesTable } from "./components/DeploymentServicesTable";

export function DeploymentDetailModule() {
  const { deploymentId } = useParams();
  const [appName, setAppName] = useState("Deployment");
  const {
    snapshot,
    error,
    refreshing: isRefreshing,
    dispatching: isDispatching,
    canceling: isCanceling,
    cancel: handleCancel,
    refresh: handleRefresh,
    dispatch: handleManualDispatch,
  } = useDeploymentDetail(deploymentId);
  const deployment = snapshot?.deployment;
  const events = snapshot?.events ?? [];
  const activities = snapshot?.activities ?? [];
  const logs = snapshot?.logs ?? [];
  useEffect(() => {
    if (!deployment?.appId) return;
    let canceled = false;
    void getEnvironmentDetail(deployment.appId)
      .then((app) => {
        if (!canceled) setAppName(app.name);
      })
      .catch(() => {
        if (!canceled) setAppName("App unavailable");
      });
    return () => {
      canceled = true;
    };
  }, [deployment?.appId]);

  const sources = useMemo(
    () => ["all", ...Array.from(new Set(logs.map((log) => log.source)))],
    [logs],
  );
  const logCollection = useRecordCollection(
    `${deploymentId}:logs`,
    logs,
    {
      createdAt: (item) => item.createdAt,
      source: (item) => item.source,
      content: (item) => item.content,
    },
    (item) => item.id,
  );
  const activityCollection = useRecordCollection(
    `${deploymentId}:activities`,
    activities,
    { name: (item) => item.name, status: (item) => item.status },
    (item) => item.id,
  );
  const filteredLogs = logCollection.records;
  const activeDeployment = isActiveDeployment(deployment?.status);

  if (error && !deployment) {
    return <p className="text-sm text-destructive">{error}</p>;
  }

  if (!deployment) {
    return (
      <p className="text-sm text-muted-foreground">Loading deployment...</p>
    );
  }

  return (
    <PageScaffold
      breadcrumbs={[
        { label: "Deployments", to: dashboardRoutes.deployments.path },
        { label: "Deployment details" },
      ]}
      header={
        <RecordToolbar
          title={appName}
          back={{ to: dashboardRoutes.deployments.path, label: "Back to deployments" }}
          status={<StatusBadge status={activeDeployment && deployment.cancellationRequestedAt ? "canceling" : deployment.status} />}
          metadata={
            <>
              <span>{formatTiming(deployment, events)}</span>
            </>
          }
          refreshLabel={`Refresh deployment ${deployment.id}`}
          onRefresh={handleRefresh}
          loading={isRefreshing}
          secondary={activeDeployment ? {
            id: "cancel",
            label: isCanceling || deployment.cancellationRequestedAt ? "Canceling..." : "Cancel deployment",
            disabled: isCanceling || Boolean(deployment.cancellationRequestedAt),
            onClick: () => void handleCancel(),
          } : undefined}
          primary={{
            id: "redeploy",
            label: isDispatching ? "Dispatching..." : "Redeploy",
            icon: <RotateCw className="h-4 w-4" />,
            disabled: isDispatching || activeDeployment || deployment.services.length === 0,
            loading: isDispatching,
            onClick: () => void handleManualDispatch(),
          }}
        />
      }
      title={appName}
    >
      {activeDeployment && deployment.cancellationRequestedAt ? (
        <p role="status" className="text-sm text-muted-foreground">Cancellation requested. Waiting for execution to stop. Changes already applied are not restored.</p>
      ) : null}
      {error ? (
        <p role="alert" className="text-destructive">
          {error}
        </p>
      ) : null}
      <Tabs defaultValue="progress">
        <TabsList className="h-auto max-w-full justify-start overflow-x-auto">
          <TabsTrigger value="progress">Progress</TabsTrigger>
          <TabsTrigger value="services">Services</TabsTrigger>
          <TabsTrigger value="activities">Activities</TabsTrigger>
          <TabsTrigger value="logs">Logs</TabsTrigger>
        </TabsList>
        <TabsContent value="progress" className="space-y-4">
          <DeploymentProgressPanel
            deployment={deployment}
            events={events}
            logs={logs}
            observedAt={snapshot!.observedAt}
          />
        </TabsContent>
        <TabsContent value="services">
          <DeploymentServicesTable services={deployment.services} />
        </TabsContent>
        <TabsContent value="activities">
          <CollectionToolbar
            label="activities"
            {...activityCollection.toolbar}
            onRefresh={() => handleRefresh()}
            loading={isRefreshing}
          />
          <Card>
            <CardContent className="space-y-3 pt-4">
              {activities.length === 0 ? (
                <p className="text-sm text-muted-foreground">
                  No console activities recorded yet.
                </p>
              ) : null}
              {activityCollection.records.map((activity) => (
                <div
                  className="rounded-md border border-border p-3 text-sm"
                  key={activity.id}
                >
                  <div className="flex flex-wrap items-center justify-between gap-3">
                    <div>
                      <div className="font-medium">{activity.name}</div>
                      <div className="text-xs text-muted-foreground">
                        {activity.conclusion
                          ? `${activity.status} / ${activity.conclusion}`
                          : activity.status}
                      </div>
                    </div>
                    {activity.completedAt ? (
                      <span className="text-xs text-muted-foreground">
                        {formatDate(activity.completedAt)}
                      </span>
                    ) : null}
                  </div>
                  {activity.steps.length > 0 ? (
                    <div className="mt-3 grid gap-2">
                      {activity.steps.map((step) => (
                        <div
                          className="flex items-center justify-between gap-3 rounded-md bg-muted px-3 py-2 text-xs"
                          key={`${activity.id}-${step.number}`}
                        >
                          <span>{step.name}</span>
                          <span className="text-muted-foreground">
                            {step.conclusion ?? step.status}
                          </span>
                        </div>
                      ))}
                    </div>
                  ) : null}
                </div>
              ))}
            </CardContent>
          </Card>
        </TabsContent>
        <TabsContent value="logs">
          <Card>
            <CardHeader>
              <div className="flex flex-wrap items-center justify-between gap-3">
                <CardTitle>Logs</CardTitle>
                <CollectionToolbar
                  label="logs"
                  {...logCollection.toolbar}
                  onRefresh={() => handleRefresh()}
                  loading={isRefreshing}
                  filters={[
                    {
                      id: "source",
                      label: "Source",
                      options: options(
                        sources.filter((value) => value !== "all"),
                      ),
                    },
                  ]}
                />
              </div>
            </CardHeader>
            <CardContent>
              <pre className="max-h-[460px] overflow-auto rounded-md bg-muted p-4 text-xs text-muted-foreground">
                {filteredLogs.length > 0
                  ? filteredLogs
                      .map((log) => `[${log.source}] ${log.content}`)
                      .join("\n")
                  : "No logs recorded yet."}
              </pre>
            </CardContent>
          </Card>
        </TabsContent>
      </Tabs>
    </PageScaffold>
  );
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(new Date(value));
}

function formatTiming(
  deployment: DeploymentResponse,
  events: DeploymentEventResponse[],
) {
  if (deployment.completedAt)
    return `Completed ${formatDate(deployment.completedAt)}`;
  return `Started ${formatDate(deploymentAttemptTiming(deployment, events).totalStartedAt)}`;
}
