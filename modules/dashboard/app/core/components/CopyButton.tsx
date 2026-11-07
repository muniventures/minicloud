import { Button } from "@/components/ui/button";
import { Check, Copy } from "lucide-react";
import { useState } from "react";

export function CopyButton({ value, label = "Copy", ariaLabel }: { value: string; label?: string; ariaLabel?: string }) {
  const [copied, setCopied] = useState(false);

  async function copyValue() {
    await navigator.clipboard.writeText(value);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1400);
  }

  return (
    <Button aria-label={copied ? "Copied" : ariaLabel} onClick={copyValue} size="sm" type="button" variant="outline">
      {copied ? <Check className="h-4 w-4" /> : <Copy className="h-4 w-4" />}
      {copied ? "Copied" : label}
    </Button>
  );
}
