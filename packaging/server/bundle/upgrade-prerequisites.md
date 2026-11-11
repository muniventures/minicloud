# Upgrade and full-recovery prerequisites

Coordinated maintenance uses the durable maintenance authority
(`dotnet Minicloud.Api.dll maintenance ...`, migration
`20261111134441_MaintenanceAuthority`). Every API write and scheduler pass is
admitted through its fence. It reports drain status for queued, running,
retrying and uncertain work, and it revokes processes registered at a
superseded epoch. The tooling waits for accepted work to finish. It never kills
work.

Before upgrading, the operator must have:

- The **installed** release's verified assets directory, passed as
  `--previous-assets-dir`. Its bundle digest must equal the installed
  `bundleSha256`.
- The independently verified **target** release assets. The target must be
  newer, with application, identity and configuration schema versions no lower
  than the installed release.
- A protected age recipients file and identity file. The upgrade takes a full
  pre-upgrade backup and authenticates it before stopping any service.
- At least 2 GiB free at the backup destination, plus space for the new images.
- A maintenance window. Writes stay fenced from drain until the new release
  reports healthy.

The upgrade never downgrades images. If it fails after migrations start,
writes stay fenced. Either re-run the same upgrade to resume, or restore the
pre-upgrade backup with the previous release's installer.

Restore needs the exact release that created the backup, the original root
path and the original configuration file. After a restore or transfer, an old
host is revoked only when it next reaches this database. Shut down any old
host that runs its own database copy or holds live provider, DNS or SSH
credentials, or rotate those credentials.

Independent recovery-dependency archives (`*-recovery`) are not full backups.
Never use them as a pre-upgrade backup.
