import { useState, type ReactNode } from "react";
import { ChevronDown } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible";
import { cn } from "@/lib/utils";
import { Accordion, AccordionContent, AccordionItem, AccordionTrigger } from "@/components/ui/accordion";

export function SectionDisclosure({
  label,
  children,
  className,
}: {
  label: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <Accordion type="single" collapsible className={className}>
      <AccordionItem value="content" className="border-0">
        <AccordionTrigger className="px-2">{label}</AccordionTrigger>
        <AccordionContent className="px-2">{children}</AccordionContent>
      </AccordionItem>
    </Accordion>
  );
}

export function Disclosure({
  label,
  children,
  className,
  contentClassName,
}: {
  label: ReactNode;
  children: ReactNode;
  className?: string;
  contentClassName?: string;
}) {
  const [open, setOpen] = useState(false);
  return (
    <Collapsible open={open} onOpenChange={setOpen} className={className}>
      <CollapsibleTrigger asChild>
        <Button variant="ghost" className="h-auto w-full justify-between px-2 py-2 text-left">
          <span>{label}</span>
          <ChevronDown className={cn("h-4 w-4 shrink-0 transition-transform", open && "rotate-180")} />
        </Button>
      </CollapsibleTrigger>
      <CollapsibleContent className={cn("px-2 pb-2", contentClassName)}>
        {children}
      </CollapsibleContent>
    </Collapsible>
  );
}

export function DiagnosticsDisclosure({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState(false);
  return (
    <Collapsible open={open} onOpenChange={setOpen} className="mt-2">
      <CollapsibleTrigger asChild>
        <Button size="sm" variant="outline">
          {open ? "Hide diagnostics" : "Show diagnostics"}
        </Button>
      </CollapsibleTrigger>
      <CollapsibleContent className="mt-2 max-h-80 overflow-auto rounded-md border bg-muted/40 p-3 font-mono text-xs whitespace-pre-wrap break-words">
        {children}
      </CollapsibleContent>
    </Collapsible>
  );
}
