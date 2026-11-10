# T80 — Final candidate and source-pin handoff

Repository: `muniventures/minicloud`. Owner: one final metadata/integration Copilot task, triggered by the user.
Depends on: all implementation tasks T00–T70 merged, including authorized private integration. Status: not started. No parallel implementation writers remain in shared release files.

## Deliverable

This is the final code-completion handoff before Codex validates, not another feature or an authorization to publish/deploy.

- Check actual public/private merged SHAs and task results. Report a missing implementation rather than incorporating an unreviewed branch or private local commit.
- Update T20's default CLI source pin and examples to an existing merged canonical revision containing the final auth, SSH and numbered-deployment client. Do not leave the initial pre-feature pin in the reusable customer workflow. The pin must identify actual client code, not this metadata commit itself; no self-referential commit hash or nonexistent tag is required.
- Align the existing server/CLI compatibility metadata, release inputs and documentation with that final contract. Use the T40 pipeline's real digest generation; never invent a digest, claim an unpublished asset exists or add another release mechanism.
- Confirm private candidate selection consumes the final public release input instead of a historical baseline, and record both repositories' candidate identifiers. Any private source change needed must use its own private PR; do not put hosted configuration/secrets in the public PR.
- Update the execution tracker with merged PRs and outstanding historical live evidence. Leave T90/T91 uncompleted and hand Codex the exact candidate/commands/limitations.

Own only final source-pin/compatibility metadata and relevant caller/documentation references after earlier owners hand them off. Run focused metadata/workflow contract checks. If new product code is needed, return it to the relevant task; do not hide a feature repair inside this final chore.

Issue prompt: complete only T80 after all implementation PRs merge. Align final source pins and candidate metadata without publishing or deploying, report focused check results, and hand the merged candidate to Codex for T90/T91.
