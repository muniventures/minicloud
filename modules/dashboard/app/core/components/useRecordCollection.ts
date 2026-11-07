import { useAuth } from "../auth/AuthProvider";
import { compareRecords, options, useCollectionQuery } from "./collectionQuery";
/** For embedded, complete collections. The caller remains the data/refresh owner. */
export function useRecordCollection<T>(
  namespace: string,
  records: T[],
  fields: Record<string, (record: T) => string | null | undefined>,
  id: (record: T) => string,
  defaultSort = Object.keys(fields)[0],
  direction: "asc" | "desc" = "asc",
) {
  const { selectedOrganization } = useAuth();
  const [query, onChange] = useCollectionQuery(
    `${selectedOrganization?.id}:${namespace}`,
    defaultSort,
    direction,
  );
  const filtered = records
    .filter(
      (record) =>
        Object.values(fields).some((field) =>
          (field(record) ?? "")
            .toLowerCase()
            .includes(query.search.trim().toLowerCase()),
        ) &&
        Object.entries(query.filters).every(
          ([field, value]) => !value || fields[field]?.(record) === value,
        ),
    )
    .sort((a, b) =>
      compareRecords(
        fields[query.sort]?.(a),
        fields[query.sort]?.(b),
        query.direction,
        id(a),
        id(b),
      ),
    );
  return {
    records: filtered,
    toolbar: {
      query,
      onChange,
      count: filtered.length,
      sorts: options(Object.keys(fields)),
      filters: Object.keys(fields)
        .filter((field) =>
          ["access", "status", "state", "plan", "service"].includes(field),
        )
        .map((field) => ({
          id: field,
          label: options([field])[0].label,
          options: options([
            ...new Set(
              records
                .map(fields[field])
                .filter((value): value is string => Boolean(value)),
            ),
          ]),
        })),
    },
  };
}
