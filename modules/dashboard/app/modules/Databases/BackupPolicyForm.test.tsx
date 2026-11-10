import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { BackupPolicyForm, defaultPolicy } from "./BackupPolicyForm";
afterEach(cleanup);
it("defaults disabled policies to recorded expiry and submits explicit indefinite retention only on selection", () => {
  const save = vi.fn();
  render(<BackupPolicyForm initial={{ ...defaultPolicy, enabled: false }} repositories={[]} busy={false} onSave={save} />);
  const keep = screen.getByRole("checkbox", { name: /Keep minimum complete copies/ });
  expect(keep).not.toBeChecked();
  expect(screen.getByText(/already-expired copies become eligible/)).toBeInTheDocument();
  fireEvent.click(keep);
  expect(screen.getByText(/kept indefinitely/)).toHaveTextContent("2 complete copies");
  fireEvent.click(screen.getByRole("button", { name: "Save backup policy" }));
  expect(save).toHaveBeenCalledWith(expect.objectContaining({ enabled: false, keepLastCopies: true }));
});

it("retained-source mode forces captures disabled even if the initial policy was enabled", () => {
  const save = vi.fn();
  const initial = { ...defaultPolicy, enabled: true, repositoryAppId: "old-repository", frequencyHours: 6, retentionDays: 90, minimumCopies: 4, keepLastCopies: true };
  render(<BackupPolicyForm initial={initial} repositories={[]} busy={false} retainedSource onSave={save} />);
  expect(screen.getByRole("checkbox", { name: "Enable scheduled backups" })).toBeDisabled();
  fireEvent.click(screen.getByRole("button", { name: "Save backup policy" }));
  expect(save).toHaveBeenCalledWith({ ...initial, enabled: false });
});
