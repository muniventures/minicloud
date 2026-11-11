# Maintenance source implementation — not a qualified server release

No version, tested CLI range, dependency digest or upgrade path is assigned by
this document. The integration owner supplies actual tested release metadata.

The installer dispatches signed, inventory-checked maintenance code under its
persistent installation lock. `upgrade` and `restore` run the selected
release's signed helper. Coordinated operations call the durable maintenance
authority inside the API image, passing input on stdin. Maintenance never kills
accepted work.

- `backup`: fence, wait for drain within a bounded time, capture, verify,
  release. The capture covers both PostgreSQL databases, the OpenBao, artifact
  (registry) and Caddy volumes, installation metadata and credentials, and the
  OpenBao recovery material. Each component is age-encrypted separately.
- `verify-backup`, `remove-backup` and `schedule-backup` (authored units only,
  never installed).
- `upgrade`: pre-upgrade backup, then stop, switch, migrate, start and verify.
  The phases are persisted, so an interrupted upgrade can resume. A failure
  before migration is rolled back automatically. A failure after migration
  stays fenced, with rollback guidance.
- `restore`: verified before it changes anything. Replacing a live installation
  requires explicit confirmation. The restored host takes over the authority by
  transfer and stays fenced until `restore-confirm`.
- `transfer`: increments the epoch and binds the authority to this host's
  `host-id`. It does **not** stop an old host running on its own database copy,
  or an unreachable host that holds live provider credentials.
- `release`: an escape hatch for a recorded fence.
- Independent recovery-dependency archives (`*-recovery`) remain available and
  are not full backups.

Automated service stop and removal remain unavailable. None of these flows has
been qualified on a real host. See [upgrade prerequisites](upgrade-prerequisites.md).
