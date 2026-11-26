# FAQ and troubleshooting

## Why wasn't this season (or show, or movie) removed?

Every item shows its reason in **Shows**/**Movies**, and every skip is in **History**. What they mean:

| Reason | Why | What to do |
|---|---|---|
| **Nobody has started it** | Clearr only removes what someone has watched. | Nothing. Untouched items are never removed. |
| **Waiting on Anna (5/10)** | Someone started and hasn't finished. | Wait. Or, if they've dropped off, see *dropped-off viewers* below. |
| **Waiting on Bjorn (2/10, sampled)** | Bjorn dropped off, and dropped-off viewers hold back cleanup. | Untick *Dropped-off viewers still hold back cleanup*, or tick **Ignore** for that user. |
| **Nobody has finished it (everyone who started dropped off)** | Everyone gave up; nobody finished. | Clearr won't remove things nobody finished. Delete it yourself if you like. |
| **Everyone finished, but it's marked keep** | Kept in Clearr, or tagged `keep` in Jellyfin, Sonarr or Radarr. | Remove the keep (or the tag) if you want it gone. |
| **Everyone finished; grace period until …** | The grace period hasn't passed. | Wait, or shorten **Grace period**. |
| **Newest season of a show that's still continuing** | Protects a season that's still airing. | Untick *Never remove the newest season of a continuing show*. |
| **Show status is Continuing** | Whole shows are only removed once they've Ended. Finished seasons can still go. | Untick *Only remove whole shows that have Ended*. |
| **A season is marked keep** | A kept season blocks the whole show; its other seasons can still be removed. | |
| **Not in Sonarr / Not in Radarr** | Clearr only removes what Sonarr/Radarr manage. | Add it to Sonarr/Radarr, or (plugin only) turn on removal through Jellyfin. |
| **Sonarr has 11 files in season 2 but Jellyfin only lists 10 episodes** | Something new downloaded and Jellyfin hasn't scanned it yet. | Nothing. It goes through once Jellyfin has scanned it. |
| **Per-run limit of 50 GB reached** | The rest waits for the next run. | Raise **Max GB removed per run** if you like. |
| **Stopped: cleanup mode was changed during the run** | You switched away from Delete while it was running. | That's the safety stop working as intended. |

## The page says "Clearr only accepts connections from your local network"

No password is set, and you're connecting from outside your network (or through a reverse proxy from the internet). Set a password in **Settings → Security** from a device at home, or connect over your VPN.

## "Missing X-Clearr-Request header" or "Cross-site request refused"

Clearr only accepts changes from its own page. You'll see this if:

- **A script calls the API:** send the API key as `X-Api-Key` and it works.
- **A reverse proxy rewrites the address:** the proxy must pass on the original host. Most do this by default; see [Reverse proxy](reverse-proxy.md).

## Test says 401 or "Unauthorized"

The API key is wrong, or belongs to a different server. Copy it again from Sonarr/Radarr (*Settings → General*) or create a new one in Jellyfin (*Dashboard → API Keys*).

## Test says "connection refused" or times out

The URL is wrong, or Clearr can't reach that machine. From inside Docker, `localhost` means the Clearr container itself. Use the server's LAN address (such as `http://192.168.1.6:8989`) or, if they share a Docker network, the container name (such as `http://sonarr:8989`).

## The daily run happens at the wrong time

Set `TZ` on the container, for example `TZ=Europe/Stockholm`. The sidebar shows the next run time.

## Where are the logs?

- **App:** the **Logs** page, or `/config/logs/clearr.txt`. Turn on **Debug logging** in Settings to see every decision.
- **Plugin:** Jellyfin's own logs (*Dashboard → Logs*); look for lines starting with `Clearr`.

Attach them when reporting a problem.

## Does Clearr delete files itself?

The app never touches files. Sonarr and Radarr delete them when Clearr asks. The plugin does the same, and only deletes through Jellyfin for items Sonarr/Radarr don't manage if you've turned that on.

## Can I undo a removal?

Not from Clearr. Removed files are gone, just as if you had deleted them in Sonarr/Radarr. The show or movie stays in Sonarr/Radarr (unmonitored), so you can search for it again there. That's why Clearr starts in Dry run and has the per-run limit.

## Can I use both the app and the plugin?

You can install both, but only one should be in Delete mode. Otherwise both act on the same library.
