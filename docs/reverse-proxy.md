# Running Clearr behind a reverse proxy

Clearr works behind Nginx Proxy Manager, Traefik, Caddy, SWAG and similar proxies, on its own domain (`https://clearr.example.com`) or a sub-path (`https://example.com/clearr/`).

**Set a password first** if the proxy is reachable from the internet (**Settings → Security**). Until one is set, Clearr only answers visitors on your local network. It reads the visitor's real address from the proxy's `X-Forwarded-For` header, trusting that header only when the proxy itself is on a local address.

HTTPS on the proxy is all you need. Leave Clearr's own HTTPS off.

## Nginx Proxy Manager

1. **Hosts → Proxy Hosts → Add Proxy Host**.
2. **Domain name:** `clearr.example.com`. **Scheme:** `http`. **Forward hostname / IP:** the server's address (or `clearr` if they share a Docker network). **Forward port:** `8585`.
3. **SSL** tab: request a certificate and tick **Force SSL**.

NPM passes on the visitor's address and host by default.

## Caddy

```
clearr.example.com {
    reverse_proxy clearr:8585
}
```

## Traefik (Docker labels)

```yaml
    labels:
      - traefik.enable=true
      - traefik.http.routers.clearr.rule=Host(`clearr.example.com`)
      - traefik.http.routers.clearr.entrypoints=websecure
      - traefik.http.routers.clearr.tls.certresolver=letsencrypt
      - traefik.http.services.clearr.loadbalancer.server.port=8585
```

## Nginx / SWAG on a sub-path

The proxy must remove the prefix before passing requests on. Clearr's page uses relative links, so it then works under any path:

```nginx
location /clearr/ {
    proxy_pass http://clearr:8585/;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Host $host;
}
```

## Single sign-on (Authelia, Authentik)

Put Clearr behind your forward-auth like any other app. Exclude `/ping` if your setup health-checks it. Keep Clearr's own password too, or at least don't expose port 8585 directly.

## Health checks

`GET /ping` answers `ok` without signing in. The Docker image already uses it for its health check.
