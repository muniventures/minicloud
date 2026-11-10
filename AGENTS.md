# Minicloud repository instructions

This repository contains the Minicloud product source and builds its public CLI releases.
Keep changes focused. Never push code unless explicitly asked.

- Start with the README and the user documentation under `docs/`.
- Keep behavior and tests together. Run the affected .NET or dashboard checks from the README.
- Keep hosted operations and operator credentials out of this repo. Use synthetic fixtures and example configuration.
- Keep the public installer paths, CLI package names, and reusable deployment workflow compatible with existing callers.

## Generated artifacts

- Put all intermediate files and generated execution output under the owning repository's `.local/artifacts/` directory, which must be git-ignored before writing outputs.
- This includes screenshots and browser recordings, benchmark results, SQL dumps and execution plans, logs, traces, coverage/build reports, and agent implementation/validation/handoff reports. Never put these outputs in `docs/`, source folders, or the Git index; never force-add ignored artifacts.
- Keep reusable scripts/tests and concise, maintained documentation in source control. Deliberately authored product assets and explicitly requested deliverables are not intermediate execution output.
- Before finishing, check `git status` and `git check-ignore` to ensure generated outputs cannot enter the change set. Move any misplaced outputs into `.local/artifacts/` and update producers and references.
