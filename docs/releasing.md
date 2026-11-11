# Releasing

Pushing a git tag builds container images and publishes them. The tag name picks which.

## Tags

| Tag | Publishes |
| --- | --- |
| `server-vX.Y.Z` | the game server image only |
| `list-vX.Y.Z` | the listing server image only |
| `vX.Y.Z` | both |

- `X.Y.Z` is three numbers. A suffix is allowed: `v1.2.3-rc.1`. Any other tag name fails the workflow.
- A suffix marks a pre-release: it gets its version tag but never moves `latest`.

```sh
git tag server-v0.1.0 && git push origin server-v0.1.0   # game server only
git tag list-v0.1.0 && git push origin list-v0.1.0       # listing server only
git tag v0.1.0 && git push origin v0.1.0                 # both
```

## What the workflow does

File: `.github/workflows/release.yml`.

1. Reads the tag and decides which images to build.
2. Builds and runs the tests of the part being released. A failure stops it before anything is published.
3. Builds the image and pushes it with the workflow's own `GITHUB_TOKEN`.

- The game server is built after `Prospect.Tests`; the listing server after `Prospect.Listing.Tests`.
- Both images carry the OCI labels `source`, `revision` and `version`.

## Where the images end up

GitHub Container Registry, under the repository owner (lower-case). Each is tagged `X.Y.Z` and `latest`.

| Image | Platforms |
| --- | --- |
| `ghcr.io/<owner>/prospect-server` | `linux/amd64` |
| `ghcr.io/<owner>/prospect-listing` | `linux/amd64`, `linux/arm64` |

- The game server is amd64 only: BRUTAL's RakNet package has no `linux-arm64` library.
- A new package is private. Make it public once in the package's settings on GitHub.

## Run the game server

```sh
docker run -d -p 27420:27420/udp ghcr.io/<owner>/prospect-server:latest --name "My server"
```

- Game port: UDP `27420`. Change it with `--port` and publish the same port on the host: a public server tells
  the list the port it listens on.
- Everything after the image name is the server's command line (`--help` lists it): `--name`, `--visibility`,
  `--port`, `--max-players`, `--password`, `--listing`, `--seed`, `--settings`, `--set id=value`.
- Unlisted by default: joined by address. `--visibility public` announces it to the listing server
  (`https://mvl.sh`; `--listing <url>` for another, `--listing off` for none).
- The password goes in the `PROSPECT_PASSWORD` environment variable, not on the command line:

```sh
docker run -d -p 27420:27420/udp -e PROSPECT_PASSWORD='open sesame' \
  ghcr.io/<owner>/prospect-server:latest --name "My server" --visibility public
```

- The password protects itself, not the traffic: game packets are not encrypted. See [hosting.md](hosting.md).
- World settings from a file: mount it read-only and point `--settings` at it. The container runs as an
  unprivileged user, so the file must be world-readable.

```sh
docker run -d -p 27420:27420/udp -v ./settings.json:/config/settings.json:ro \
  ghcr.io/<owner>/prospect-server:latest --settings /config/settings.json --set <id>=<value>
```

- The file can also hold the server's name, visibility, port, player slots, password and listing address under
  `"server"`; see [hosting.md](hosting.md). A world-readable file is no place for a password: use the variable.
- The server writes no files. Nothing needs a volume.
- Ids and ranges for settings: `content/rules/settings.json`.

## Run the listing server

```sh
docker run -d -p 127.0.0.1:8080:8080 -e Listing__TrustedProxies=172.16.0.0/12 ghcr.io/<owner>/prospect-listing:latest
```

- HTTP on port `8080`, published on `127.0.0.1` only: the proxy on the same host is the one thing that may reach it.
  Published on every interface, a direct client would arrive from the Docker bridge, a trusted address, and could
  write its own `X-Forwarded-For`.
- Health check: `GET /healthz`.
- Settings are environment variables `Listing__<Key>`.
- Behind a reverse proxy, set `Listing__TrustedProxies`. Keys and proxy notes: [listing-server.md](listing-server.md).

## Build an image locally

```sh
make server-image     # prospect-server
make listing-image    # prospect-listing
```
