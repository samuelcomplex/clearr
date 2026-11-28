# Contributing to Clearr

Thanks for helping. Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

Open an issue with the **Bug report** form. Logs help most: the **Logs** page in the app (turn on **Debug logging** first), or Jellyfin's logs for the plugin.

Security problems go through the repository's **Security** tab instead (see [SECURITY.md](SECURITY.md)), not a public issue.

## Working on the code

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for the JavaScript lint, Node.js 22.

```sh
dotnet run --project src/Clearr                 # the app on http://localhost:8585
dotnet run --project tests/Clearr.Tests         # all tests, against built-in fake Jellyfin/Sonarr/Radarr
dotnet run --project tools/Demo                 # a demo household on http://127.0.0.1:6567
```

| Folder | |
|---|---|
| `src/Clearr.Core` | Everything shared with the Jellyfin plugin: the rules (`Analyzer`), cleanups (`ClearrEngine`), API clients, storage and the web UI (`ui/`) |
| `src/Clearr` | The app: web server, sign-in, schedule, HTTPS, logs |
| `tests/Clearr.Tests` | Tests, including the deletion safety tests and a performance check |
| `tests/Integration` | The weekly end-to-end test against real Jellyfin, Sonarr and Radarr containers |
| `docker/` | The container's entrypoint and health check |

## Before opening a pull request

```sh
dotnet run --project tests/Clearr.Tests
dotnet format src/Clearr --verify-no-changes
npm ci && npx eslint
```

CI also builds with warnings as errors, runs CodeQL, lints the Dockerfile and workflows, and checks that the container really runs.

**Anything that changes what gets deleted needs tests.** Add a case to `tests/Clearr.Tests/SafetyTests.cs`, and if you change a rule, update the independent `Oracle` there too, so the randomized test keeps checking it. Changes to settings that already exist need a step in `SettingsMigrations`.

Keep the UI and messages in plain English, and keep the plugin in mind: `Clearr.Core` must keep building for `net8.0` and work inside Jellyfin.

## Translations

All text lives in `src/Clearr.Core/ui/lang-<code>.json`, one file per language, used by both the app and the plugin. To add a language:

1. Copy `lang-en.json` to `lang-<code>.json` (for example `lang-de.json`) and translate the values. Keep every `{placeholder}` as it is.
2. Add the language to `LANGUAGES` at the top of `src/Clearr.Core/ui/clearr-ui.js`, with its own name for itself (`de: 'Deutsch'`).
3. Run `npm run test:ui`, which checks that every key is there with the same placeholders.

Anything missing falls back to English, so a partial translation still works.

## License

By contributing you agree that your contribution is licensed under the [GPL-3.0](LICENSE), like the rest of Clearr.
