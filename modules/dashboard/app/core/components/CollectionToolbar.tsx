import { useRef, useState, type ReactNode, type Ref } from "react";
import {
  ArrowDown,
  ArrowUp,
  LayoutGrid,
  List,
  RefreshCw,
  Search,
  SlidersHorizontal,
  MoreHorizontal,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Separator } from "@/components/ui/separator";
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "@/components/ui/tooltip";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import type { CollectionQuery } from "./collectionQuery";
export type CollectionAction = {
  ref?: Ref<HTMLButtonElement>;
  id: string;
  label: string;
  icon?: ReactNode;
  onClick: () => void;
  visible?: boolean;
  disabled?: boolean;
  loading?: boolean;
};
export type CollectionFilter = {
  id: string;
  label: string;
  defaultLabel?: string;
  options?: { value: string; label: string }[];
  type?: "date" | "text" | "number";
};

function SelectControl({
  label,
  value,
  placeholder,
  options,
  onValueChange,
}: {
  label: string;
  value: string;
  placeholder?: string;
  options: { value: string; label: string }[];
  onValueChange: (value: string) => void;
}) {
  return (
    <div className="grid gap-1.5">
      <Label>{label}</Label>
      <Select value={value || "__all"} onValueChange={(next) => onValueChange(next === "__all" ? "" : next)}>
        <SelectTrigger aria-label={label}>
          <SelectValue placeholder={placeholder ?? "All"} />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="__all">{placeholder ?? "All"}</SelectItem>
          {options.map((option) => (
            <SelectItem key={option.value} value={option.value}>
              {option.label}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </div>
  );
}

export function CollectionActions({
  primary,
  secondary,
  more = [],
}: {
  primary?: CollectionAction;
  secondary?: CollectionAction;
  more?: CollectionAction[][];
}) {
  const actions = more
    .flat()
    .filter(
      (action) =>
        action.visible !== false &&
        action.id !== primary?.id &&
        action.id !== secondary?.id,
    );

  if (
    actions.length === 0 &&
    ![primary, secondary].some(
      (action) => action && action.visible !== false,
    )
  ) {
    return null;
  }

  return (
    <div className="flex flex-wrap items-center gap-2">
      {[secondary, primary].map((action, index) =>
        action && action.visible !== false ? (
          <Button
            key={action.id}
            ref={action.ref}
            variant={index === 0 ? "outline" : "default"}
            disabled={action.disabled || action.loading}
            onClick={action.onClick}
          >
            {action.icon}
            {action.label}
          </Button>
        ) : null,
      )}
      {actions.length ? (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="outline" size="icon" aria-label="More">
              <MoreHorizontal className="h-4 w-4" />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            {actions.map((action) => (
              <DropdownMenuItem
                key={action.id}
                disabled={action.disabled || action.loading}
                onClick={action.onClick}
              >
                {action.icon}
                {action.label}
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
      ) : null}
    </div>
  );
}
type Props = {
  label: string;
  searchSupported?: boolean;
  query: CollectionQuery;
  onChange: (query: CollectionQuery) => void;
  onRefresh: () => void | Promise<unknown>;
  loading?: boolean;
  count: number;
  countLabel?: string;
  filters?: CollectionFilter[];
  sorts: { value: string; label: string }[];
  views?: { value: string; label: string }[];
  primary?: CollectionAction;
  secondary?: CollectionAction;
  more?: CollectionAction[][];
};
export function CollectionToolbar({
  label,
  searchSupported = true,
  countLabel,
  query,
  onChange,
  onRefresh,
  loading,
  count,
  filters = [],
  sorts,
  views,
  primary,
  secondary,
  more = [],
}: Props) {
  const refreshingRef = useRef(false);
  const [refreshing, setRefreshing] = useState(false);
  const busy = loading || refreshing;
  async function refresh() {
    if (busy || refreshingRef.current) return;
    refreshingRef.current = true;
    setRefreshing(true);
    try {
      await onRefresh();
    } finally {
      refreshingRef.current = false;
      setRefreshing(false);
    }
  }
  const active = Object.values(query.filters).filter(Boolean).length;
  return (
    <div
      className="flex flex-wrap items-center justify-between gap-2"
      aria-label={`${label} controls`}
    >
      <div className="flex w-full min-w-0 items-center gap-2 lg:w-auto lg:flex-1">
        <Button
          variant="outline"
          size="icon"
          aria-label={`Refresh ${label}`}
          disabled={busy}
          onClick={() => void refresh()}
        >
          <RefreshCw className={busy ? "h-4 w-4 animate-spin" : "h-4 w-4"} />
        </Button>
        {searchSupported ? (
          <div className="relative min-w-0 flex-1 sm:max-w-72">
            <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              aria-label={`Search ${label}`}
              placeholder={`Search ${label}`}
              className="pl-9"
              value={query.search}
              onChange={(event) =>
                onChange({ ...query, search: event.target.value })
              }
            />
          </div>
        ) : null}
        <Popover>
          <PopoverTrigger asChild>
            <Button variant="outline" className="shrink-0">
              <SlidersHorizontal className="h-4 w-4" />
              <span className="hidden sm:inline">Filter &amp; sort</span>
              {active ? (
                <span className="flex h-5 min-w-5 items-center justify-center rounded-full bg-primary px-1.5 text-xs text-primary-foreground">
                  {active}
                </span>
              ) : null}
            </Button>
          </PopoverTrigger>
          <PopoverContent
            align="start"
            collisionPadding={12}
            className="max-h-[min(32rem,var(--radix-popover-content-available-height))] w-[min(20rem,calc(100vw-1.5rem))] overflow-y-auto p-0"
          >
            <div className="px-4 py-3">
              <h2 className="text-sm font-semibold">Filter &amp; sort</h2>
              <p className="text-sm text-muted-foreground">
                {count} {countLabel ?? "matching"} {label}
              </p>
            </div>
            <Separator />
            <div className="grid gap-4 p-4">
              {filters.map((filter) =>
                filter.type ? (
                  <div key={filter.id} className="grid gap-1.5">
                    <Label htmlFor={`${label}-${filter.id}`}>{filter.label}</Label>
                  <Input
                    id={`${label}-${filter.id}`}
                    type={filter.type}
                    value={query.filters[filter.id] ?? ""}
                    onChange={(event) =>
                      onChange({
                        ...query,
                        filters: {
                          ...query.filters,
                          [filter.id]: event.target.value,
                        },
                      })
                    }
                  />
                  </div>
                ) : (
                  <SelectControl
                    key={filter.id}
                    label={filter.label}
                    value={query.filters[filter.id] ?? ""}
                    placeholder={filter.defaultLabel}
                    options={filter.options ?? []}
                    onValueChange={(value) =>
                      onChange({
                        ...query,
                        filters: {
                          ...query.filters,
                          [filter.id]: value,
                        },
                      })
                    }
                  />
                )
              )}
              {sorts.length ? (
              <>
                {filters.length ? <Separator /> : null}
                <SelectControl
                    label="Sort by"
                    value={query.sort}
                    options={sorts}
                    onValueChange={(value) =>
                      onChange({ ...query, sort: value })
                    }
                />
                <div className="grid gap-1.5">
                  <Label>Direction</Label>
                  <div className="grid grid-cols-2 rounded-md border border-input p-0.5" role="group" aria-label="Sort direction">
                    {(["asc", "desc"] as const).map((direction) => {
                      const Icon = direction === "asc" ? ArrowUp : ArrowDown;
                      return (
                        <Button
                          key={direction}
                          type="button"
                          variant={query.direction === direction ? "secondary" : "ghost"}
                          className="h-8"
                          aria-pressed={query.direction === direction}
                          onClick={() => onChange({ ...query, direction })}
                        >
                          <Icon />
                          {direction === "asc" ? "Ascending" : "Descending"}
                        </Button>
                      );
                    })}
                  </div>
                </div>
              </>
            ) : (
              <p className="text-sm text-muted-foreground">
                Order is provided by the server.
              </p>
              )}
              <div className="flex justify-end">
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={active === 0}
                  onClick={() => onChange({ ...query, filters: {} })}
                >
                  Clear filters
                </Button>
              </div>
            </div>
          </PopoverContent>
        </Popover>
        {views?.length ? (
          <TooltipProvider delayDuration={300}>
            <div className="inline-flex h-9 shrink-0 items-center rounded-md border border-input" role="group" aria-label="View mode">
              {views.map((view) => {
                const name = view.value === "cards" ? "Card view" : view.value === "list" ? "List view" : view.label;
                const Icon = view.value === "cards" ? LayoutGrid : List;
                return (
                  <Tooltip key={view.value}>
                    <TooltipTrigger asChild>
                      <Button
                        size="icon"
                        className="h-full min-h-0 w-9 rounded-none first:rounded-l-[calc(var(--radius)-1px)] last:rounded-r-[calc(var(--radius)-1px)]"
                        aria-label={name}
                        aria-pressed={query.view === view.value}
                        variant={query.view === view.value ? "secondary" : "ghost"}
                        onClick={() => onChange({ ...query, view: view.value })}
                      >
                        <Icon className="h-4 w-4" />
                      </Button>
                    </TooltipTrigger>
                    <TooltipContent>{name}</TooltipContent>
                  </Tooltip>
                );
              })}
            </div>
          </TooltipProvider>
        ) : null}
      </div>
      <CollectionActions primary={primary} secondary={secondary} more={more} />
      {count === 0 && (query.search || active > 0) ? (
        <p className="w-full text-sm text-muted-foreground" role="status">
          No matching {label}.
        </p>
      ) : null}
    </div>
  );
}
