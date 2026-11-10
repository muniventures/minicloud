import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { DatabaseDetailModule } from "./DatabaseDetailModule";
import * as api from "../../api/databasesApi";
import { downloadDatabaseFile, downloadDatabaseBlob } from "./downloadDatabaseFile";
vi.mock("./downloadDatabaseFile", () => ({ downloadDatabaseFile: vi.fn(), downloadDatabaseBlob: vi.fn() }));
vi.mock("../../core/auth/AuthProvider", () => ({ useAuth: () => ({ selectedOrganization: { id: "org-1", role: "owner" } }) }));
vi.mock("../../api/appsApi", () => ({ listApps: vi.fn().mockResolvedValue([]), listAppServices: vi.fn().mockResolvedValue([]) }));
vi.mock("../../api/databasesApi", () => ({ getDatabase: vi.fn(), getBackupPolicy: vi.fn(), listBackups: vi.fn(), listDatabaseOperations: vi.fn(), listRepositories: vi.fn(), listDatabaseServers: vi.fn(), getDatabaseCapabilities: vi.fn(), requestDatabaseOperation: vi.fn(), setDatabaseAccess: vi.fn(), revealDatabaseConnection: vi.fn(), exportBackupRecoveryMaterial: vi.fn(), cancelDatabaseBackup: vi.fn(), downloadBackupFile: vi.fn(), listDatabaseBindings: vi.fn().mockResolvedValue([]), bindDatabase: vi.fn() }));
beforeEach(() => {
  vi.mocked(api.getDatabase).mockResolvedValue({ id: "db-1", name: "Orders", engine: "postgres", engineVersion: "16", placement: "shared", status: "ready", accessMode: "internal", allowedCidrsJson: "[]" });
  vi.mocked(api.getBackupPolicy).mockResolvedValue({ enabled: true, frequencyHours: 24, retentionDays: 30, minimumCopies: 2, repositoryAppId: "repo-1", status: "pending" });
  vi.mocked(api.listBackups).mockResolvedValue([{ id: "artifact-1", backupId: "snapshot-1", status: "complete", sizeBytes: 100 }]);
  vi.mocked(api.listDatabaseOperations).mockResolvedValue([]);
  vi.mocked(api.listRepositories).mockResolvedValue([]);
  vi.mocked(api.listDatabaseServers).mockResolvedValue([{ id: "server-1", name: "Shared", mode: "shared", status: "ready", engine: "postgres", engineVersion: "16", planId: "plan-1" }]);
  vi.mocked(api.getDatabaseCapabilities).mockResolvedValue({ accessModes: ["internal"] });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });
function mount() { render(<MemoryRouter initialEntries={["/databases/db-1"]}><Routes><Route path="/databases/:databaseId" element={<DatabaseDetailModule />} /></Routes></MemoryRouter>); }
it("does not offer unsupported network modes", async () => { mount(); await screen.findByText("snapshot-1"); expect(screen.getByLabelText("Access mode")).toHaveTextContent("Internal only"); expect(screen.queryByRole("option", { name: "Public (TLS)" })).not.toBeInTheDocument(); });
it("restores a complete backup only into an explicitly named new target", async () => { mount(); await screen.findByText("snapshot-1"); fireEvent.click(screen.getByRole("button", { name: "Restore as new database" })); fireEvent.change(screen.getByLabelText("Target database name"), { target: { value: "orders_recovered" } }); fireEvent.change(screen.getByLabelText("Target server"), { target: { value: "server-1" } }); fireEvent.click(screen.getByRole("button", { name: "Request restore" })); await waitFor(() => expect(api.requestDatabaseOperation).toHaveBeenCalledWith("db-1", { kind: "restore", targetName: "orders_recovered", targetServerId: "server-1", backupArtifactId: "artifact-1" })); });
it("shows failed backup errors without claiming success", async () => { vi.mocked(api.requestDatabaseOperation).mockRejectedValue(new Error("Repository unavailable")); mount(); await screen.findByText("snapshot-1"); fireEvent.click(screen.getByRole("button", { name: "Back up now" })); expect(await screen.findByRole("alert")).toHaveTextContent("Repository unavailable"); });
it("does not offer restore for an incomplete artifact", async () => { vi.mocked(api.listBackups).mockResolvedValue([{ id: "artifact-1", backupId: "partial", status: "pending", sizeBytes: 0 }]); mount(); await screen.findByText("partial"); expect(screen.getByRole("button", { name: "Restore as new database" })).toBeDisabled(); });
it("reveals credentials only on request and clears them when hidden", async () => {
  vi.mocked(api.revealDatabaseConnection).mockResolvedValue({ databaseId: "db-1", engine: "postgres", accessMode: "internal", databaseName: "orders", username: "orders_user", password: "synthetic-password", host: null, port: 5432, sslMode: null, endpointAvailable: false });
  mount(); await screen.findByText("snapshot-1");
  expect(api.revealDatabaseConnection).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Reveal connection credentials" }));
  expect(await screen.findByText("synthetic-password")).toBeInTheDocument();
  expect(screen.getByText(/No verified remote endpoint/)).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Hide credentials" }));
  expect(screen.queryByText("synthetic-password")).not.toBeInTheDocument();
});

