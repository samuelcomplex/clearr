# Changelog

All notable changes to Clearr. Versions follow [semantic versioning](https://semver.org): until 1.0, a minor version (0.**x**.0) can change behaviour, and the notes say how.

The Jellyfin plugin ([Clearr for Jellyfin](https://github.com/samuelcomplex/clearr-plugin)) is built from the same core. Its version `2.<minor>.<patch>.<build>` follows Clearr's.

## [Unreleased] – 0.2.0

The first public release.

### Clearing
- Tracks each Jellyfin user's progress for every show, season and movie, and clears what everyone who started has finished, through Sonarr and Radarr.
- Flags viewers who dropped off: Sampled, Stalled, Mid-episode.
- Keep per show, season or movie, mirrored as a `keep` tag in Sonarr/Radarr; tags set in Jellyfin, Sonarr or Radarr are respected too.
- Whole shows are removed with their seasons unmonitored and the show left monitored, so new seasons still arrive.

### Safety
- Nothing is removed unless the mode is Delete, enforced by the server and **checked again before every item**: switching away from Delete during a run stops it.
- Preview with sizes, a per-run size limit, unmonitor-before-delete, and a guard against files Jellyfin hasn't seen yet.
- Tested against hundreds of randomly generated libraries, compared with an independent re-implementation of the rules.

### Security
- Without a password, only devices on the local network can use Clearr, also behind a reverse proxy.
- Protection against other websites triggering actions through your browser.
- Five wrong passwords lock that address out for 10 minutes; changing the password signs everyone out.
- API keys and secrets on disk are readable by Clearr's user only; security headers on every page.

### Shows and movies, languages
- Shows and movies each have their own cleanup mode and daily run time (two scheduled tasks in the plugin), so you can clean up one without the other.
- English and Swedish, following the browser or a setting (the plugin follows each Jellyfin user's display language). Reasons, History and results are translated too; older History entries keep their English text.

### Running
- Docker image for amd64 and arm64 on Alpine, running as `PUID`/`PGID` (or Docker's `user:`), with a health check.
- HTTPS on port 8586 with your own certificate or a self-signed one.
- Log files in `/config/logs` with rotation, a Logs page and a Debug logging option.
- Settings carry a format version and are upgraded automatically, with a backup; a damaged settings file no longer stops Clearr from starting.
- Much faster on large libraries: Jellyfin and Sonarr requests run side by side.
- Backup and restore of settings and the Keep list as one file.
- FAQ: how to use Sonarr's and Radarr's Recycling Bin as an undo for removals.

### Statistics
- The History page starts with statistics: space freed and items removed per month for the last year, and the shows and movies viewers drop most often.

### Notifications
- A message after each run that removed something or ran into trouble, to ntfy, Discord and/or any JSON webhook, with a test button. Optional messages after dry runs.
- `/api/summary` for a Home Assistant REST sensor: removals and space freed in the last 30 days, totals, modes and next runs.

### Keeping
- Favourites in Jellyfin are kept automatically (Keep favourites, on by default).
- Optional free-space target: only clean up drives that are running low, longest-finished first, stopping once there's enough room.
- Whole Sonarr/Radarr root folders (a Kids or Anime folder, say) can be kept from Settings.

[Unreleased]: https://github.com/samuelcomplex/clearr/commits/main
