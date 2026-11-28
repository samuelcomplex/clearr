// UI smoke test: mounts the shared Clearr UI (as the app and as the plugin) in a simulated browser,
// opens every page against canned API answers, and fails on any script error.
import { readFileSync, existsSync } from "node:fs";
import { JSDOM } from "jsdom";

const uiDir = new URL("../../src/Clearr.Core/ui/", import.meta.url);
const ui = readFileSync(new URL("clearr-ui.js", uiDir), "utf8");
const en = JSON.parse(readFileSync(new URL("lang-en.json", uiDir), "utf8"));
const sv = JSON.parse(readFileSync(new URL("lang-sv.json", uiDir), "utf8"));
let failures = 0;
const check = (ok, what) => { console.log((ok ? "PASS " : "FAIL ") + "[ui] " + what); if (!ok) failures++; };

const user = (status, extra = {}) => ({ userId: "u1", userName: "Anna <b>", status, watched: 2, total: 4, partial: 0, flags: [], ...extra });
const answers = {
  status: { version: "0.2.0", configured: true, showsMode: "DryRun", moviesMode: "Delete", passwordRequired: false, authenticated: true, nextShowsRun: "2026-11-27T04:00:00", nextMoviesRun: null, language: "", notice: { code: "notice.upgraded", args: { from: 1, to: 2, file: "settings.v1.bak.json" } }, https: { active: false } },
  report: {
    generatedAt: "2026-11-26T10:00:00Z", trackedUsers: ["Anna"], sonarrConnected: true, radarrConnected: true, warnings: [], directDelete: false,
    shows: [{ showId: "s1", name: "<img src=x onerror=alert(1)>", status: "Ended", episodeCount: 4, kept: false, managed: true, via: "Sonarr", eligible: true, reason: "Finished by Anna", reasonMsg: { code: "reason.ready", args: { names: "Anna" } },
      users: [user("Finished")], seasons: [{ seasonId: "se1", number: 1, name: "Season 1", episodeCount: 4, kept: false, eligible: true, reason: "Finished by Anna", reasonMsg: { code: "reason.ready", args: { names: "Anna" } }, users: [user("Finished")] }] }],
    movies: [{ movieId: "m1", name: "Movie", year: 2020, kept: false, managed: true, via: "Radarr", eligible: false, reason: "Waiting on Anna (0/1, sampled)", reasonMsg: { code: "reason.waiting", args: { people: [{ name: "Anna", watched: 0, total: 1, flags: ["Sampled"] }] } }, users: [user("InProgress", { flags: ["Sampled"] })] }],
  },
  history: [
    { date: "2026-11-25T04:00:00Z", action: "WouldDelete", kind: "Season", title: "Show", seasonName: "Season 1", bytes: 3221225472, detail: "Finished by Anna", parts: [{ code: "reason.ready", args: { names: "Anna" } }] },
    { date: "2026-11-20T04:00:00Z", action: "Deleted", kind: "Movie", title: "Old entry", bytes: 1073741824, detail: "From before translations" },
  ],
  settings: { settings: { showsMode: "DryRun", moviesMode: "Delete", language: "", sonarr: { url: "", apiKey: "" }, radarr: { url: "", apiKey: "" }, jellyfin: { url: "", apiKey: "" }, https: { enabled: false, port: 8586 }, excludedUserIds: [], excludedRootFolders: ["/KIDS"], notifications: { ntfyUrl: "" }, debugLogging: true }, hasPassword: false, apiKey: "k" },
  users: [{ id: "u1", name: "Anna" }],
  rootfolders: { sonarr: ["/tv/", "/kids/"], radarr: ["/movies"] },
  "logs?lines=1000": { files: [{ name: "clearr.txt", size: 2048 }], lines: ["2026-11-26 10:00:00.000|Warn|Engine|Sonarr <script>bad()</script> slow", "   at Something.Trace()"] },
};

