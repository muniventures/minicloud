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
