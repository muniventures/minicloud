import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { IssueTelemetryCredentialDialog } from "./IssueTelemetryCredentialDialog";

vi.mock("../../../api/telemetryApi", () => ({
  issueTelemetryCredential: vi.fn()
}));

describe("IssueTelemetryCredentialDialog", () => {
  afterEach(cleanup);

  it("preserves the selected service when an equivalent services array is provided", async () => {
    const props = {
      appId: "app_123",
      open: true,
      onOpenChange: vi.fn(),
      onIssued: vi.fn()
    };
    const view = render(
      <IssueTelemetryCredentialDialog
        {...props}
        services={["admin", "example-suite-api"]}
      />
    );

    expect(screen.getByRole("textbox", { name: "Name" })).toHaveAttribute("placeholder", "production");
    expect(screen.getByRole("combobox", { name: "Expires" })).toHaveTextContent("3 months");
    expect(screen.queryByLabelText("Expiration date")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Cancel" }).parentElement).toBe(
      screen.getByRole("button", { name: "Issue credential" }).parentElement
    );
    fireEvent.click(screen.getByRole("combobox", { name: "Service" }));
    fireEvent.click(await screen.findByRole("option", { name: "example-suite-api" }));
    expect(screen.getByRole("combobox", { name: "Service" })).toHaveTextContent("example-suite-api");

    view.rerender(
      <IssueTelemetryCredentialDialog
        {...props}
        services={["admin", "example-suite-api"]}
      />
    );

    expect(screen.getByRole("combobox", { name: "Service" })).toHaveTextContent("example-suite-api");

    fireEvent.click(screen.getByRole("combobox", { name: "Expires" }));
    fireEvent.click(await screen.findByRole("option", { name: "Custom" }));
    expect(screen.getByLabelText("Expiration date")).toHaveAttribute("type", "date");
  });
});
