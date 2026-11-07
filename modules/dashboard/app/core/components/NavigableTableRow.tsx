import type { MouseEvent, ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { TableRow } from "@/components/ui/table";

const interactiveSelector = "a,button,input,select,textarea,[role=button],[role=menuitem],[data-row-interactive]";

export function isNonInteractiveNavigationClick(event: MouseEvent<HTMLElement>) {
  return !(event.target as HTMLElement).closest(interactiveSelector) && !window.getSelection()?.toString();
}

export function NavigableTableRow({ href, children }: { href: string; children: ReactNode }) {
  const navigate = useNavigate();

  function follow(event: MouseEvent<HTMLTableRowElement>) {
    if (!isNonInteractiveNavigationClick(event)) return;
    if (event.metaKey || event.ctrlKey) window.open(href, "_blank", "noopener,noreferrer");
    else navigate(href);
  }

  return (
    <TableRow className="cursor-pointer focus-within:bg-muted/50" onClick={follow}>
      {children}
    </TableRow>
  );
}
