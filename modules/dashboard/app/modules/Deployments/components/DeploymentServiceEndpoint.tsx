import { useCallback, useEffect, useRef, useState } from "react";
import { Check, Copy } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";

export function DeploymentServiceEndpoint({
  url,
  name,
}: {
  url: string;
  name: string;
}) {
  const [copied, setCopied] = useState(false);
  const [error, setError] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(timer.current), []);
  const copy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(url);
      setCopied(true);
      setError(false);
      clearTimeout(timer.current);
      timer.current = setTimeout(() => setCopied(false), 1400);
    } catch {
      setError(true);
    }
  }, [url]);
  return (
    <div>
      <div className="flex min-w-0 items-start justify-between gap-1 sm:gap-2">
        <a
          href={url}
          target="_blank"
          rel="noreferrer"
          className="min-w-0 break-all py-1 text-xs text-foreground underline-offset-4 hover:underline"
          aria-label={`Open ${name} endpoint ${url}`}
        >
          {url.replace(/^https?:\/\//, "")}
        </a>
        <TooltipProvider>
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="ghost"
                size="icon"
                className="h-7 w-7 shrink-0"
                onClick={() => void copy()}
                aria-label={
                  copied
                    ? `${name} endpoint copied`
                    : `Copy ${name} endpoint ${url}`
                }
              >
                {copied ? (
                  <Check className="h-3.5 w-3.5" />
                ) : (
                  <Copy className="h-3.5 w-3.5" />
                )}
              </Button>
            </TooltipTrigger>
            <TooltipContent>
              {copied ? "Copied" : "Copy endpoint"}
            </TooltipContent>
          </Tooltip>
        </TooltipProvider>
      </div>
      {error ? (
        <p role="status" className="text-xs text-destructive">
          Unable to copy. Select the endpoint to copy it.
        </p>
      ) : null}
    </div>
  );
}
