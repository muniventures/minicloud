import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { listCustomDomains, removeCustomDomain, registerCustomDomain, verifyCustomDomainOwnership } from "@/api/customDomainsApi";
import { createAppCustomDomain } from "@/api/environmentsApi";
import type { CustomDomainRegistrationResponse } from "@/models/controlPlane";
import { AppDomains } from "./AppDomains";
import { CustomDomainSetupDialog } from "./CustomDomainSetupDialog";

vi.mock("@/api/customDomainsApi", () => ({ listCustomDomains: vi.fn(), removeCustomDomain: vi.fn(), registerCustomDomain: vi.fn(), verifyCustomDomainOwnership: vi.fn() }));
vi.mock("@/api/environmentsApi", () => ({ createAppCustomDomain: vi.fn(), verifyAppDomain: vi.fn() }));
afterEach(() => { cleanup(); vi.resetAllMocks(); });
const registration: CustomDomainRegistrationResponse = { id: "reg", appId: "app", hostname: "app1.example.com", verificationToken: "mc-domain-verification=token", status: "pending", failureMessage: null, lastCheckedAt: null };

it("registers and verifies an app domain without a deployed service or an assignment step", async () => {
  vi.mocked(listCustomDomains).mockResolvedValue([]);
  vi.mocked(registerCustomDomain).mockResolvedValue(registration);
  vi.mocked(verifyCustomDomainOwnership).mockResolvedValue({ ...registration, status: "verified" });
  render(<AppDomains organizationId="org" appId="app" services={[]} onSaved={vi.fn().mockResolvedValue(undefined)} onManageService={vi.fn()} />);
  await screen.findByText("No custom domains added to this app.");
  expect(listCustomDomains).toHaveBeenCalledWith("app", expect.any(AbortSignal));
  fireEvent.click(screen.getByRole("button", { name: "Add domain" }));
  fireEvent.change(screen.getByLabelText("Your domain"), { target: { value: registration.hostname } });
  fireEvent.click(screen.getByText("Continue to DNS verification"));
  await screen.findByText("Verify you own app1.example.com");
  expect(registerCustomDomain).toHaveBeenCalledWith("app", registration.hostname);
  fireEvent.click(screen.getByRole("button", { name: "Verify DNS" }));
  await screen.findByText("Domain verified");
  expect(verifyCustomDomainOwnership).toHaveBeenCalledWith("app", "reg");
  expect(screen.queryByLabelText("Subdomain (optional)")).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Done" })).toBeInTheDocument();
  expect(createAppCustomDomain).not.toHaveBeenCalled();
});

it("assigns a service subdomain under a verified app domain", async () => {
  vi.mocked(verifyCustomDomainOwnership).mockResolvedValue({ ...registration, status: "verified" });
  vi.mocked(createAppCustomDomain).mockResolvedValue({ id: "binding", hostname: "api.app1.example.com", serviceName: "api", dnsStatus: "pending", status: "pending_dns" } as never);
  render(<CustomDomainSetupDialog organizationId="org" appId="app" serviceName="api" registration={{ ...registration, status: "verified" }} liveBindings={[]} onClose={vi.fn()} onSaved={vi.fn().mockResolvedValue(undefined)} />);
  fireEvent.change(screen.getByLabelText("Subdomain (optional)"), { target: { value: "api" } });
  fireEvent.click(screen.getByRole("button", { name: "Connect to api" }));
  await screen.findByText("Point your domain to the service");
  expect(createAppCustomDomain).toHaveBeenCalledWith("app", "api", "api.app1.example.com");
  expect(registerCustomDomain).not.toHaveBeenCalled();
});

it("returns to TXT verification when a previously verified domain loses ownership and retains the subdomain", async () => {
  vi.mocked(verifyCustomDomainOwnership).mockResolvedValue({ ...registration, status: "pending", failureMessage: "The TXT record is not visible yet." });
  render(<CustomDomainSetupDialog organizationId="org" appId="app" serviceName="api" registration={{ ...registration, status: "verified" }} liveBindings={[]} onClose={vi.fn()} onSaved={vi.fn().mockResolvedValue(undefined)} />);
  fireEvent.change(screen.getByLabelText("Subdomain (optional)"), { target: { value: "api" } });
  fireEvent.click(screen.getByRole("button", { name: "Connect to api" }));
  await screen.findByText("Verify you own app1.example.com");
  expect(screen.getByText("_minicloud-verification.app1.example.com")).toBeInTheDocument();
  expect(screen.queryByText("Domain verified")).not.toBeInTheDocument();
  expect(createAppCustomDomain).not.toHaveBeenCalled();
  vi.mocked(verifyCustomDomainOwnership).mockResolvedValue({ ...registration, status: "verified" });
  fireEvent.click(screen.getByRole("button", { name: "Verify DNS" }));
  await screen.findByText("Domain verified");
  expect(screen.getByLabelText("Subdomain (optional)")).toHaveValue("api");
});

