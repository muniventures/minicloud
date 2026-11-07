import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Power, PowerOff } from "lucide-react";
import { useState } from "react";
import { updateTelemetrySettings } from "../../../api/telemetryApi";
import type { TelemetrySettingsResponse } from "../../../models/controlPlane";

type Props = {
  appId: string;
  settings: TelemetrySettingsResponse | null;
  onChanged: (settings: TelemetrySettingsResponse | null) => void;
};

export function TelemetrySettingsCard({ appId, settings, onChanged }: Props) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function setEnabled(enabled: boolean) {
    setBusy(true);
    setError(null);
    try {
      onChanged(await updateTelemetrySettings(appId, enabled));
    } catch (updateError) {
      setError(updateError instanceof Error ? updateError.message : "Unable to update telemetry settings.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between">
        <CardTitle>Application logs setup</CardTitle>
        {settings?.enabled ? (
          <Button disabled={busy} onClick={() => void setEnabled(false)} size="sm" variant="outline">
            <PowerOff className="mr-2 h-4 w-4" />
            Disable
          </Button>
        ) : (
          <Button disabled={busy} onClick={() => void setEnabled(true)} size="sm">
            <Power className="mr-2 h-4 w-4" />
            Enable
          </Button>
        )}
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        {error ? <p className="text-sm text-destructive">{error}</p> : null}
        {!settings ? (
          <p className="text-muted-foreground">
            Application logs are not enabled. Enable them to create service credentials and start exporting
            OpenTelemetry logs. Enabling settings alone does not collect logs until an exporter is configured.
          </p>
        ) : (
          <>
            <div className="flex justify-between gap-4">
              <span className="text-muted-foreground">Status</span>
              <span>{settings.enabled ? "Enabled" : "Disabled"}</span>
            </div>
            <div className="flex justify-between gap-4">
              <span className="text-muted-foreground">Retention</span>
              <span>{settings.retentionPolicy}</span>
            </div>
            <div className="flex justify-between gap-4">
              <span className="text-muted-foreground">Last received</span>
              <span>{settings.lastAcceptedAt ? formatDate(settings.lastAcceptedAt) : "No logs received yet"}</span>
            </div>
            {settings.provisioningStatus === "provisioning" ? (
              <p className="rounded-md border p-3 text-muted-foreground">
                Log storage is provisioning for this app. Reads will appear once provisioning completes.
              </p>
            ) : null}
            {settings.provisioningStatus === "failed" ? (
              <p className="rounded-md border border-destructive/40 p-3 text-destructive">
                Log storage provisioning failed. Reads are unavailable; retry later.
              </p>
            ) : null}
            <div className="rounded-md border p-3">
              <p className="font-medium">Exporter configuration</p>
              <p className="mt-1 text-xs text-muted-foreground">
                Point the OpenTelemetry logs exporter at the signal-specific endpoint and store the complete
                headers value as the reserved app secret MINICLOUD_OTLP_LOGS_HEADERS, mapped with secretEnv:
              </p>
              <pre className="mt-2 overflow-x-auto rounded-md bg-muted p-3 font-mono text-xs text-muted-foreground">
{`OTEL_EXPORTER_OTLP_LOGS_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=https://telemetry.cloud.muni.dev/otlp/v1/logs
OTEL_EXPORTER_OTLP_LOGS_HEADERS=Authorization=Bearer%20<token>
OTEL_SERVICE_NAME=<service-name>`}
              </pre>
              <pre className="mt-2 overflow-x-auto rounded-md bg-muted p-3 font-mono text-xs text-muted-foreground">
{`secretEnv:
  OTEL_EXPORTER_OTLP_LOGS_HEADERS: MINICLOUD_OTLP_LOGS_HEADERS`}
              </pre>
              <p className="mt-2 text-xs text-muted-foreground">
                Only the logs signal is supported; metrics and traces exporters are rejected. Branch apps need
                their own credential — the parent token is never copied to a branch.
              </p>
            </div>
          </>
        )}
      </CardContent>
    </Card>
  );
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}
