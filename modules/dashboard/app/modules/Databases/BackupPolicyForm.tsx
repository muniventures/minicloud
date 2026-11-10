import { useState, type FormEvent } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import type { BackupPolicy, Repository } from "../../api/databasesApi";
export const selectClass = "h-10 w-full rounded-md border bg-background px-3 text-sm";
export const defaultPolicy: BackupPolicy = { enabled: true, frequencyHours: 24, retentionDays: 30, minimumCopies: 2, keepLastCopies: false };
export function BackupPolicyFields({ value, onChange, repositories }: { value: BackupPolicy; onChange: (value: BackupPolicy) => void; repositories: Repository[] }) {
  return <div className="space-y-3"><label className="flex items-center gap-2"><input type="checkbox" checked={value.enabled} onChange={e => onChange({ ...value, enabled: e.target.checked })} />Enable scheduled backups</label>
    {value.enabled && <><Label htmlFor="repository">Off-server repository</Label><select id="repository" className={selectClass} required value={value.repositoryAppId ?? ""} onChange={e => onChange({ ...value, repositoryAppId: e.target.value })}><option value="">Select repository</option>{repositories.map(r => <option key={r.id} value={r.id}>{r.name}</option>)}</select>{!repositories.length && <p className="text-sm text-muted-foreground">An independent repository must be configured before enabling backups.</p>}
    <Label htmlFor="frequency">Frequency (hours)</Label><Input id="frequency" type="number" required min={1} value={value.frequencyHours} onChange={e => onChange({ ...value, frequencyHours: Number(e.target.value) })} />
    <Label htmlFor="retention">Retention (days)</Label><Input id="retention" type="number" required min={1} value={value.retentionDays} onChange={e => onChange({ ...value, retentionDays: Number(e.target.value) })} />
    <Label htmlFor="copies">Minimum complete copies</Label><Input id="copies" type="number" required min={1} value={value.minimumCopies} onChange={e => onChange({ ...value, minimumCopies: Number(e.target.value) })} /></>}
    <label className="flex items-start gap-2"><input type="checkbox" checked={value.keepLastCopies ?? false} onChange={e => onChange({ ...value, keepLastCopies: e.target.checked })} />Keep minimum complete copies until manually deleted after disabling backups or deleting the source</label>
    <p className="text-sm text-muted-foreground">{value.keepLastCopies ? `At least ${value.minimumCopies} complete copies will be kept indefinitely after disablement or source deletion, and storage charges continue.` : "After disablement or source deletion, copies follow their recorded expiry; already-expired copies become eligible for cleanup."}</p>
    <p className="text-sm text-muted-foreground">Backups are encrypted before upload. Repository storage incurs separate charges. Changes apply to future captures.</p></div>;
}
export function BackupPolicyForm({ initial, repositories, busy, onSave }: { initial: BackupPolicy; repositories: Repository[]; busy: boolean; onSave: (value: BackupPolicy) => void }) {
  const [value, setValue] = useState(initial);
  function submit(e: FormEvent) { e.preventDefault(); onSave(value); }
  return <form onSubmit={submit} className="space-y-4"><BackupPolicyFields value={value} onChange={setValue} repositories={repositories} />{!value.enabled && <p className="text-sm">Disabling stops scheduled captures. The selected retention choice controls existing backups.</p>}<Button disabled={busy || (value.enabled && !value.repositoryAppId)} type="submit">Save backup policy</Button></form>;
}
