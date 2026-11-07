import { useCallback, useState } from "react";
export type CollectionQuery = {
  search: string;
  filters: Record<string, string>;
  sort: string;
  direction: "asc" | "desc";
  view: string;
};
export function useCollectionQuery(
  namespace: string,
  sort = "name",
  direction: "asc" | "desc" = "asc",
) {
  const defaults: CollectionQuery = {
    search: "",
    filters: {},
    sort,
    direction,
    view: "cards",
  };
  const read = () => {
    try {
      const saved = JSON.parse(sessionStorage.getItem(namespace) ?? "{}");
      return {
        search: typeof saved.search === "string" ? saved.search : defaults.search,
        filters: saved.filters && typeof saved.filters === "object" ? Object.fromEntries(Object.entries(saved.filters).filter(([,value]) => typeof value === "string")) as Record<string,string> : {},
        sort: typeof saved.sort === "string" ? saved.sort : defaults.sort,
        direction: saved.direction === "asc" || saved.direction === "desc" ? saved.direction : defaults.direction,
        view: typeof saved.view === "string" ? saved.view : defaults.view,
      } as CollectionQuery;
    } catch {
      return defaults;
    }
  };
  const [stored, setStored] = useState<{
    namespace: string;
    query: CollectionQuery;
  }>(() => ({ namespace, query: read() }));
  const query = stored.namespace === namespace ? stored.query : read();
  const update = useCallback(
    (next: CollectionQuery) => {
      setStored({ namespace, query: next });
      try {
        sessionStorage.setItem(namespace, JSON.stringify(next));
      } catch {
        /* Storage may be disabled. */
      }
    },
    [namespace],
  );
  return [query, update] as const;
}
export function compareRecords(
  a: string | number | null | undefined,
  b: string | number | null | undefined,
  direction: string,
  aid: string,
  bid: string,
) {
  const missing = (v: unknown) =>
    v === null ||
    v === undefined ||
    v === "" ||
    (typeof v === "number" && Number.isNaN(v));
  if (missing(a) || missing(b))
    return missing(a) === missing(b)
      ? aid.localeCompare(bid)
      : missing(a)
        ? 1
        : -1;
  const order =
    typeof a === "number" && typeof b === "number"
      ? a - b
      : String(a).localeCompare(String(b));
  return order
    ? order * (direction === "desc" ? -1 : 1)
    : aid.localeCompare(bid);
}
export const deploymentStatuses = [
  "waiting_for_build",
  "queued",
  "dispatching",
  "provisioning",
  "deploying",
  "verifying",
  "succeeded",
  "failed",
  "canceled",
];
export const options = (values: string[]) =>
  values.map((value) => ({
    value,
    label:
      (
        {
          waiting_for_build: "Waiting for build",
          createdAt: "Created time",
          updatedAt: "Updated time",
          lastUsedAt: "Last used",
          name: "Name",
          role: "Role",
          status: "Status",
          state: "State",
          image: "Image",
          message: "Message",
          content: "Content",
          source: "Source",
          access: "Access",
          service: "Service",
          plan: "Plan",
          id: "ID",
        } as Record<string, string>
      )[value] ?? value,
  }));
