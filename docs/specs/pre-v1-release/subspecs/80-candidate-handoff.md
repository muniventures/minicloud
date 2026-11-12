# T80 — Final candidate and source-pin handoff

Repository: `muniventures/minicloud`. Owner: one final metadata/integration Copilot task, triggered by the user.
Depends on: required public T00–T62 implementation and source repairs merged. Private T70 #36 is backlog, not a prerequisite, by explicit user direction on 2026-11-12; #44 is later validation, not a code-completion blocker. Status: assigned public-only candidate metadata for `0.0.1-rc.1`. No parallel implementation writers remain in shared release files.

## Deliverable

This is the final code-completion handoff before Codex validates, not another feature or an authorization to publish/deploy.

- Check actual public merged SHAs and task results. Report a missing required public implementation rather than incorporating an unreviewed branch. Do not inspect or depend on a private local commit.
- Update T20's default CLI source pin and examples to an existing merged canonical revision containing the final auth, SSH and numbered-deployment client. Do not leave the initial pre-feature pin in the reusable customer workflow. The pin must identify actual client code, not this metadata commit itself; no self-referential commit hash or nonexistent tag is required.
- Align the existing server/CLI compatibility metadata, release inputs and documentation with that final contract. Use the T40 pipeline's real digest generation; never invent a digest, claim an unpublished asset exists or add another release mechanism.
- Record the public candidate/source identifiers and evidence limits. Future private candidate wiring belongs to backlog T70, not this public deliverable; do not put hosted configuration/secrets in the public PR.
- Update the execution tracker with merged PRs and outstanding historical live evidence. Leave T90/T91 uncompleted and hand Codex the exact candidate/commands/limitations.

Own only final source-pin/compatibility metadata and relevant caller/documentation references after earlier owners hand them off. The current user-authorized run excludes manual tests/builds and CI reruns; record source/registry evidence separately from deferred runtime compatibility and qualification. If new product code is needed, return it to the relevant task; do not hide a feature repair inside this final chore.

Issue prompt: complete only T80 after all implementation PRs merge. Align final source pins and candidate metadata without publishing or deploying, report focused check results, and hand the merged candidate to Codex for T90/T91.
