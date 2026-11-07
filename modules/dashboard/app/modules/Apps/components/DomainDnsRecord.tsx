import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { CopyButton } from "@/core/components/CopyButton";
import { useId, useState } from "react";

type Props = { type: "TXT" | "A" | "AAAA"; name: string; value: string };

export function DomainDnsRecord({ type, name, value }: Props) {
  const id = useId();
  const [zone, setZone] = useState("");
  const normalized = zone.trim().toLowerCase().replace(/\.$/, "");
  const matches = name === normalized || name.endsWith(`.${normalized}`);
  const shortName = normalized && matches ? name === normalized ? "@" : name.slice(0, -(normalized.length + 1)) : name;
  return (
    <div className="space-y-4">
      <dl className="divide-y rounded-lg border text-sm">
        <div className="grid grid-cols-[3rem_minmax(0,1fr)] sm:grid-cols-[4rem_minmax(0,1fr)] items-center gap-3 p-3"><dt className="text-muted-foreground">Type</dt><dd className="font-mono font-medium">{type}</dd></div>
        <div className="grid grid-cols-[3rem_minmax(0,1fr)] sm:grid-cols-[4rem_minmax(0,1fr)] items-start gap-3 p-3"><dt className="pt-1 text-muted-foreground">Name</dt><dd className="flex min-w-0 items-start justify-between gap-2"><code className="break-all pt-1 text-xs sm:text-sm">{shortName}</code><CopyButton value={shortName} ariaLabel={`Copy ${type} record name`} label="" /></dd></div>
        <div className="grid grid-cols-[3rem_minmax(0,1fr)] sm:grid-cols-[4rem_minmax(0,1fr)] items-start gap-3 p-3"><dt className="pt-1 text-muted-foreground">Value</dt><dd className="flex min-w-0 items-start justify-between gap-2"><code className="break-all pt-1 text-xs sm:text-sm">{value}</code><CopyButton value={value} ariaLabel={`Copy ${type} record value`} label="" /></dd></div>
        <div className="grid grid-cols-[3rem_minmax(0,1fr)] sm:grid-cols-[4rem_minmax(0,1fr)] items-center gap-3 p-3"><dt className="text-muted-foreground">TTL</dt><dd>Auto / default</dd></div>
      </dl>
      <details className="text-sm">
        <summary className="cursor-pointer text-muted-foreground">Does your provider add the domain name automatically?</summary>
        <div className="mt-3 space-y-2">
          <Label htmlFor={id}>DNS zone</Label>
          <Input id={id} placeholder="example.com" value={zone} onChange={event => setZone(event.target.value)} />
          <p className="text-xs text-muted-foreground">Enter the zone shown in your provider (for example, example.com). The record name above will shorten to match, including @ for the apex.</p>
          {normalized && !matches ? <p role="alert" className="text-xs text-destructive">This record does not belong to that DNS zone.</p> : null}
        </div>
      </details>
    </div>
  );
}