it("reopens an existing binding at TXT verification when runtime DNS checks detect lost ownership", async () => {
  vi.mocked(listCustomDomains).mockResolvedValue([{ ...registration, status: "pending" }]);
  const binding = { id: "binding", hostname: "api.app1.example.com", verificationToken: registration.verificationToken, failureCode: "dns_ownership_pending", failureMessage: "Restore the TXT record.", status: "pending_dns" } as import("@/models/controlPlane").DomainBindingResponse;
  render(<CustomDomainSetupDialog organizationId="org" appId="app" serviceName="api" existingBinding={binding} liveBindings={[]} onClose={vi.fn()} onSaved={vi.fn().mockResolvedValue(undefined)} />);
  await screen.findByText("Verify you own app1.example.com");
  expect(screen.getByText("Restore the TXT record.")).toBeInTheDocument();
  vi.mocked(verifyCustomDomainOwnership).mockResolvedValue({ ...registration, status: "verified" });
  fireEvent.click(screen.getByRole("button", { name: "Verify DNS" }));
  await screen.findByText("Point your domain to the service");
  expect(createAppCustomDomain).not.toHaveBeenCalled();
});

it("confirms domain removal and refreshes the app domain list", async () => {
  vi.mocked(listCustomDomains).mockResolvedValueOnce([registration]).mockResolvedValue([]);
  vi.mocked(removeCustomDomain).mockResolvedValue(undefined);
  render(<AppDomains organizationId="org" appId="app" services={[]} onSaved={vi.fn().mockResolvedValue(undefined)} onManageService={vi.fn()} />);
  fireEvent.pointerDown(await screen.findByRole("button", { name: "Actions for app1.example.com" }), { button: 0, ctrlKey: false });
  fireEvent.click(await screen.findByRole("menuitem", { name: "Remove domain" }));
  expect(removeCustomDomain).not.toHaveBeenCalled();
  expect(screen.getByRole("alertdialog")).toHaveTextContent("Your DNS records remain at your provider");
  fireEvent.click(screen.getByRole("button", { name: "Remove domain" }));
  await screen.findByText("No custom domains added to this app.");
  expect(removeCustomDomain).toHaveBeenCalledWith("app", "reg");
});

it("uses compact owned-domain and hostname tables with no orphan Use CTA", async () => {
  const verified: CustomDomainRegistrationResponse = { ...registration, status: "verified" };
  const service = {
    name: "web",
    image: "img",
    port: 80,
    public: true,
    path: "/",
    healthPath: "/health",
    domains: [
      { id: "b1", hostname: "app.example.com", kind: "custom_domain", dnsStatus: "verified", applyStatus: "applied", sslStatus: "active", status: "active" },
      { id: "b2", hostname: "old.example.com", kind: "custom_domain", dnsStatus: "verified", applyStatus: "applied", sslStatus: "active", status: "disabled" },
    ],
  };
  vi.mocked(listCustomDomains).mockResolvedValue([verified]);
  render(<AppDomains organizationId="org" appId="app" services={[service] as never} onSaved={vi.fn().mockResolvedValue(undefined)} onManageService={vi.fn()} />);

  expect(await screen.findByRole("columnheader", { name: "Domain" })).toBeInTheDocument();
  expect(screen.getByRole("columnheader", { name: "Ownership" })).toBeInTheDocument();
  expect(screen.getByRole("columnheader", { name: "Hostname" })).toBeInTheDocument();
  expect(screen.getByRole("columnheader", { name: "Service" })).toBeInTheDocument();
  expect(screen.getByRole("columnheader", { name: "Routing" })).toBeInTheDocument();
  expect(screen.getByRole("columnheader", { name: "HTTPS" })).toBeInTheDocument();
  expect(screen.queryByText(/Use app1\.example\.com/)).not.toBeInTheDocument();
  expect(screen.queryByText("Choose a public service")).not.toBeInTheDocument();
  expect(screen.getByText("Disabled")).toBeInTheDocument();

  fireEvent.click(screen.getByRole("button", { name: "Add hostname" }));
  fireEvent.click(screen.getByRole("combobox", { name: "Service" }));
  fireEvent.click(await screen.findByRole("option", { name: "web" }));
  fireEvent.click(screen.getByRole("combobox", { name: "Domain" }));
  fireEvent.click(await screen.findByRole("option", { name: "app1.example.com" }));
  fireEvent.click(screen.getByRole("button", { name: "Continue" }));

  expect(await screen.findByRole("dialog", { name: "Assign a service hostname" })).toBeInTheDocument();
  expect(registerCustomDomain).not.toHaveBeenCalled();
});

it("keeps the domain visible when the server refuses removal", async () => {
  vi.mocked(listCustomDomains).mockResolvedValue([registration]);
  vi.mocked(removeCustomDomain).mockRejectedValue(new Error("Remove service hostnames first."));
  render(<AppDomains organizationId="org" appId="app" services={[]} onSaved={vi.fn().mockResolvedValue(undefined)} onManageService={vi.fn()} />);
  fireEvent.pointerDown(await screen.findByRole("button", { name: "Actions for app1.example.com" }), { button: 0, ctrlKey: false });
  fireEvent.click(await screen.findByRole("menuitem", { name: "Remove domain" }));
  fireEvent.click(screen.getByRole("button", { name: "Remove domain" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Remove service hostnames first.");
  expect(screen.getByRole("button", { name: "Remove domain" })).toBeEnabled();
});
