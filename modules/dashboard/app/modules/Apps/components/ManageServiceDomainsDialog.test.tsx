import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createAppCustomDomain, verifyAppDomain } from "@/api/environmentsApi";
import { listCustomDomains, registerCustomDomain, verifyCustomDomainOwnership } from "@/api/customDomainsApi";
import type { CustomDomainRegistrationResponse, DomainBindingResponse } from "@/models/controlPlane";
import { CustomDomainSetupDialog } from "./CustomDomainSetupDialog";
import { ManageServiceDomainsDialog } from "./ManageServiceDomainsDialog";

vi.mock("@/api/environmentsApi", () => ({ createAppCustomDomain: vi.fn(), verifyAppDomain: vi.fn(), checkAppDomainAvailability: vi.fn(), createAppDomain: vi.fn(), deleteAppDomain: vi.fn(), setAppDomainPrimary: vi.fn() }));
vi.mock("@/api/customDomainsApi", () => ({ listCustomDomains: vi.fn(), registerCustomDomain: vi.fn(), verifyCustomDomainOwnership: vi.fn() }));
afterEach(() => { cleanup(); vi.resetAllMocks(); });
beforeEach(() => { vi.mocked(listCustomDomains).mockResolvedValue([]); });
const registration: CustomDomainRegistrationResponse = { id: "reg", appId: "app", hostname: "example.com", verificationToken: "mc-domain-verification=token", status: "pending", failureMessage: null, lastCheckedAt: null };
const binding = { id: "dom", appId: "app", serviceName: "web", hostname: "example.com", kind: "custom_domain", pathPrefix: "/", dnsStatus: "pending", status: "pending_dns", targetIp: "192.0.2.10", verificationToken: registration.verificationToken } as DomainBindingResponse;
const onClose = vi.fn();
const onSaved = vi.fn().mockResolvedValue(undefined);
function show(domains: DomainBindingResponse[] = []) {
  render(<ManageServiceDomainsDialog organizationId="org" appId="app" serviceName="web" domains={domains} open onOpenChange={vi.fn()} onSaved={onSaved} />);
}
async function register() {
  vi.mocked(registerCustomDomain).mockResolvedValue(registration);
  render(<CustomDomainSetupDialog organizationId="org" appId="app" serviceName="web" liveBindings={[]} onClose={onClose} onSaved={onSaved} />);
  fireEvent.change(screen.getByLabelText("Your domain"), { target: { value: "example.com" } });
  fireEvent.click(screen.getByText("Continue to DNS verification"));
  await screen.findByText("Verify you own example.com");
}
it("verifies ownership before allowing service assignment, then guides address setup", async () => {
  await register();
  expect(registerCustomDomain).toHaveBeenCalledWith("app", "example.com");
  expect(createAppCustomDomain).not.toHaveBeenCalled();
  expect(screen.queryByText("Connect to web")).not.toBeInTheDocument();
  expect(screen.getByText("_minicloud-verification.example.com")).toBeInTheDocument();
  expect(screen.queryByText("192.0.2.10")).not.toBeInTheDocument();
  vi.mocked(verifyCustomDomainOwnership).mockResolvedValue({ ...registration, status: "verified" });
  fireEvent.click(screen.getByRole("button", { name: "Verify DNS" }));
  await screen.findByText("Domain verified");
  expect(createAppCustomDomain).not.toHaveBeenCalled();
  vi.mocked(createAppCustomDomain).mockResolvedValue(binding);
  fireEvent.click(screen.getByRole("button", { name: "Connect to web" }));
  await screen.findByText("Point your domain to the service");
  expect(createAppCustomDomain).toHaveBeenCalledWith("app", "web", "example.com");
  expect(screen.getByText("192.0.2.10")).toBeInTheDocument();
  expect(screen.queryByText(registration.verificationToken)).not.toBeInTheDocument();
  vi.mocked(verifyAppDomain).mockResolvedValue({ ...binding, dnsStatus: "verified", status: "ssl_pending" });
  fireEvent.click(screen.getByText("Check connection"));
  await screen.findByText("DNS verified · setting up HTTPS");
  expect(screen.queryByText("Your domain is connected")).not.toBeInTheDocument();
});
it("retains the DNS step when ownership has not propagated", async () => {
  await register();
  vi.mocked(verifyCustomDomainOwnership).mockResolvedValue({ ...registration, failureMessage: "TXT record not found." });
  fireEvent.click(screen.getByRole("button", { name: "Verify DNS" }));
  expect(await screen.findByText("TXT record not found.")).toBeInTheDocument();
  expect(createAppCustomDomain).not.toHaveBeenCalled();
  expect(screen.queryByRole("button", { name: "Connect to web" })).not.toBeInTheDocument();
});
it("allows saving a verified domain for later without assigning a service", async () => {
  await register();
  vi.mocked(verifyCustomDomainOwnership).mockResolvedValue({ ...registration, status: "verified" });
  fireEvent.click(screen.getByRole("button", { name: "Verify DNS" }));
  await screen.findByText("Domain verified");
  fireEvent.click(screen.getByText("Save for later"));
  expect(onClose).toHaveBeenCalled();
  expect(createAppCustomDomain).not.toHaveBeenCalled();
});
it("reopens an assigned domain with connection steps instead of inline DNS text", async () => {
  show([binding]);
  expect(screen.queryByText("192.0.2.10")).not.toBeInTheDocument();
  fireEvent.click(screen.getByText("Continue setup"));
  expect(await screen.findByText("192.0.2.10")).toBeInTheDocument();
  fireEvent.click(screen.getByText("Does your provider add the domain name automatically?"));
  fireEvent.change(screen.getByLabelText("DNS zone"), { target: { value: "example.com" } });
  expect(screen.getByText("@")).toBeInTheDocument();
});
it("shows create errors inside the wizard and allows retry", async () => {
  vi.mocked(registerCustomDomain).mockRejectedValue(new Error("Domain is already registered."));
  render(<CustomDomainSetupDialog organizationId="org" appId="app" serviceName="web" liveBindings={[]} onClose={onClose} onSaved={onSaved} />);
  fireEvent.change(screen.getByLabelText("Your domain"), { target: { value: "example.com" } });
  fireEvent.click(screen.getByText("Continue to DNS verification"));
  expect(await screen.findByRole("alert")).toHaveTextContent("Domain is already registered.");
  expect(screen.getByText("Continue to DNS verification")).toBeEnabled();
});

