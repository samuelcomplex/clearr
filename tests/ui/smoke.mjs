// UI smoke test: mounts the shared Clearr UI (as the app and as the plugin) in a simulated browser,
// opens every page against canned API answers, and fails on any script error.
import { readFileSync } from "node:fs";
import { JSDOM } from "jsdom";

const ui = readFileSync(new URL("../../src/Clearr.Core/ui/clearr-ui.js", import.meta.url), "utf8");
let failures = 0;
const check = (ok, what) => { console.log((ok ? "PASS " : "FAIL ") + "[ui] " + what); if (!ok) failures++; };

const user = (status, extra = {}) => ({ userId: "u1", userName: "Anna <b>", status, watched: 2, total: 4, partial: 0, flags: [], ...extra });
const answers = {
  status: { version: "0.2.0", configured: true, mode: "DryRun", passwordRequired: false, authenticated: true, nextRun: "2026-11-27T04:00:00", notice: "Settings upgraded from format 0 to 1.", https: { active: false } },
  report: {
    generatedAt: "2026-11-26T10:00:00Z", trackedUsers: ["Anna"], sonarrConnected: true, radarrConnected: true, warnings: [], directDelete: false,
    shows: [{ showId: "s1", name: "<img src=x onerror=alert(1)>", status: "Ended", episodeCount: 4, kept: false, managed: true, via: "Sonarr", eligible: true, reason: "Finished by Anna",
      users: [user("Finished")], seasons: [{ seasonId: "se1", number: 1, name: "Season 1", episodeCount: 4, kept: false, eligible: true, reason: "Finished by Anna", users: [user("Finished")] }] }],
    movies: [{ movieId: "m1", name: "Movie", year: 2020, kept: false, managed: true, via: "Radarr", eligible: false, reason: "Waiting on Anna (0/1)", users: [user("InProgress", { flags: ["Sampled"] })] }],
  },
  history: [{ date: "2026-11-25T04:00:00Z", action: "WouldDelete", kind: "Season", title: "Show", seasonName: "Season 1", bytes: 3221225472, detail: "Finished by Anna" }],
  settings: { settings: { mode: "DryRun", sonarr: { url: "", apiKey: "" }, radarr: { url: "", apiKey: "" }, jellyfin: { url: "", apiKey: "" }, https: { enabled: false, port: 8586 }, excludedUserIds: [], debugLogging: true }, hasPassword: false, apiKey: "k" },
  users: [{ id: "u1", name: "Anna" }],
  "logs?lines=1000": { files: [{ name: "clearr.txt", size: 2048 }], lines: ["2026-11-26 10:00:00.000|Warn|Engine|Sonarr <script>bad()</script> slow", "   at Something.Trace()"] },
};

for (const host of ["app", "plugin"]) {
  const dom = new JSDOM("<!DOCTYPE html><div id=root></div>", { runScripts: "outside-only", url: "http://clearr.test/" });
  const w = dom.window;
  const errors = [];
  w.addEventListener("error", (e) => errors.push(e.message));
  w.eval(ui);
  const calls = [];
  const api = (path) => { calls.push(path); return path in answers ? Promise.resolve(JSON.parse(JSON.stringify(answers[path]))) : Promise.reject(new Error("unexpected " + path)); };
  const root = w.document.getElementById("root");
  const mounted = w.ClearrUI.mount(root, { host, api });
  const pages = host === "app" ? ["shows", "movies", "history", "settings", "logs"] : ["shows", "movies", "history", "settings"];
  const seen = {};
  for (const p of pages) {
    mounted.show(p);
    await new Promise((r) => setTimeout(r, 20));
    seen[p] = { text: root.textContent, imgs: root.querySelectorAll("[data-el=list] img").length };
  }

  const text = root.textContent;
  check(errors.length === 0 && !calls.some((c) => !(c in answers)), `${host}: every page opens without errors (${calls.length} API calls)`);
  check(seen.shows.imgs === 0 && seen.shows.text.includes("<img src=x onerror=alert(1)>") && seen.shows.text.includes("Anna <b>"), `${host}: names containing HTML are shown as text, not run`);
  check(text.includes("Settings upgraded from format 0 to 1."), `${host}: settings notice shown in the banner`);
  check(text.includes("Would remove (dry run)") && text.includes("3.0 GB"), `${host}: history lists the dry-run entry with its size`);
  if (host === "app") {
    check(root.querySelector('[data-page="logs"]') && text.includes("Sonarr <script>bad()</script> slow") && root.querySelector("script") === null, "app: Logs page shows lines as text");
    check(root.querySelector('a[href="api/logs/clearr.txt"]') !== null && root.querySelector('input[name="debugLogging"]').checked, "app: log download link and Debug logging setting");
  } else {
    check(root.querySelector('[data-page="logs"]') === null && root.querySelector('input[name="debugLogging"]') === null && !calls.includes("logs?lines=1000"), "plugin: no Logs page or Debug option (Jellyfin has its own logs)");
    check(root.querySelector('input[name="allowDirectDelete"]') !== null && root.querySelector('input[name="jellyfin.url"]') === null, "plugin: Jellyfin fallback shown, Jellyfin connection hidden");
  }
}

process.exit(failures ? 1 : 0);
