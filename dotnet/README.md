# Campfire for .NET

A port of [Campfire](../README.md) from Rails to **.NET 10**, built on ASP.NET Core minimal APIs.
It is the same product: the browser runs the very same Stimulus controllers, Turbo, Lexxy editor,
stylesheets, images and sounds, and the server speaks the same protocols (HTML + Turbo Streams,
the ActionCable WebSocket protocol, the bot API) against the same SQLite schema.

## Running it

Requirements: the .NET 10 SDK. Optional: `ffmpeg`/`ffprobe` on `PATH` for video posters.

```sh
cd dotnet
dotnet run --project src/Campfire.Web          # http://localhost:3000
dotnet test                                     # the test suite (555 tests)
```

The first visit starts the setup wizard to create the administrator. Data lives in
`src/Campfire.Web/App_Data/` (database in `db/`, uploads in `files/`) unless `STORAGE_PATH` says
otherwise. In development a `SECRET_KEY_BASE` is generated into `App_Data/local_secret.txt`.

### Configuration

| Variable | Purpose |
|---|---|
| `SECRET_KEY_BASE` | Root secret for signed cookies, CSRF tokens, signed ids and stream names. **Required in production.** |
| `VAPID_PUBLIC_KEY` / `VAPID_PRIVATE_KEY` | Web Push key pair. Without them push notifications are skipped. |
| `STORAGE_PATH` | Database + files root (Docker: `/rails/storage`, the Rails image's path). |
| `DISABLE_SSL` | In production the app assumes TLS is terminated in front of it (HSTS, secure cookies); set to serve plain HTTP. |
| `APP_VERSION`, `GIT_REVISION` | Shown in the UI and the `X-Version`/`X-Rev` headers. |
| `ASPNETCORE_URLS`, `ASPNETCORE_ENVIRONMENT` | Standard ASP.NET Core settings. |

### Admin commands

The Rails `script/admin/*` tools are subcommands of the app:

```sh
dotnet Campfire.Web.dll admin generate-secrets        # SECRET_KEY_BASE + VAPID keys (prints secrets!)
dotnet Campfire.Web.dll admin create-vapid-key
dotnet Campfire.Web.dll admin reset-password <email> <password>
dotnet Campfire.Web.dll admin prepare-backup          # online SQLite backup to storage/backups
```

### Docker

```sh
docker build -t campfire-dotnet dotnet
docker run --volume campfire:/rails/storage -e SECRET_KEY_BASE=... -e TLS_DOMAIN=chat.example.com \
  -p 80:80 -p 443:443 campfire-dotnet
```

The image mirrors the Rails one: one process (web, cable, background jobs) behind
[Thruster](https://github.com/basecamp/thruster) for TLS, HTTP/2 and compression, running as uid 1000
with storage at `/rails/storage`, and the ONCE `pre-backup`/`post-restore` hooks.

## Migrating from the Rails app

Point `STORAGE_PATH` at an existing Campfire `storage/` directory. The schema is Rails'
`db/schema.rb` verbatim, times use the Rails text format, uploads keep Active Storage's disk
layout, passwords are bcrypt — accounts, rooms, messages, attachments and logins carry over.
What doesn't: existing browser sessions (cookies are signed differently, so people sign in once
more) and Rails-signed URLs (avatar and attachment URLs are re-issued on render).

## Architecture

| Rails | .NET |
|---|---|
| Controllers + routes.rb | Minimal API endpoint groups per feature (`Features/`), same URLs |
| ERB views + helpers | [RazorSlices](https://github.com/DamianEdwards/RazorSlices) templates (`Views/`), source-generated, streamed unbuffered |
| Active Record | Hand-written SQL over `Microsoft.Data.Sqlite` (`Data/Queries/`), records read by ordinal |
| `Current`, concerns, `before_action` | `Current` (per-request state) + one middleware driven by endpoint metadata (`AccessPolicy`) |
| Action Cable + Redis | In-process WebSocket server speaking the ActionCable protocol (`Cable/`) |
| Active Job + Resque + Redis | In-process bounded queue + workers (`Jobs/`) |
| Action Text | `RichText/` (AngleSharp parsing, safe-list sanitizing, auto-linking, plain text) |
| Active Storage + libvips | `Storage/` (same disk layout; NetVips variants; ffmpeg posters) |
| web-push gem | `Push/` (VAPID + RFC 8291 `aes128gcm` with .NET cryptography) |
| Propshaft + importmap-rails | `Assets/` (digested in-memory assets, CSS url rewriting, generated importmap) |

Design choices:

- **Immutability.** Domain data and view models are `sealed record`s; collections cross module
  boundaries as `IReadOnlyList<T>`; static tables are `FrozenDictionary`/`FrozenSet`; services are
  sealed and stateless or internally synchronized.
- **Speed.** No ORM or reflection on hot paths: ordinal readers, `json_each` for `IN` lists,
  prepared-once regexes (`[GeneratedRegex]`), source-generated JSON, assets served from memory with
  precompressed Brotli, the account cached in memory, and rendered messages cached as UTF-8
  fragments (the Rails app's `cache` blocks) and written straight to the response. SQLite
  connections are pooled with their prepared statements; posts are group-committed (one
  transaction for every message that arrives while the last batch commits); pages are rendered into
  a pooled buffer and sent in one write. Each of these came from profiling, not guessing.
- **Same security model.** Session cookies, masked CSRF tokens, purpose-bound signed ids and stream
  names (HKDF-derived keys), SSRF guarding for link previews and push endpoints, Active Storage's
  rules for serving untrusted content, bans and rate limits — see [SECURITY.md](../SECURITY.md).

See [docs/PORTING.md](docs/PORTING.md) for the conventions the port follows.

## Status

Feature-complete against the Rails app: setup wizard, sign-in/sign-out/transfer links, join links,
rooms (open, closed, direct), messages with rich text, mentions, link previews, sounds, file and
image/video attachments, boosts, editing, search, unread tracking, presence and typing, sidebar,
profiles and avatars, account administration (people, bots, join code, logo, custom styles), bans,
web push, bot API and webhooks, PWA manifest and service worker, QR codes, admin commands.

- **Tests:** 555 (`dotnet test`), porting the Rails suite's controller, model, channel, helper and
  library tests, plus protocol tests (ActionCable over real WebSockets, RFC 8291 test vectors).
- **Browser-verified** against the unchanged frontend in Chrome: setup, posting through Lexxy,
  live delivery over ActionCable, boosts, editing, search, room creation, unread badges,
  bot posts and attachments, sign-out/sign-in.
- **Docker image** builds and serves through Thruster; video posters verified with ffmpeg inside it.

### Throughput: side by side with Rails

`bench/compare/compare.sh` runs both apps on the same machine (Ryzen 7 5800XT) the way the repo's
`bench/compare_http.rb` does: each in its own container pinned to 4 hardware threads, the load
generator on 4 others, 16 keep-alive clients, uncompressed responses read in full, every response
200, a fresh copy of one Rails-built seed per run (29 users, 7 rooms, 300 messages with mentions,
links and boosts), the same paths. Medians of two alternating rounds, 10 s per workload:

| Workload | Rails, 1 worker | Rails, 4 workers | .NET | .NET ÷ Rails (4 workers) | p99 Rails (4w) | p99 .NET |
|---|---:|---:|---:|---:|---:|---:|
| Room page | 81 | 252 | 5,958 | 23.6× | 188 ms | 9.9 ms |
| Messages page | 158 | 424 | 8,000 | 18.9× | 112 ms | 5.1 ms |
| Sidebar | 131 | 438 | 8,731 | 19.9× | 78 ms | 4.1 ms |
| Search | 83 | 259 | 5,170 | 20.0× | 137 ms | 7.2 ms |
| Post a message | 105 | 253 | 4,395 | 17.4× | 167 ms | 20.7 ms |

- `compare_http.rb` runs Puma with one worker, which Ruby's GVL keeps to about one core; four
  workers use the four allocated threads (and land close to the top-level README's Rails figures).
- Measured with the compiled client in `bench/Campfire.Bench`; the repo's Ruby client saturates
  first against .NET (`CLIENT=ruby` reproduces that). See [bench/compare/README.md](bench/compare/README.md).
- Against the top-level README's ratios to Rails (Go: 16×, 13.5×, 36×, 16×, 17.5×; Rust: 150×,
  99×, 63×, 76×, 25×), .NET sits at or above Go on page rendering and search, level with it on
  posting and behind it on the sidebar. Seeds and machines differ from that table, so treat the
  placement as approximate.
- Profiling-driven changes took the port from 4,916 / 6,658 / 6,700 / 4,856 / 1,557 req/s to the
  figures above: pooled connections with cached prepared statements (statement preparation was a
  third of a read request), group commit for posts (they were queued on SQLite's single writer),
  pages rendered into a pooled buffer and sent in one write (Kestrel's per-write lock was
  contended), single-parse message bodies, and fewer AngleSharp documents per parse.

### Intentional differences from the Rails app

- Writes are serialized in-process (`Data/WriteGate.cs`) instead of colliding on SQLite's lock;
  Microsoft.Data.Sqlite's busy retry sleeps 150 ms, which dominated posting's tail latency.
- DOM ids for rooms are always `room_{id}` (Rails used the STI class, e.g. `list_rooms_open_5`,
  so converting a room between open and closed left sidebar updates pointing at the old id).
- Image variants are cached as files under `files/variants/` rather than rows in
  `active_storage_variant_records`; variants an existing Rails install generated are regenerated.
- A message attachment whose attachable is gone renders nothing (Rails rendered "☒").
- Banning skips private/loopback session addresses instead of failing the whole ban.
- The account people list renders the first 500 and lazy-loads the rest (Rails rendered everyone,
  then appended page 2 again).
- Malformed input that crashed Rails answers 4xx instead: missing sign-up fields (422), an unknown
  involvement level (422), an email change colliding with another user (ignored).
- A blank webhook URL means no webhook; blank searches aren't recorded; push bodies are shortened
  to fit a single encrypted record (oversized payloads were rejected by push services).
- Turbo Frame responses are rendered without turbo-rails' frame layout (Turbo only reads the frame).

### Noticed upstream

`message_formatter.js` highlights mentions of the current user with
`.mention img[src^="/users/{id}/avatar"]`, but avatar URLs use a signed token rather than the id
(in Rails too), so that highlight never matches. The port keeps the Rails behavior.
