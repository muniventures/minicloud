# UX drawing source

Static review drawings, not the dashboard implementation. Uses the product's
existing React Button, Card and Badge plus Lucide icons. CSS intentionally isolates
the composition from product layout changes. Synthetic data; no API access.

From the public product root, after installing dashboard dependencies:

```sh
node modules/dashboard/node_modules/vite/bin/vite.js --config docs/specs/assets/apps-ux/source/vite.config.mjs
```

Open `http://127.0.0.1:3217/?screen=apps`. Supported drawing query values:
`apps`, `overview`, `services`, `domains`, `console`, `environments`.
Other tabs are labels for context, not implemented destinations.

Export desktop drawings at 1440×1000 (full page for Services and Console), and
Overview at 390×844 for the mobile drawing. Drawings are regenerated through a
browser screenshot. Source edits do not change production UI.
