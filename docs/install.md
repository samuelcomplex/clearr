# Installing Clearr

Clearr comes in two forms with the same features:

| | Clearr (app) | Clearr for Jellyfin (plugin) |
|---|---|---|
| Runs | As its own Docker container, next to Sonarr and Radarr | Inside Jellyfin |
| Opens at | `http://<server>:8585` | Jellyfin **Dashboard → Clearr** |
| Daily run | Its own schedule (Settings) | A Jellyfin scheduled task |
| Sign-in | Optional password, API key | Jellyfin administrators |
| Items Sonarr/Radarr don't manage | Never removed | Optionally removed through Jellyfin (off by default) |

Pick **one**. Running both in Delete mode against the same library would make them race each other.

## Docker Compose / Portainer

```yaml
services:
  clearr:
    image: ghcr.io/samuelcomplex/clearr:latest
    container_name: clearr
    environment:
      - TZ=Europe/Stockholm      # the daily run uses this time zone
      - PUID=1000                # user and group Clearr runs as; files in /config
      - PGID=1000                #   belong to them (find yours with: id)
    volumes:
      - ./clearr:/config         # settings, keep list, history and logs
    ports:
      - 8585:8585                # web UI
    restart: unless-stopped      # also lets "Restart Clearr" in Settings work
```

In Portainer: **Stacks → Add stack**, paste the above, then **Deploy**.

Clearr needs **no access to your media folders**. It works entirely through the Jellyfin, Sonarr and Radarr APIs.

### Environment variables

| Variable | Default | What it does |
|---|---|---|
| `TZ` | `Etc/UTC` | Time zone for the daily run, such as `Europe/Stockholm` |
| `PUID` / `PGID` | `1000` / `1000` | The user and group Clearr runs as. Existing files in `/config` are given to them on start. |
| `UMASK` | `022` | Permissions for new files. Files with secrets are always private to Clearr's user. |
| `CLEARR_PORT` | `8585` | The web UI port inside the container |

Running with Docker's `user:` option (or rootless Docker) also works. Clearr then skips `PUID`/`PGID` and just runs as that user.

### Image tags

| Tag | |
|---|---|
| `latest` | The newest release |
| `0.2.0` (etc.) | One specific version, if you prefer to update by hand |

Images are built for `amd64` and `arm64` (Raspberry Pi 4/5, most ARM NAS boxes).

## docker run

```sh
docker run -d --name clearr \
  -e TZ=Europe/Stockholm -e PUID=1000 -e PGID=1000 \
  -v /path/to/clearr:/config \
  -p 8585:8585 \
  --restart unless-stopped \
  ghcr.io/samuelcomplex/clearr:latest
```

## Unraid

Clearr is being submitted to Community Applications. Once it's listed, search for **Clearr** in the **Apps** tab: the template fills in the port, the `/config` path (`/mnt/user/appdata/clearr`) and Unraid's `PUID=99`, `PGID=100`.

Until then, add it under **Docker → Add Container** with repository `ghcr.io/samuelcomplex/clearr:latest`, port `8585`, a path `/config` → `/mnt/user/appdata/clearr`, and the variables `PUID=99`, `PGID=100` and `TZ`.

## Jellyfin plugin

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and add:
   `https://raw.githubusercontent.com/samuelcomplex/clearr-plugin/main/manifest.json`
2. Go to **Catalog**, find **Clearr**, install it, and restart Jellyfin.
3. Open **Dashboard → Clearr**.

The plugin needs Jellyfin **10.11 or newer**.

## First run

1. Open **Settings → Connections**. In the app, add **Jellyfin** (URL and an API key from *Jellyfin Dashboard → API Keys → +*). In both, add **Sonarr** and/or **Radarr** (URL and API key from *Settings → General*). Click **Test** on each, then **Save**.
2. Look through **Shows** and **Movies**. Everyone's existing watch history counts from day one.
3. Leave the mode on **Dry run** for a week or so. **History** shows what *would* have been cleared each night. Click **Keep** on anything you want to hold on to.
4. When the list looks right, set **Cleanup mode** to **Delete**.

If Clearr can be reached from outside your home network, set a password first (**Settings → Security**). Until a password is set, Clearr only answers devices on your local network. See [Reverse proxy](reverse-proxy.md).

## Updating

- **Docker:** pull the new image and recreate the container (in Portainer: **Stacks → clearr → Update the stack → Re-pull image**). Settings carry over, and older settings files are upgraded automatically, with a backup kept in `/config`.
- **Plugin:** Jellyfin shows the update under **Plugins**. Install it and restart Jellyfin.

## Backing up

Everything Clearr knows is in `/config` (the plugin keeps it in Jellyfin's `data/clearr` folder):

| File | |
|---|---|
| `settings.json` | All settings, including your Sonarr/Radarr/Jellyfin API keys |
| `keep.json` | Everything marked Keep in Clearr |
| `history.json` | What was removed, previewed, skipped or failed |
| `api.key`, `session.key` | Clearr's own API key and sign-in secret |
| `logs/` | Log files |

Watch progress isn't stored by Clearr. It's always read fresh from Jellyfin.

## Removing Clearr

Stop and remove the container (or uninstall the plugin), then delete the `/config` folder. Clearr doesn't change anything in Jellyfin, Sonarr or Radarr apart from the files it removed, the items it unmonitored, and the `keep` tags it added.
