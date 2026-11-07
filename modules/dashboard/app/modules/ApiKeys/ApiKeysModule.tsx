import { CollectionToolbar } from "@/core/components/CollectionToolbar";
import { Disclosure } from "@/core/components/Disclosure";
import {
  compareRecords,
  options,
  useCollectionQuery,
} from "@/core/components/collectionQuery";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
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
import { KeyRound, Plus } from "lucide-react";
import { FormEvent, useCallback, useEffect, useRef, useState } from "react";
import { createApiKey, listApiKeys, revokeApiKey } from "../../api/apiKeysApi";
import { useAuth } from "../../core/auth/AuthProvider";
import { CopyButton } from "../../core/components/CopyButton";
import { EmptyState } from "../../core/components/EmptyState";
import { PageScaffold } from "../../core/components/PageScaffold";
import type { ApiKeyResponse, ApiKeyScope } from "../../models/controlPlane";

const scopes: ApiKeyScope[] = [
  "apps:read",
  "apps:write",
  "deployments:create",
  "deployments:read",
  "api_keys:read",
  "api_keys:write",
  "logs:read",
  "telemetry:manage",
];

export function ApiKeysModule() {
  const { selectedOrganization } = useAuth();
  const canManage = ["owner", "admin"].includes(
    selectedOrganization?.role?.toLowerCase() ?? "",
  );
  const createButton = useRef<HTMLButtonElement>(null);
  const pending = useRef<object | null>(null);
  const [query, setQuery] = useCollectionQuery(
    `${selectedOrganization?.id}:api-keys`,
    "createdAt",
    "desc",
  );
  const [loading, setLoading] = useState(false);
  const [keys, setKeys] = useState<ApiKeyResponse[]>([]);
  const [createdToken, setCreatedToken] = useState<string | null>(null);
  const [isCreateOpen, setIsCreateOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    if (!selectedOrganization || pending.current) return;
    const request = {};
    pending.current = request;
    setLoading(true);
    setError(null);
    try {
      const next = await listApiKeys(selectedOrganization.id);
      if (pending.current === request) setKeys(next);
    } catch (error) {
      if (pending.current === request)
        setError(
          error instanceof Error ? error.message : "Unable to load API keys.",
        );
    } finally {
      if (pending.current === request) {
        pending.current = null;
        setLoading(false);
      }
    }
  }, [selectedOrganization?.id]);
  useEffect(() => {
    pending.current = null;
    setKeys([]);
    setCreatedToken(null);
    void refresh();
    return () => {
      pending.current = null;
    };
  }, [refresh]);

  async function revoke(keyId: string) {
    try {
      await revokeApiKey(keyId);
      await refresh();
    } catch (error) {
      setError(
        error instanceof Error ? error.message : "Unable to revoke key.",
      );
    }
  }

  const state = (key: ApiKeyResponse) =>
    key.revokedAt
      ? "Revoked"
      : key.expiresAt && Date.parse(key.expiresAt) <= Date.now()
        ? "Expired"
        : "Active";
  const filtered = keys
    .filter(
      (key) =>
        `${key.name} ${key.prefix}`
          .toLowerCase()
          .includes(query.search.trim().toLowerCase()) &&
        (!query.filters.state || state(key) === query.filters.state) &&
        (!query.filters.scope ||
          key.scopes.includes(query.filters.scope as ApiKeyScope)),
    )
    .sort((a, b) =>
      compareRecords(
        a[query.sort as "name" | "createdAt" | "lastUsedAt"],
        b[query.sort as "name" | "createdAt" | "lastUsedAt"],
        query.direction,
        a.id,
        b.id,
      ),
    );
  return (
    <PageScaffold
      breadcrumbs={[{ label: "Settings" }, { label: "API Keys" }]}
      title="API Keys"
    >
      <CollectionToolbar
        label="API keys"
        query={query}
        onChange={setQuery}
        onRefresh={() => refresh()}
        loading={loading}
        count={filtered.length}
        filters={[
          {
            id: "state",
            label: "State",
            options: options(["Active", "Expired", "Revoked"]),
          },
          { id: "scope", label: "Scope", options: options(scopes) },
        ]}
        sorts={[
          { value: "name", label: "Name" },
          { value: "createdAt", label: "Created time" },
          { value: "lastUsedAt", label: "Last used" },
        ]}
        primary={{
          id: "create",
          ref: createButton,
          label: "Create key",
          visible: canManage,
          onClick: () => setIsCreateOpen(true),
        }}
      />
      <Dialog open={isCreateOpen} onOpenChange={setIsCreateOpen}>
        <DialogContent onCloseAutoFocus={event => { event.preventDefault(); createButton.current?.focus(); }}>
          <DialogHeader>
            <DialogTitle>Create API key</DialogTitle>
            <DialogDescription>
              Raw tokens are shown once and cannot be recovered.
            </DialogDescription>
          </DialogHeader>
          <CreateKeyForm
            onCreated={(token) => {
              setCreatedToken(token);
              setIsCreateOpen(false);
              void refresh();
            }}
            organizationId={selectedOrganization?.id}
          />
        </DialogContent>
      </Dialog>
      {createdToken ? (
        <Card className="border-border bg-warning-surface">
          <CardHeader>
            <CardTitle>Copy this token now</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-wrap items-center gap-3">
            <code className="max-w-full break-all rounded-md bg-background px-3 py-2 text-sm">
              {createdToken}
            </code>
            <CopyButton value={createdToken} />
            <Button onClick={() => setCreatedToken(null)} variant="outline">
              Done
            </Button>
          </CardContent>
        </Card>
      ) : null}
      {error ? <p className="text-sm text-destructive">{error}</p> : null}
      {loading ? <p role="status">Loading API keys…</p> : null}
      {keys.length > 0 && filtered.length === 0 ? (
        <p>No matching API keys.</p>
      ) : null}
      {!loading && !error && keys.length === 0 ? (
        <EmptyState
          action={
            canManage ? (
              <Button onClick={() => setIsCreateOpen(true)}>
                <KeyRound className="h-4 w-4" />
                Create key
              </Button>
            ) : undefined
          }
          title="No API keys"
        >
          Create a scoped key for CI or CLI deployments.
        </EmptyState>
      ) : (
        <Card>
          <CardContent className="p-0">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Prefix</TableHead>
                  <TableHead>Scopes</TableHead>
                  <TableHead>Created</TableHead>
                  <TableHead>Last used</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead />
                </TableRow>
              </TableHeader>
              <TableBody>
                {filtered.map((key) => (
                  <TableRow key={key.id}>
                    <TableCell data-label="Name">{key.name}</TableCell>
                    <TableCell data-label="Prefix" className="font-mono text-xs">
                      {key.prefix}••••
                    </TableCell>
                    <TableCell data-label="Scopes" className="max-w-sm text-xs">
                      <Disclosure label={`${key.scopes.length} permissions`}>
                        {key.scopes.join(", ")}
                      </Disclosure>
                    </TableCell>
                    <TableCell data-label="Created">{formatDate(key.createdAt)}</TableCell>
                    <TableCell data-label="Last used">
                      {key.lastUsedAt
                        ? formatDate(key.lastUsedAt)
                        : key.lastUsedAt === null
                          ? "Never used"
                          : "Unavailable"}
                    </TableCell>
                    <TableCell data-label="Status">
                      <Disclosure label={state(key)}>
                        {key.expiresAt ? `Expires ${formatDate(key.expiresAt)}` : "No expiry"}
                        {key.revokedAt ? ` · Revoked ${formatDate(key.revokedAt)}` : ""}
                      </Disclosure>
                    </TableCell>
                    <TableCell data-label="Actions" className="text-right">
                      {canManage && !key.revokedAt ? (
                        <AlertDialog>
                          <AlertDialogTrigger asChild>
                            <Button size="sm" variant="outline">
                              Revoke
                            </Button>
                          </AlertDialogTrigger>
                          <AlertDialogContent>
                            <AlertDialogHeader>
                              <AlertDialogTitle>
                                Revoke API key?
                              </AlertDialogTitle>
                              <AlertDialogDescription>
                                This immediately blocks automation using this
                                token.
                              </AlertDialogDescription>
                            </AlertDialogHeader>
                            <AlertDialogFooter>
                              <AlertDialogCancel>Cancel</AlertDialogCancel>
                              <AlertDialogAction
                                onClick={() => void revoke(key.id)}
                              >
                                Revoke
                              </AlertDialogAction>
                            </AlertDialogFooter>
                          </AlertDialogContent>
                        </AlertDialog>
                      ) : null}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}
    </PageScaffold>
  );
}

function CreateKeyForm({
  organizationId,
  onCreated,
}: {
  organizationId?: string;
  onCreated: (token: string) => void;
}) {
  const [name, setName] = useState("");
  const [selectedScopes, setSelectedScopes] = useState<ApiKeyScope[]>([
    "apps:read",
    "deployments:create",
    "deployments:read",
  ]);
  const [expiresAt, setExpiresAt] = useState("");
  const [error, setError] = useState<string | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!organizationId) {
      return;
    }

    try {
      const key = await createApiKey({
        organizationId,
        name,
        scopes: selectedScopes,
        expiresAt: expiresAt ? new Date(expiresAt).toISOString() : null,
      });
      onCreated(key.token);
    } catch (submitError) {
      setError(
        submitError instanceof Error
          ? submitError.message
          : "Unable to create API key.",
      );
    }
  }

  return (
    <form className="space-y-4" onSubmit={submit}>
      <div className="grid gap-2">
        <Label htmlFor="key-name">Name</Label>
        <Input
          id="key-name"
          onChange={(event) => setName(event.target.value)}
          required
          value={name}
        />
      </div>
      <div className="grid gap-2">
        <Label>Scopes</Label>
        <div className="grid gap-2 sm:grid-cols-2">
          {scopes.map((scope) => (
            <label className="flex items-center gap-2 text-sm" key={scope}>
              <Checkbox
                checked={selectedScopes.includes(scope)}
                onCheckedChange={(checked) =>
                  setSelectedScopes((current) =>
                    checked
                      ? [...current, scope]
                      : current.filter(
                          (currentScope) => currentScope !== scope,
                        ),
                  )
                }
              />
              {scope}
            </label>
          ))}
        </div>
      </div>
      <div className="grid gap-2">
        <Label htmlFor="expires">Expires</Label>
        <Input
          id="expires"
          onChange={(event) => setExpiresAt(event.target.value)}
          type="datetime-local"
          value={expiresAt}
        />
      </div>
      {error ? <p className="text-sm text-destructive">{error}</p> : null}
      <Button type="submit">Create key</Button>
    </form>
  );
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(new Date(value));
}
