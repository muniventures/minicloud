# Maintenance source implementation — not a qualified server release

No version, tested CLI range, dependency digest or upgrade path is assigned by
this document. The integration owner supplies actual tested release metadata.

The installer now dispatches signed, inventory-checked maintenance code under
its existing persistent installation lock. Independent OpenBao recovery
dependencies can be archived with authenticated `age` encryption, verified
against the original installation/release/CA, rehearsed as protected files
without starting services, and scheduled through authored (not installed)
systemd units. Retention planning and exact-identity archive removal are
available. These archives are explicitly **not full coordinated backups**.

Coordinated backup, upgrade, full restore, production transfer and automated
service stop/removal fail closed. Current production code has no common
management-write admission fence, complete accepted-work drain or durable
old-worker production-transfer fence. Packaging cannot safely replace these
with container termination, a bootstrap readiness result or an operator flag.
The [upgrade prerequisites](upgrade-prerequisites.md) remain release blockers.