it("shows routing failures without claiming the connection is ready", async () => {
  show([{ ...binding, dnsStatus: "verified", status: "failed", failureMessage: "The public service was not found." }]);
  fireEvent.click(screen.getByText("Connection settings"));
  expect(await screen.findByText("Connection needs attention")).toBeInTheDocument();
  expect(screen.getByText("The public service was not found.")).toBeInTheDocument();
  expect(screen.queryByText("Your domain is connected")).not.toBeInTheDocument();
});

it("assigns a subdomain directly from the service dialog with a full hostname preview", async () => {
  vi.mocked(listCustomDomains).mockResolvedValue([{ ...registration, status: "verified" }]);
  vi.mocked(verifyCustomDomainOwnership).mockResolvedValue({ ...registration, status: "verified" });
  vi.mocked(createAppCustomDomain).mockResolvedValue({ ...binding, hostname: "api.example.com" });
  show();
  await waitFor(() => expect(screen.getByLabelText("Service subdomain (optional)")).toBeEnabled());
  fireEvent.change(screen.getByLabelText("Service subdomain (optional)"), { target: { value: "api" } });
  expect(screen.getByText("api.example.com → web")).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Assign to web" }));
  await screen.findByText("Point your domain to the service");
  expect(createAppCustomDomain).toHaveBeenCalledWith("app", "web", "api.example.com");
});

it("returns inline assignment to ownership verification without losing the subdomain", async () => {
  vi.mocked(listCustomDomains).mockResolvedValue([{ ...registration, status: "verified" }]);
  vi.mocked(verifyCustomDomainOwnership).mockResolvedValue({ ...registration, status: "pending" });
  show();
  await waitFor(() => expect(screen.getByLabelText("Service subdomain (optional)")).toBeEnabled());
  fireEvent.change(screen.getByLabelText("Service subdomain (optional)"), { target: { value: "api" } });
  fireEvent.click(screen.getByRole("button", { name: "Assign to web" }));
  await screen.findByText("Verify you own example.com");
  expect(createAppCustomDomain).not.toHaveBeenCalled();
  vi.mocked(verifyCustomDomainOwnership).mockResolvedValue({ ...registration, status: "verified" });
  fireEvent.click(screen.getByRole("button", { name: "Verify DNS" }));
  await screen.findByText("Domain verified");
  expect(screen.getByLabelText("Subdomain (optional)")).toHaveValue("api");
});
