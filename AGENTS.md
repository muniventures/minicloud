# Minicloud repository instructions

This repository contains the Minicloud product source and builds its public CLI releases.
Keep changes focused. Never push code unless explicitly asked.

- Start with the README and the user documentation under `docs/`.
- Keep behavior and tests together. Run the affected .NET or dashboard checks from the README.
- Keep hosted operations and operator credentials out of this repo. Use synthetic fixtures and example configuration.
- Keep the public installer paths, CLI package names, and reusable deployment workflow compatible with existing callers.
