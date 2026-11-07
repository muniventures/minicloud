import { useCallback, useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import type { DeploymentResponse } from "@/models/controlPlane";
import { DeploymentServiceEndpoint } from "./DeploymentServiceEndpoint";

type Service = DeploymentResponse["services"][number];
export function DeploymentServicesTable({ services }: { services: Service[] }) {
  const [selected, setSelected] = useState<string | null>(null);
  const trigger = useRef<HTMLButtonElement | null>(null);
  const panel = useRef<HTMLElement>(null);
  useEffect(() => {
    if (selected) {
      panel.current?.focus({ preventScroll: true });
      if (window.innerWidth < 1280)
        panel.current?.scrollIntoView({ block: "nearest" });
    }
  }, [selected]);
  const service = services.find((item) => item.name === selected);
  const close = useCallback(() => {
    setSelected(null);
    trigger.current?.focus();
  }, []);
  return (
    <section
      aria-labelledby="deployment-services-heading"
      className="rounded-xl border bg-background"
    >
      <div className="flex flex-wrap items-center justify-between gap-2 border-b px-4 py-4 sm:px-6">
        <h2 id="deployment-services-heading" className="font-medium">
          Services{" "}
          <span className="ml-2 text-sm font-normal text-muted-foreground">
            {services.length}
          </span>
        </h2>
        <span className="text-xs text-muted-foreground">
          Deployment configuration
        </span>
      </div>
      <div
        className={
          service
            ? "grid min-w-0 xl:grid-cols-[minmax(0,1.5fr)_minmax(0,1fr)]"
            : "min-w-0"
        }
      >
        <div className="min-w-0 px-4 pb-2 sm:px-6">
          <table
            aria-label="Deployment services"
            className="w-full table-fixed text-sm"
          >
            <thead>
              <tr className="text-left text-xs text-muted-foreground">
                <th scope="col" className="w-1/4 py-3 pr-2 font-normal">
                  Service
                </th>
                <th scope="col" className="py-3 font-normal">
                  Endpoints
                </th>
                <th scope="col" className="w-16 py-3">
                  <span className="sr-only">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody className="divide-y border-t">
              {services.map((item) => (
                <tr
                  key={item.name}
                  className={
                    service?.name === item.name ? "bg-muted/40" : undefined
                  }
                >
                  <th
                    scope="row"
                    className="break-words py-3 pr-2 text-left align-top text-xs font-medium sm:text-sm"
                  >
                    {item.name}
                  </th>
                  <td className="min-w-0 py-3 align-top">
                    <div className="space-y-2">
                      {item.urls?.length ? (
                        item.urls.map((url) => (
                          <DeploymentServiceEndpoint
                            key={url}
                            url={url}
                            name={item.name}
                          />
                        ))
                      ) : (
                        <span className="text-xs text-muted-foreground">
                          {item.public
                            ? "No endpoint configured"
                            : "Internal only"}
                        </span>
                      )}
                    </div>
                  </td>
                  <td className="py-2 text-right align-top">
                    <Button
                      variant="link"
                      size="sm"
                      className="px-1 text-xs"
                      aria-label={`Details for ${item.name}`}
                      aria-controls="deployment-service-inspector"
                      aria-expanded={service?.name === item.name}
                      onClick={(event) => {
                        trigger.current = event.currentTarget;
                        setSelected(item.name);
                      }}
                    >
                      Details
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {!services.length ? (
            <p className="py-6 text-sm text-muted-foreground">
              No services configured.
            </p>
          ) : null}
        </div>
        {service ? (
          <aside
            ref={panel}
            tabIndex={-1}
            id="deployment-service-inspector"
            aria-label={`${service.name} configuration`}
            className="min-w-0 border-t bg-muted/20 p-4 sm:p-6 xl:border-l xl:border-t-0"
            onKeyDown={(event) => {
              if (event.key === "Escape") close();
            }}
          >
            <div className="flex items-center justify-between gap-2">
              <h3 className="break-all font-medium">{service.name}</h3>
              <Button variant="ghost" size="sm" onClick={close}>
                Close details
              </Button>
            </div>
            <dl className="mt-4 space-y-4 text-sm">
              <div>
                <dt className="text-xs text-muted-foreground">Access</dt>
                <dd className="mt-1">
                  {service.public ? "Public" : "Internal"}
                </dd>
              </div>
              <div>
                <dt className="text-xs text-muted-foreground">
                  Image reference
                </dt>
                <dd className="mt-1 break-all font-mono text-xs">
                  {service.image}
                </dd>
              </div>
              <div>
                <dt className="text-xs text-muted-foreground">
                  Container port
                </dt>
                <dd className="mt-1">{service.port}</dd>
              </div>
              <div>
                <dt className="text-xs text-muted-foreground">Routing path</dt>
                <dd className="mt-1 break-all">{service.path || "—"}</dd>
              </div>
              <div>
                <dt className="text-xs text-muted-foreground">
                  Health-check path
                </dt>
                <dd className="mt-1 break-all">
                  {service.healthPath || "Not configured"}
                </dd>
              </div>
            </dl>
          </aside>
        ) : null}
      </div>
    </section>
  );
}
