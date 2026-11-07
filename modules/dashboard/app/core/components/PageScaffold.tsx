import type { ReactNode } from "react";
import { Link } from "react-router-dom";

export type BreadcrumbItem = {
  label: string;
  to?: string;
};

export function PageScaffold({
  title,
  actions,
  header,
  breadcrumbs,
  children
}: {
  title: string;
  actions?: ReactNode;
  header?: ReactNode;
  breadcrumbs?: BreadcrumbItem[];
  children: ReactNode;
}) {
  return (
    <div className="space-y-4">
      {breadcrumbs?.length ? (
        <nav aria-label="Breadcrumb" className="flex flex-wrap items-center gap-1 text-sm text-muted-foreground">
          {breadcrumbs.map((item, index) => (
            <span className="flex items-center gap-1" key={`${item.label}-${index}`}>
              {index > 0 ? <span aria-hidden="true">/</span> : null}
              {item.to && index < breadcrumbs.length - 1 ? (
                <Link className="hover:text-foreground hover:underline" to={item.to}>
                  {item.label}
                </Link>
              ) : (
                <span className={index === breadcrumbs.length - 1 ? "text-foreground" : undefined}>{item.label}</span>
              )}
            </span>
          ))}
        </nav>
      ) : null}
      {header ?? (
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h1 className="text-2xl font-medium tracking-tight">{title}</h1>
          {actions ? <div className="flex items-center gap-2">{actions}</div> : null}
        </div>
      )}
      {children}
    </div>
  );
}
