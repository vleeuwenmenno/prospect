# Releasing

Pushing a git tag builds container images and publishes them. The tag name picks which.

## Tags

| Tag | Publishes |
| --- | --- |
| `server-vX.Y.Z` | the game server image only |
| `list-vX.Y.Z` | the listing server image only |
| `vX.Y.Z` | both |

- `X.Y.Z` is three numbers. A suffix is allowed: `v1.2.3-rc.1`. Any other tag name fails the workflow.
- Stable `1.2.3` publishes image tags `1`, `1.2`, `1.2.3` and `latest`.
- A suffix marks a pre-release: `1.2.3-rc.1` publishes its exact version and `latest-unstable`, leaving stable tags unchanged.
- Stable releases leave `latest-unstable` unchanged. Aliases move only for the images selected by the git tag.
- During alpha testing, use `latest-unstable` or a pinned pre-release; `latest` is created by the first stable release.

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

GitHub Container Registry, under the repository owner (lower-case), with the version and aliases described above.

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
  (`https://prospect.mvl.sh`; `--listing <url>` for another, `--listing off` for none).
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

- The file can also hold the server's name, visibility, port, player slots, password, listing address and
  connection limits under `"server"`; see [hosting.md](hosting.md). A world-readable file is no place for a
  password: use the variable.
- Without `--world`, the server saves nothing. Saved worlds require a writable volume (below).
- Ids and ranges for settings: `content/rules/settings.json`.

## Dedicated world volume

Saved mode is not joinable yet; remote players are refused until verified ownership is implemented.
The image provides `/data`, owned by its unprivileged user (`APP_UID`, currently 1654).
Use a named volume; a bind mount must already be writable by that uid. Do not use a network filesystem.

```sh
docker run -d --name prospect-saved --stop-timeout 90 -v prospect-worlds:/data \
  ghcr.io/<owner>/prospect-server:latest --world /data/world --create-world --listing off
```

- First run creates the world. Later runs open it, never overwrite it; remove `--create-world` to require existence.
- Add `--autosave 30` to set the interval (1–3600 seconds). No changes means no periodic write; stop still saves.
- Saved seed/settings win. Conflicting explicit launch values are refused under the lease before writable open; see [hosting.md](hosting.md).
- Stop with `docker stop --time 90 prospect-saved`. Read logs for checkpoint/backup failures even with exit 0.
- Retain the named volume across image replacement. Copy only after a successful final checkpoint;
  copying only `world.db` while a WAL is needed can lose committed data.

The published server includes SQLite native runtime assets and the dependency notices from `LICENSES/`.
Linux native-load/process-close and container checks must run before accepting a release;
macOS and Windows are separate checks, not implied by Linux success.

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

## Optional central accounts

Accounts are disabled by default. Enabling them requires Discord credentials and a durable writable /data volume.
The image owns /data as APP_UID; bind mounts must grant that user write access.
SQLite native assets ship for amd64 and arm64. Account configuration, callback registration, backups and logging rules: [accounts.md](accounts.md).

## Protected disk world identity foundation

Linux disk worlds now have an immutable P256 public key in database schema 2 and a separate `world.key`.
Snapshot codec 2 and its account/owner bindings are unchanged; older database schemas are refused without migration.
`SqliteWorldStore.Identity` exposes UUID, canonical 91-byte SPKI (base64), SHA256 fingerprint and key status.
Missing, malformed, mismatched or unsafe keys never regenerate; private gameplay remains loadable.
This status is an observation at open, not hosting authorization. Saved multiplayer remains locked.

- Key creation uses mode 0600 from the first byte, file flush and durable publication before database publication.
- Only Linux protection is supported. Windows and macOS creation are refused; copied worlds can open privately with status Unsafe.
- Automatic database copies are gameplay-only: they cannot recover a lost private key. Protected identity recovery artifacts are not implemented.
- Move or copy the complete stopped folder, preserving key permissions, to retain identity. A bare database is insufficient.
- Independent Claude security review remains required. No admission, TLS, key rotation or destructive recovery is included.
