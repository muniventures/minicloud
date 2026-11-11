import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import {
  browserNavigation, getAccountFlow, startAccountFlow, startProviderRedirect, submitAccountFlow,
  type BrowserFlow, type FlowKind, type FlowNode
} from "../../api/accountApi";
import { accountErrorMessage } from "../../api/apiClient";
import { useAuth } from "../../core/auth/AuthProvider";
import { ApiError } from "../../models/controlPlane";

function fieldLabel(name: string) {
  switch (name) {
    case "identifier": case "traits.email": case "email": return "Email";
    case "password": return "Password";
    case "code": return "Recovery code";
    case "traits.name": return "Display name";
    case "traits.name.first": return "First name";
    case "traits.name.last": return "Last name";
    default: return "Account detail";
  }
}

function supportedNode(node: FlowNode) {
  if (node.name === "password" && node.type !== "password" ||
      node.name === "csrf_token" && node.type !== "hidden") return false;
  return ["default", "password", "profile", "code", "recovery"].includes(node.group) &&
    ["hidden", "text", "email", "password", "submit"].includes(node.type) &&
    ["csrf_token", "identifier", "password", "method", "email", "code", "traits.email", "traits.name", "traits.name.first", "traits.name.last"].includes(node.name);
}

export function AccountFlowForm({ kind, returnTo = "/apps", flowId, notice }: {
  kind: FlowKind;
  returnTo?: string;
  /** Re-read this existing flow once (provider callback continuation) instead of starting a new one. */
  flowId?: string;
  notice?: string;
}) {
  const { capabilities, refreshSession } = useAuth();
  const [flow, setFlow] = useState<BrowserFlow | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [complete, setComplete] = useState(false);
  const [restartRequired, setRestartRequired] = useState(false);
  const [revision, setRevision] = useState(0);
  const generation = useRef(0);
  const enabled = kind === "recovery" ? capabilities?.emailRecovery === true : capabilities?.password === true;

  useEffect(() => {
    const current = ++generation.current;
    setFlow(null);
    setComplete(false);
    setError(null);
    setRestartRequired(false);
    if (!enabled) return;
    setBusy(true);
    (flowId && revision === 0 ? getAccountFlow(kind, flowId) : startAccountFlow(kind, returnTo)).then(result => {
      if (current === generation.current) setFlow(result.flow);
    }).catch(loadError => {
      if (current === generation.current) {
        setError(accountErrorMessage(loadError));
        setRestartRequired(true);
      }
    }).finally(() => { if (current === generation.current) setBusy(false); });
    return () => { ++generation.current; };
  }, [enabled, kind, returnTo, revision, flowId]);

  async function continueWithGoogle() {
    if (!flow || busy || restartRequired) return;
    if (Date.parse(flow.expiresAt) <= Date.now()) {
      setError("This form expired. Restart to try again.");
      setRestartRequired(true);
      return;
    }
    const current = generation.current;
    setBusy(true);
    setError(null);
    try {
      const destination = await startProviderRedirect(flow, "google", returnTo);
      if (current === generation.current) browserNavigation.assign(destination);
    } catch (providerError) {
      if (current !== generation.current) return;
      setError(accountErrorMessage(providerError));
      setRestartRequired(true);
      setBusy(false);
    }
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!flow || busy || restartRequired) return;
    if (Date.parse(flow.expiresAt) <= Date.now()) {
      setError("This form expired. Restart to try again.");
      setRestartRequired(true);
      return;
    }
    const current = generation.current;
    const form = event.currentTarget;
    const submitter = (event.nativeEvent as SubmitEvent).submitter;
    const data = new FormData(form);
    const fields: Record<string, string> = {};
    for (const [name, value] of data) {
      if (typeof value === "string") fields[name] = value;
    }
    if (submitter instanceof HTMLButtonElement && submitter.name) fields[submitter.name] = submitter.value;
    setBusy(true);
    setError(null);
    try {
      const result = await submitAccountFlow(flow, fields);
      if (current !== generation.current) return;
      form.reset();
      setFlow(result.flow);
      if (result.session) {
        setComplete(true);
        await refreshSession();
      }
    } catch (submitError) {
      if (current !== generation.current) return;
      form.reset();
      setError(accountErrorMessage(submitError));
      if (submitError instanceof ApiError && submitError.code === "auth_flow_invalid") {
        try {
          const result = await getAccountFlow(kind, flow.id);
          if (current === generation.current) setFlow(result.flow);
        } catch (readError) {
          if (current === generation.current) {
            setError(accountErrorMessage(readError));
            setRestartRequired(true);
          }
        }
      } else {
        setRestartRequired(true);
      }
    } finally {
      if (current === generation.current) setBusy(false);
    }
  }

  const nodes = flow?.ui.nodes.filter(supportedNode) ?? [];
  const google = capabilities?.google === true && kind !== "recovery" &&
    flow?.ui.providers?.some(provider => provider.id === "google") === true;
  const hasSubmit = nodes.some(node => node.type === "submit");
  return (
    <Card className="w-full max-w-md">
      <CardHeader>
        <CardTitle>{kind === "login" ? "Sign in" : kind === "settings" ? "Account settings" : "Recover account"}</CardTitle>
        <CardDescription>{kind === "settings" ? "Change your password." : "Access your Minicloud account."}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {!enabled ? <p>This authentication method is not enabled. Contact the host administrator.</p> : null}
        {busy && !flow ? <p role="status">Loading account form</p> : null}
        {complete ? <p role="status">{kind === "settings" ? "Password updated." : "Account verified."}</p> : null}
        {notice ? <p role="alert" className="text-sm text-destructive">{notice}</p> : null}
        {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
        {flow && !complete ? (
          <form key={flow.id} className="space-y-4" onSubmit={submit}>
            {flow.ui.messages.length > 0 ? <p role="status">Review your account details to continue.</p> : null}
            {nodes.map((node, index) => {
              const id = `account-${index}`;
              const key = `${node.group}:${node.name}:${node.type}`;
              if (node.type === "hidden") return <input key={key} type="hidden" name={node.name} value={node.value ?? ""} />;
              if (node.type === "submit") return (
                <Button key={key} type="submit" name={node.name} value={node.value ?? ""} disabled={busy || restartRequired}>
                  {kind === "login" ? "Sign in" : kind === "settings" ? node.value === "profile" ? "Save profile" : "Save password" : "Continue recovery"}
                </Button>
              );
              return (
                <div key={key} className="space-y-2">
                  <Label htmlFor={id}>{fieldLabel(node.name)}</Label>
                  <Input id={id} name={node.name} type={node.name === "password" ? "password" : node.type}
                    autoComplete={node.name === "password" ? kind === "settings" ? "new-password" : "current-password" : node.name === "code" ? "one-time-code" :
                      node.name === "traits.name.first" ? "given-name" : node.name === "traits.name.last" ? "family-name" : "username"}
                    defaultValue={node.name === "password" || node.name === "code" ? "" : node.value ?? ""}
                    required={node.required} disabled={busy || restartRequired} aria-invalid={node.messages.length > 0} />
                  {node.messages.length > 0 ? <p className="text-sm text-destructive">Check this field.</p> : null}
                </div>
              );
            })}
            {!hasSubmit ? <p role="alert">This form cannot continue. Restart or contact the host administrator.</p> : null}
          </form>
        ) : null}
        {flow && !complete && google ? (
          <Button type="button" variant="outline" className="w-full" disabled={busy || restartRequired} onClick={continueWithGoogle}>
            {kind === "settings" ? "Link Google account" : "Continue with Google"}
          </Button>
        ) : null}
        {enabled ? <Button variant="outline" disabled={busy} onClick={() => setRevision(value => value + 1)}>Restart form</Button> : null}
        {kind === "login" && capabilities?.emailRecovery ? <Link className="block underline" to="/recovery" state={{ from: returnTo }}>Forgot password?</Link> : null}
        {kind === "recovery" ? <Link className="block underline" to="/sign-in" state={{ from: returnTo }}>Return to sign in</Link> : null}
      </CardContent>
    </Card>
  );
}
