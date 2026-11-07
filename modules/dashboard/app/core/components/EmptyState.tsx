import type { ReactNode } from "react";

export function EmptyState({ title, children, action }: { title: string; children: ReactNode; action?: ReactNode }) {
  return (
    <div className="flex min-h-52 flex-col items-center justify-center rounded-lg border border-dashed border-border bg-card px-6 py-10 text-center">
      <h2 className="text-base font-semibold">{title}</h2>
      <div className="mt-2 max-w-xl text-sm text-muted-foreground">{children}</div>
      {action ? <div className="mt-4">{action}</div> : null}
    </div>
  );
}