// [host, browser language, Jellyfin's language (plugin only), language expected]
for (const [host, lang, jf, expect] of [["app", "en-US", null, "en"], ["plugin", "sv-SE", "en-GB", "en"], ["app", "sv-SE", null, "sv"], ["plugin", "en-US", "sv", "sv"], ["plugin", "sv-SE", "de", "en"]]) {
  const dom = new JSDOM("<!DOCTYPE html><div id=root></div>", { runScripts: "outside-only", url: "http://clearr.test/" });
  const w = dom.window;
  Object.defineProperty(w.navigator, "languages", { value: [lang] });
  // The UI fetches lang-<code>.json next to itself.
  w.fetch = (url) => {
    const name = String(url).split("/").pop();
    const file = new URL(name, uiDir);
    return Promise.resolve(existsSync(file) ? { ok: true, json: () => Promise.resolve(JSON.parse(readFileSync(file, "utf8"))) } : { ok: false, json: () => Promise.resolve({}) });
  };
  const L = expect === "sv" ? sv : en;
  const tag = `${host} (browser ${lang}${jf ? ", Jellyfin " + jf : ""})`;
  const errors = [];
  w.addEventListener("error", (e) => errors.push(e.message));
  w.eval(ui);
  const calls = [];
  const api = (path) => { calls.push(path); return path in answers ? Promise.resolve(JSON.parse(JSON.stringify(answers[path]))) : Promise.reject(new Error("unexpected " + path)); };
  const root = w.document.getElementById("root");
  const mounted = w.ClearrUI.mount(root, { host, api, language: jf || undefined });
  await mounted.ready;
  const pages = host === "app" ? ["shows", "movies", "history", "settings", "logs"] : ["shows", "movies", "history", "settings"];
  const seen = {};
  for (const p of pages) {
    mounted.show(p);
    await new Promise((r) => setTimeout(r, 20));
    seen[p] = { text: root.textContent, imgs: root.querySelectorAll("[data-el=list] img").length, deleteHidden: root.querySelector("[data-el=deleteNow]").classList.contains("cl-hidden") };
  }

  const text = root.textContent;
  check(errors.length === 0 && !calls.some((c) => !(c in answers)), `${tag}: every page opens without errors (${calls.length} API calls)`);
  check(seen.shows.imgs === 0 && seen.shows.text.includes("<img src=x onerror=alert(1)>") && seen.shows.text.includes("Anna <b>"), `${tag}: names containing HTML are shown as text, not run`);
  check(text.includes(L["notice.upgraded"].replace("{from}", "1").replace("{to}", "2").replace("{file}", "settings.v1.bak.json")), `${tag}: settings notice shown in the banner, translated`);
  check(text.includes(L["action.WouldDeleteHistory"]) && text.includes("3.0 GB") && text.includes("From before translations"), `${tag}: history lists entries in the language, older untranslated ones as they were`);
  check(seen.shows.text.includes(L["reason.ready"].replace("{names}", "Anna")) && seen.movies.text.includes(L["reason.waiting"].replace("{people}", "Anna (0/1, " + L["flagInline.Sampled"] + ")")), `${tag}: reasons rendered from codes`);
  check(seen.shows.deleteHidden && !seen.movies.deleteHidden && seen.shows.text.includes(L["modeFor.shows"].replace("{mode}", L["modeName.DryRun"])) && seen.movies.text.includes(L["modeFor.movies"].replace("{mode}", L["modeName.Delete"])), `${tag}: each page follows its own mode (shows Dry run: no delete button; movies Delete: button shown)`);
  check(text.includes(L["nav.settings"]) && !(expect === "sv" && text.includes("Remove finished seasons")) && !(expect === "en" && text.includes("Ta bort färdigsedda säsonger")), `${tag}: page text is in the right language`);
  check(text.includes(L["stats.total"].replace("{count}", "1").replace("{size}", "1.0 GB")) && text.includes(L["stats.droppedBy"].replace("{count}", "1")) && text.includes("Movie (2020)"), `${tag}: statistics on the History page (total freed, most dropped)`);
  check(root.querySelectorAll(".cl-root").length === 3 && [...root.querySelectorAll(".cl-root")].find((x) => x.value === "/kids/").checked && root.querySelector('[data-el="testNotify"]') !== null && root.querySelector('[data-el="exportBackup"]') !== null, `${tag}: root folders listed (the left-out one ticked), notification test and backup buttons`);
  if (host === "app") {
    check(root.querySelector('[data-page="logs"]') && text.includes("Sonarr <script>bad()</script> slow") && root.querySelector("script") === null, "app: Logs page shows lines as text");
    check(root.querySelector('a[href="api/logs/clearr.txt"]') !== null && root.querySelector('input[name="debugLogging"]').checked, "app: log download link and Debug logging setting");
  } else {
    check(root.querySelector('[data-page="logs"]') === null && root.querySelector('input[name="debugLogging"]') === null && !calls.includes("logs?lines=1000"), "plugin: no Logs page or Debug option (Jellyfin has its own logs)");
    check(root.querySelector('select[name="language"]') === null, "plugin: no Language setting (it follows Jellyfin)");
    check(root.querySelector('input[name="allowDirectDelete"]') !== null && root.querySelector('input[name="jellyfin.url"]') === null, "plugin: Jellyfin fallback shown, Jellyfin connection hidden");
  }
}

// Every language has every key, with the same {placeholders} as English.
const placeholders = (s) => (s.match(/\{\w+\}/g) || []).sort().join(",");
const problems = Object.keys(en).filter((k) => !(k in sv) || placeholders(en[k]) !== placeholders(sv[k]));
check(problems.length === 0 && Object.keys(sv).every((k) => k in en), "Swedish has every English key with the same placeholders" + (problems.length ? ": " + problems.join(", ") : ""));
check(en["modeNote.safe"].endsWith("Use {keep} to protect something."), 'Mode note reads "Use Keep to protect something."');

process.exit(failures ? 1 : 0);
