import { CollectionToolbar } from "@/core/components/CollectionToolbar";
import {
  compareRecords,
  options,
  useCollectionQuery,
} from "@/core/components/collectionQuery";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { useAuth } from "../../core/auth/AuthProvider";
import { PageScaffold } from "../../core/components/PageScaffold";

export function TeamSettingsModule() {
  const {
    profile,
    selectedOrganization,
    refreshProfile,
    status,
    profileError,
  } = useAuth();

  const [query, setQuery] = useCollectionQuery(
    `${selectedOrganization?.id}:memberships`,
  );
  const memberships = (profile?.organizations ?? [])
    .filter(
      (org) =>
        `${org.name} ${org.slug}`
          .toLowerCase()
          .includes(query.search.trim().toLowerCase()) &&
        (!query.filters.role || org.role === query.filters.role),
    )
    .sort((a, b) =>
      compareRecords(
        query.sort === "role" ? a.role : a.name,
        query.sort === "role" ? b.role : b.name,
        query.direction,
        a.id,
        b.id,
      ),
    );
  return (
    <PageScaffold
      breadcrumbs={[{ label: "Settings" }, { label: "Team" }]}
      title="Team"
    >
      <Card>
        <CardHeader>
          <CardTitle>Organization</CardTitle>
        </CardHeader>
        <CardContent className="space-y-2 text-sm">
          <div className="flex justify-between gap-4">
            <span className="text-muted-foreground">Name</span>
            <span>{selectedOrganization?.name}</span>
          </div>
          <div className="flex justify-between gap-4">
            <span className="text-muted-foreground">Slug</span>
            <span>{selectedOrganization?.slug}</span>
          </div>
          <div className="flex justify-between gap-4">
            <span className="text-muted-foreground">Your role</span>
            <span>{selectedOrganization?.role}</span>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Memberships</CardTitle>
          <CollectionToolbar
            label="memberships"
            query={query}
            onChange={setQuery}
            onRefresh={() => refreshProfile()}
            loading={status === "initializing"}
            count={memberships.length}
            filters={[
              {
                id: "role",
                label: "Role",
                options: options([
                  ...new Set(
                    (profile?.organizations ?? []).map((org) => org.role),
                  ),
                ]),
              },
            ]}
            sorts={options(["name", "role"])}
          />
          {profileError ? <p role="alert">{profileError}</p> : null}
          {!memberships.length ? <p>No matching memberships.</p> : null}
        </CardHeader>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Organization</TableHead>
                <TableHead>Slug</TableHead>
                <TableHead>Role</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {memberships.map((organization) => (
                <TableRow key={organization.id}>
                  <TableCell data-label="Organization">{organization.name}</TableCell>
                  <TableCell data-label="Slug">{organization.slug}</TableCell>
                  <TableCell data-label="Role">{organization.role}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>
    </PageScaffold>
  );
}
