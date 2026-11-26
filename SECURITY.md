# Security policy

Clearr deletes files, so security problems are taken seriously.

## Reporting a problem

Please **don't open a public issue** for a security problem. Use GitHub's private reporting instead:
go to the repository's **Security** tab and click **Report a vulnerability**.
You'll get a reply within a week, and a fix as fast as the problem calls for.

Useful to include: the Clearr version (shown in the sidebar), how Clearr is reached (LAN only, reverse proxy, VPN),
and steps to reproduce.

## Supported versions

Only the latest release gets security fixes. Clearr updates through Docker (`:latest`) or Jellyfin's plugin catalog,
so staying current is a pull or an update click away.

## How Clearr protects itself

- **No password set:** only devices on your local network (private addresses, Tailscale, VPN) can use the API.
  Set a password before exposing Clearr through a reverse proxy.
- **Password:** stored as a salted PBKDF2 hash. Five wrong attempts from one address lock it out for 10 minutes.
  Changing the password signs out every existing session.
- **Cross-site requests:** anything that changes settings or deletes files must come from Clearr's own page
  (or carry the API key), so another website can't trigger a cleanup through your browser.
- **Secrets on disk:** `settings.json`, `api.key`, `session.key` and the self-signed certificate are readable by Clearr's user only.
- **Deleting:** nothing is deleted unless the mode is **Delete**, checked again before every single item.
  See the README for the rest of the safety rules.
