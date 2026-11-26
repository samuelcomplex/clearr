# Settings reference

Everything is set in the web UI (**Settings**). This page explains each setting and its default.

## Connections

| Setting | Notes |
|---|---|
| **Jellyfin** URL and API key | App only (the plugin is inside Jellyfin already). Create the key in *Jellyfin Dashboard → API Keys → +*. |
| **Sonarr** URL and API key | For shows. The key is in *Sonarr → Settings → General*. |
| **Radarr** URL and API key | For movies. The key is in *Radarr → Settings → General*. |
| **Also remove items Sonarr/Radarr don't manage, through Jellyfin** | Plugin only. **Off** by default. When on, finished items that Sonarr/Radarr don't know about are deleted from disk by Jellyfin itself. Nothing is unmonitored anywhere, so if another tool manages them they may come back. |

URLs can be written with or without `http://` and with or without a trailing slash. **Test** checks the values in the form before you save.

## Cleanup

| Setting | Default | |
|---|---|---|
| **Cleanup mode** | Dry run | **Off**: only track and flag. **Dry run**: the daily run records what it *would* remove. **Delete**: remove files. Nothing is ever removed in any mode except Delete, and the mode is checked again before every single item. |
| **Daily run at** | 04:00 | App only, in the container's `TZ`. The plugin's run is a Jellyfin scheduled task (*Dashboard → Scheduled Tasks → Clearr*). |
| **Max GB removed per run** | 50 | Anything over the limit waits for the next run. 0 = no limit. The first item of a run is always allowed, even if it's bigger than the limit on its own. |
| **Grace period** | 7 days | Time between the last viewer finishing and the removal. |
| **Remove finished seasons** | On | |
| **Remove whole shows when finished** | On | Unmonitors every existing season, deletes all files, and leaves the show itself monitored so new seasons still arrive. |
| **Only remove whole shows that have Ended** | On | Shows that are still running only lose finished seasons. |
| **Never remove the newest season of a continuing show** | On | Protects a season that's still airing. |
| **Remove finished movies** | On | |
| **Count specials (season 0)** | Off | When off, specials are ignored when deciding whether a show is finished. |
| **Refresh the Jellyfin library after a cleanup** | On | So removed items disappear from Jellyfin straight away. |
| **Keep tag** | `keep` | Items with this tag in Jellyfin, Sonarr or Radarr are never removed. Case doesn't matter. |
| **Debug logging** | Off | App only. Writes every decision to the log files. Turn it on while tracking down a problem. |

## Flags

| Setting | Default | |
|---|---|---|
| **Flag a viewer after this many days without progress** | 30 | A viewer who is *In progress* with no activity for this long has **dropped off**. |
| **"Sampled" means watched at most this many episodes** | 3 | Dropped off after this many episodes or fewer: **Sampled**. More: **Stalled**. **Mid-episode** is added if they stopped partway through an episode. |
| **Dropped-off viewers still hold back cleanup** | On | When on, someone who dropped off still counts as "not finished". Turn off to let cleanup go ahead without them, as long as at least one person finished. |

## Users

Tick **Ignore** for accounts that shouldn't count, such as a shared living-room account or a guest. Ignored users never hold anything back, and their watching doesn't make anything eligible either.

## Security (app)

| Setting | |
|---|---|
| **Password** | Optional. Until one is set, Clearr only answers devices on your local network (private addresses, Tailscale and similar VPNs). Set one before exposing Clearr through a reverse proxy. Changing it signs everyone out. Five wrong attempts lock that address out for 10 minutes. |
| **API key** | For scripts and dashboards: send it as an `X-Api-Key` header. It works with or without a password. |

## HTTPS (app)

Not needed if a reverse proxy already serves Clearr over HTTPS.

| Setting | Default | |
|---|---|---|
| **Enable HTTPS** | Off | |
| **HTTPS port** | 8586 | Inside the container. Map it in Docker too (`8586:8586`). |
| **Certificate file** | empty | Empty means a self-signed certificate that Clearr creates and keeps in `/config`. Otherwise a `.pfx`/`.p12`, or a PEM certificate such as Let's Encrypt's `fullchain.pem`. Mount the folder read-only, for example `/path/to/certs:/certs:ro`. |
| **Private key file** | empty | For PEM certificates, such as `privkey.pem`. |
| **Certificate password** | empty | For password-protected `.pfx` files or encrypted keys. |

**Check certificate** tests the files before you save. HTTPS changes take effect after **Restart Clearr**, which needs `restart: unless-stopped`. If the certificate can't be loaded at startup, Clearr keeps running on HTTP and shows the error in Settings.
