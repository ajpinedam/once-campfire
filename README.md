# Campfire

Campfire is a web-based chat application. It supports many of the features you'd
expect, including:

- Multiple rooms, with access controls
- Direct messages
- File attachments with previews
- Search
- Notifications (via Web Push)
- @mentions
- API, with support for bot integrations

## Running your own Campfire instance

Campfire's Docker image contains everything needed for a fully-functional,
single-machine deployment. This includes the web app, background jobs, caching,
file serving, and SSL. You can use our pre-built image at
`ghcr.io/basecamp/once-campfire:latest`, or build your own from this repo.

### Deploying with ONCE

The easiest way to self-host Campfire is with [ONCE](https://github.com/basecamp/once).
It will guide you through the initial set up and then keep your instance up to date automatically.

If you don't already have `once` installed, run this on the machine you want to run Campfire on:

```sh
curl https://get.once.com | sh
```

`once` will launch as soon as the install is finished. 

Choose Campfire from the list of applications, follow the instructions, and ONCE will take care of the rest.

If you prefer the command line to the dashboard, you can deploy directly:

```sh
once deploy ghcr.io/basecamp/once-campfire --host chat.example.com
```

### Deploying with Docker

If you'd rather run the Docker image yourself, you can read more about that in the [self-hosting guide](docs/self-hosting.md).

> [!TIP]
> When you start Campfire for the first time, you'll be guided through a wizard to create an admin account.
> The email address that you enter for the admin account will be visible on the sign-in page, it's there so
> that people have someone to contact if they need help with their account. If that bothers you, put in any
> email address you want and create yourself a new admin account.

## Other implementations

Campfire also has implementations in Django, Laravel, Express, Elixir, Go and Rust:

| HTTP workload (requests/sec) | Rails | [Django](https://github.com/basecamp/once-campfire-django) | [Laravel](https://github.com/basecamp/once-campfire-laravel) | [Express](https://github.com/basecamp/once-campfire-express) | [Elixir](https://github.com/basecamp/once-campfire-elixir) | [Go](https://github.com/basecamp/once-campfire-go) | [Rust](https://github.com/basecamp/once-campfire-rust) |
|---|---:|---:|---:|---:|---:|---:|---:|
| Room page | 241 | 170 | 164 | 559 | 722 | 3,860 | 36,260 |
| Messages page | 413 | 196 | 175 | 777 | 1,053 | 5,573 | 40,872 |
| Sidebar | 552 | 615 | 715 | 4,125 | 1,275 | 19,753 | 34,672 |
| Search | 435 | 315 | 305 | 1,294 | 1,156 | 7,053 | 33,299 |
| Post a message | 273 | 154 | 137 | 256 | 801 | 4,767 | 6,896 |

Measured with 16 concurrent clients on an AMD Ryzen AI MAX+ 395,
with four hardware threads allocated to each app.

### .NET

A .NET 10 port lives in [`dotnet/`](dotnet/README.md): ASP.NET Core minimal APIs, RazorSlices
views, hand-written SQL over the unchanged Rails schema, and the same frontend served byte for byte.
It was measured against this Rails app side by side on one machine (AMD Ryzen 7 5800XT) with
[`dotnet/bench/compare`](dotnet/bench/compare/README.md), which follows `bench/compare_http.rb`:
each app in its own container on four hardware threads, the load generator on four others,
16 keep-alive clients, one Rails-built seed copied fresh for every run.

| HTTP workload (requests/sec) | Rails, 1 worker | Rails, 4 workers | .NET | .NET ÷ Rails (4 workers) |
|---|---:|---:|---:|---:|
| Room page | 81 | 252 | 5,958 | 23.6× |
| Messages page | 158 | 424 | 8,000 | 18.9× |
| Sidebar | 131 | 438 | 8,731 | 19.9× |
| Search | 83 | 259 | 5,170 | 20.0× |
| Post a message | 105 | 253 | 4,395 | 17.4× |

Different hardware and seed from the table above, so compare ratios rather than numbers. Against
the ratios to Rails above (Go: 16×, 13.5×, 36×, 16×, 17.5×), .NET sits at or above Go on page
rendering and search, level with it on posting and behind it on the sidebar.

Findings along the way:

- **Puma workers matter.** `bench/compare_http.rb` runs one Puma worker, which Ruby's GVL keeps to
  about one core. Four workers (one per allocated thread) roughly triple Rails' throughput and land
  close to the Rails figures in the table above.
- **The Ruby load client saturates first** against faster servers: it measured about 1,200 req/s on
  the room page where the .NET server sustains about 6,000. The figures above use a compiled client.
- **SQLite was the bottleneck, not .NET.** Profiling showed statement preparation costing a third of
  a read request and posts queuing on SQLite's single writer; cached prepared statements and group
  commit took posting from 1,557 to 4,395 req/s.
- **Mention highlighting never matches.** `message_formatter.js` looks for
  `.mention img[src^="/users/{id}/avatar"]`, but avatar URLs carry a signed token instead of the id.

## Development

You are welcome - and encouraged - to modify Campfire to your liking.
Please see our [development guide](docs/development.md) for how to get Campfire set up for local development.

## Security

See [SECURITY.md](SECURITY.md) for how to report a vulnerability and a description of our trust model.
