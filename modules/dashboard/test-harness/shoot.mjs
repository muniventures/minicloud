// Puppeteer capture for the dashboard harness. REVIEW TOOLING ONLY — run from
// a /tmp dir with puppeteer installed; never a product dependency.
// Usage: cp this + wwwless via `node shoot.mjs` with vite dev on 127.0.0.1:3199.
import puppeteer from "puppeteer";
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";

const OUT = process.argv[2] ?? path.join(
  execFileSync("git", ["rev-parse", "--show-toplevel"], { encoding: "utf8" }).trim(),
  ".local/artifacts/apps-ux-evidence",
);
fs.mkdirSync(OUT, { recursive: true });
const EXE =
  process.env.CHROME_EXE ??
  "/home/agent/.cache/puppeteer/chrome-headless-shell/linux_arm-156.0.8078.47/chrome-headless-shell-linux-arm64/chrome-headless-shell";
const browser = await puppeteer.launch({ executablePath: EXE, headless: true, args: ["--no-sandbox"] });

const VIEWS = [
  ["apps", "/apps"],
  ["overview", "/apps/app_munipages/environments/runtime_munipages_prod/overview"],
  ["services", "/apps/app_munipages/environments/runtime_munipages_prod/services"],
  ["domains", "/apps/app_munipages/environments/runtime_munipages_prod/domains"],
  ["console", "/apps/app_munipages/environments/runtime_munipages_prod/console"],
  ["logs", "/apps/app_munipages/environments/runtime_munipages_prod/logs"],
  ["settings", "/apps/app_munipages/settings"],
];

for (const [w, h, theme] of [[1440, 900, "light"], [1440, 900, "dark"], [390, 844, "light"]]) {
  for (const [name, route] of VIEWS) {
    const page = await browser.newPage();
    await page.setViewport({ width: w, height: h });
    const url = `http://127.0.0.1:3199/test-harness/shell.html?theme=${theme}&route=${encodeURIComponent(route)}`;
    await page.goto(url, { waitUntil: "networkidle0", timeout: 30000 });
    await new Promise((r) => setTimeout(r, 1200));
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    await page.screenshot({ path: `${OUT}/${name}-${w}x${h}-${theme}.png` });
    console.log(name, w, h, theme, "h-overflow(px):", overflow);
    await page.close();
  }
}

// Keyboard sweep: tab through the scoped overview header and tabs.
{
  const page = await browser.newPage();
  await page.setViewport({ width: 1440, height: 900 });
  const url = `http://127.0.0.1:3199/test-harness/shell.html?theme=light&route=${encodeURIComponent("/apps/app_munipages/environments/runtime_munipages_prod/console")}`;
  await page.goto(url, { waitUntil: "networkidle0", timeout: 30000 });
  await new Promise((r) => setTimeout(r, 1200));
  const focused = [];
  for (let i = 0; i < 30; i++) {
    await page.keyboard.press("Tab");
    focused.push(await page.evaluate(() => {
      const el = document.activeElement;
      return el ? (el.getAttribute("aria-label") || el.textContent || el.tagName).trim().slice(0, 60) : "none";
    }));
  }
  console.log("keyboard focus labels:");
  focused.forEach((f, i) => console.log(i + 1, f));
  await page.close();
}
await browser.close();
console.log("done");
