import { useRef, useState, type ReactNode } from "react";
import { ArrowLeft, RefreshCw } from "lucide-react";
import { Link } from "react-router-dom";
import { Button } from "@/components/ui/button";
import {
  CollectionActions,
  type CollectionAction,
} from "./CollectionToolbar";

export function RecordToolbar({
  title,
  back,
  status,
  metadata,
  refreshLabel,
  onRefresh,
  loading,
  primary,
  secondary,
  more,
}: {
  title: string;
  back?: { to: string; label: string };
  status?: ReactNode;
  metadata?: ReactNode;
  refreshLabel: string;
  onRefresh: () => void | Promise<unknown>;
  loading?: boolean;
  primary?: CollectionAction;
  secondary?: CollectionAction;
  more?: CollectionAction[][];
}) {
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

  return (
    <div className="flex flex-wrap items-start justify-between gap-3" aria-label={`${title} record controls`}>
      <div className="flex min-w-0 items-start gap-2">
        {back ? (
          <Button asChild size="icon" variant="ghost">
            <Link to={back.to} aria-label={back.label}>
              <ArrowLeft className="h-4 w-4" />
            </Link>
          </Button>
        ) : null}
        <div className="min-w-0 space-y-1">
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="min-w-0 truncate text-2xl font-medium tracking-tight">{title}</h1>
            {status}
          </div>
          {metadata ? <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">{metadata}</div> : null}
        </div>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <Button
          variant="outline"
          size="icon"
          aria-label={refreshLabel}
          disabled={busy}
          onClick={() => void refresh()}
        >
          <RefreshCw className={busy ? "h-4 w-4 animate-spin" : "h-4 w-4"} />
        </Button>
        <CollectionActions primary={primary} secondary={secondary} more={more} />
      </div>
    </div>
  );
}
