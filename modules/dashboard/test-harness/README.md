# Dashboard browser harness (REVIEW ONLY — not shipped)

Renders the **real** dashboard route table (`app/routes.tsx`, including
`AppShell` with sidebar + authenticated area) in a real browser with a
local mock transport, so reviewers can reproduce the current components
and layout without a migrated live backend.

## What it proves / does not prove

- Proves: component behavior, responsive layout, shell fit, keyboard
  traversal, light/dark themes, at 320/390/1024/1440 widths.
- Does NOT prove: live backend behavior, migration/backfill, permissions
  (server-side), or hosted rollout. All API responses are synthetic
  fixtures served by an in-page `fetch` mock. Auth is a harness-only fake
  user (`getIdToken: "fake-token"`); no auth bypass exists in shipped
  product code. This harness is not part of the build and has no effect on
  production behavior.

## Reproduce

Prereqs: system Chromium libraries for the headless shell, then
`npm install puppeteer` **outside** this repo (e.g. in `/tmp`) — it is
verification tooling, never a product dependency here.

```sh
cd modules/dashboard   # repo root is this file's ../../
# 1. start the vite dev server with the harness entry as the root page
VITE_APPS_FIXTURES=1 \
VITE_FIREBASE_API_KEY=dummy-api-key \
VITE_FIREBASE_AUTH_DOMAIN=dummy.firebaseapp.com \
VITE_FIREBASE_PROJECT_ID=dummy-project \
VITE_FIREBASE_STORAGE_BUCKET=dummy.appspot.com \
VITE_FIREBASE_MESSAGING_SENDER_ID=123 \
VITE_FIREBASE_APP_ID=1:123:web:dummy \
npx vite --host 127.0.0.1 --port 3199

# 2. screenshot (puppeteer script — copy it next to the installed puppeteer,
#    e.g. /tmp/shoot.mjs, since Node resolves `puppeteer` relative to the
#    script location; the retained copy is test-harness/shoot.mjs)
cp test-harness/shoot.mjs /tmp/shoot.mjs
node /tmp/shoot.mjs ../../.local/artifacts/apps-ux-evidence   # captures apps/overview/services/domains/
                             # console/logs/settings, light+dark,
                             # desktop+mobile, plus a keyboard sweep
```

Then open `http://127.0.0.1:3199/test-harness/shell.html?route=/apps` (or
`?route=/apps/app_munipages/environments/runtime_munipages_prod/console`)
in a browser to navigate the real shell by hand. The harness url query
`?theme=dark` forces dark mode.

## Files

- `shell.html` / `shell.tsx` — mounts the real `routes` behind
  `ThemeProvider` with a mocked `AuthContext` and `fetch`.
- `mockTransport.ts` — synthetic JSON for the `/api` endpoints the
  demo paths exercise. Unmocked paths return a labeled 501 so gaps are
  visible instead of silent.
- `shoot.mjs` — puppeteer capture script (viewports/themes/views +
  keyboard sweep + overflow measurement). Copy it to where `puppeteer` is
  installed (e.g. `/tmp/shoot.mjs`) before running; the retained source of
  truth stays here.