it("downloads the public CA only after an explicit credential reveal", async () => {
  vi.mocked(api.revealDatabaseConnection).mockResolvedValue({ databaseId: "db-1", engine: "postgres", accessMode: "public", databaseName: "orders", username: "user", password: "synthetic-password", host: "192.0.2.10", port: 5432, sslMode: "verify-full", endpointAvailable: true, caCertificatePem: "synthetic-ca-pem" });
  mount(); await screen.findByText("snapshot-1");
  expect(screen.queryByRole("button", { name: "Download TLS CA certificate" })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Reveal connection credentials" }));
  fireEvent.click(await screen.findByRole("button", { name: "Download TLS CA certificate" }));
  expect(downloadDatabaseFile).toHaveBeenCalledWith("db-1-ca.pem", "synthetic-ca-pem", "application/x-pem-file");
});
it("keeps deleted-source recovery available while hiding live mutations", async () => {
  vi.mocked(api.getDatabase).mockResolvedValue({ id: "db-1", name: "Orders", engine: "postgres", engineVersion: "16", placement: "shared", status: "deleted", accessMode: "internal", allowedCidrsJson: "[]" });
  mount(); await screen.findByText("snapshot-1");
  expect(screen.getByText(/The source is deleted/)).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Delete database" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Configure backups" })).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Restore as new database" })).toBeEnabled();
  expect(screen.getByRole("button", { name: "Export backup recovery key" })).toBeEnabled();
});
it("exports recovery material only after confirmation without rendering the key", async () => {
  const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
  vi.mocked(api.exportBackupRecoveryMaterial).mockResolvedValue({ databaseId: "db-1", engine: "postgres", keyVersion: "key-1", encryptionKey: "synthetic-recovery-key" });
  mount(); await screen.findByText("snapshot-1");
  expect(api.exportBackupRecoveryMaterial).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Export backup recovery key" }));
  await waitFor(() => expect(downloadDatabaseFile).toHaveBeenCalledWith("db-1-backup-recovery.json", expect.stringContaining("synthetic-recovery-key"), "application/json"));
  expect(screen.queryByText("synthetic-recovery-key")).not.toBeInTheDocument();
  confirm.mockRestore();
});

it("offers cancellation only for queued or running backups and waits for actual worker status", async () => {
  vi.mocked(api.listDatabaseOperations).mockResolvedValue([{ id: "backup-op", kind: "backup", status: "running", createdAt: "2026-11-09T00:00:00Z" }, { id: "restore-op", kind: "restore", status: "running", createdAt: "2026-11-09T00:00:00Z" }, { id: "complete-op", kind: "backup", status: "completed", createdAt: "2026-11-09T00:00:00Z" }]);
  vi.mocked(api.cancelDatabaseBackup).mockResolvedValue(undefined);
  mount(); await screen.findByText("snapshot-1");
  expect(screen.getAllByRole("button", { name: "Cancel backup" })).toHaveLength(1);
  fireEvent.click(screen.getByRole("button", { name: "Cancel backup" }));
  await waitFor(() => expect(api.cancelDatabaseBackup).toHaveBeenCalledWith("db-1", "backup-op"));
  expect(await screen.findByText(/Cancellation requested/)).toHaveTextContent("Completed recovery points are retained");
  expect(screen.getByRole("button", { name: "Cancel backup" })).toBeDisabled();
});

it("downloads encrypted SQLite recovery files after source deletion without revealing keys", async () => {
  vi.mocked(api.getDatabase).mockResolvedValue({ id: "db-1", name: "SQLite source", engine: "sqlite", engineVersion: "3", placement: "application", status: "deleted", accessMode: "internal", allowedCidrsJson: "[]" });
  const blob = new Blob(["synthetic-ciphertext"]);
  vi.mocked(api.downloadBackupFile).mockResolvedValue(blob);
  mount(); await screen.findByText("snapshot-1");
  expect(api.downloadBackupFile).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Download encrypted database" }));
  await waitFor(() => expect(api.downloadBackupFile).toHaveBeenCalledWith("db-1", "artifact-1", "database.dump.enc"));
  await waitFor(() => expect(downloadDatabaseBlob).toHaveBeenCalledWith("snapshot-1-database.dump.enc", blob));
  await waitFor(() => expect(screen.getByRole("button", { name: "Download encrypted manifest" })).toBeEnabled());
  fireEvent.click(screen.getByRole("button", { name: "Download encrypted manifest" }));
  await waitFor(() => expect(api.downloadBackupFile).toHaveBeenCalledWith("db-1", "artifact-1", "manifest.json.enc"));
  expect(screen.queryByText("synthetic-ciphertext")).not.toBeInTheDocument();
});

it("does not offer unsupported application-hosted credential rotation", async () => {
  vi.mocked(api.getDatabase).mockResolvedValue({ id: "db-1", name: "App database", engine: "postgres", engineVersion: "16", placement: "application", status: "ready", accessMode: "internal", allowedCidrsJson: "[]" });
  mount(); await screen.findByText("snapshot-1");
  expect(screen.getByRole("button", { name: "Rotate credentials" })).toBeDisabled();
});
