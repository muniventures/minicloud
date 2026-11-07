import { CopyButton } from "@/core/components/CopyButton";

export function ServiceUrls({ services, emptyText = "No service URLs" }: {
  services: { name?: string; urls?: string[] }[];
  emptyText?: string;
}) {
  const links = services.flatMap((service) =>
    [...new Set(service.urls ?? [])].filter((url) => url.trim()).map((url) => ({ name: service.name, url })),
  );

  if (!links.length) {
    return <span className="text-muted-foreground">{emptyText}</span>;
  }

  return (
    <div className="space-y-2">
      {links.map(({ name, url }) => (
        <div className="flex flex-wrap items-center gap-2" key={`${name ?? ""}:${url}`}>
          {name ? <span className="font-medium">{name}</span> : null}
          <a className="break-all text-primary hover:underline" href={url} rel="noreferrer" target="_blank">{url}</a>
          <CopyButton value={url} />
        </div>
      ))}
    </div>
  );
}
