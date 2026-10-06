# Porting guide: Campfire (Rails) → .NET 10

This is the working agreement for the .NET port in `dotnet/`. The Rails app at the repository root
is the behavioral reference: when in doubt, read the Rails code and do what it does.

## Goals

- **Same product, same frontend.** The Stimulus controllers, Turbo, Lexxy, CSS, images and sounds are
  served byte-identical from `wwwroot/assets`. The backend must therefore emit the same HTML (ids,
  classes, `data-*` attributes, Turbo Frames/Streams) and speak the same protocols (Turbo Stream
  responses, the ActionCable JSON protocol, the bot JSON API).
- **Same data.** The SQLite schema is Rails' `db/schema.rb` verbatim and times are stored in Rails'
  text format, so an existing Campfire `storage/` directory (database + files) works unchanged.
- **Fast and immutable.** Minimal APIs, RazorSlices (source-generated, unbuffered) views, hand-written
  SQL over `Microsoft.Data.Sqlite`, no reflection-heavy libraries. Domain types are `sealed record`s;
  public collections are `IReadOnlyList<T>`/`IReadOnlyDictionary`; static lookups are
  `FrozenDictionary`/`FrozenSet`; regexes are `[GeneratedRegex]`; JSON is source-generated.

## Solution layout

```
dotnet/
  Campfire.slnx
  src/Campfire.Web/
    Program.cs                 composition root (DI, middleware, module registration)
    Configuration/             CampfireSettings (env-driven, immutable)
    Data/                      Database/Sql (connection + transaction), SqlTime, Schema
      Queries/                 static partial query classes per table (Users, Rooms, Messages, ...)
    Domain/                    sealed records + enums (User, Room, Message, Sound, ...)
    Security/                  KeyRing (HKDF keys), MessageSigner, SignedIds, Csrf, Passwords, SecureTokens
    Http/                      Current (per-request state), CampfireRequestMiddleware, AccessPolicy,
                               AppCookies (session/flash/return-to/last-room), Authentication, Respond
    Platform/                  ApplicationPlatform (user agent → browser/OS, allow_browser)
    Assets/                    AssetCatalog (Propshaft: digests, CSS url rewriting), ImportMap, AssetMiddleware
    Turbo/                     TurboStream (stream element builders), StreamNames
    Views/                     RazorSlices templates + ViewHelpers, Paths, DomId, PageContext, Slices
    Jobs/                      BackgroundQueue/BackgroundWorker (in-process ActiveJob)
    Json/                      AppJsonContext (partial; add your DTOs in your own folder)
    Cable/  RichText/  Storage/  Push/  Webhooks/  Net/  OpenGraph/  Features/   (workstreams)
  tests/Campfire.Tests/        xUnit + WebApplicationFactory (Support/: CampfireApp, Fixtures, CampfireClient)
```

## Workstreams and file ownership

Several people (agents) work at once in the same tree. **Only create or edit files you own.** If you
need a change in a file you don't own, add what you need in your own files (query classes are
`partial`, `AppJsonContext` is `partial`) or note it in your final report.

| Workstream | Owns |
|---|---|
| Lead (foundation) | everything not listed below, incl. `Program.cs`, `Data/` core, `Http/`, `Security/`, `Views/` infra (`ViewHelpers`, `Paths`, `Layouts/`, `Shared/`, `Pwa/`) |
| **Cable** | `Cable/**`, `tests/**/Cable/**` |
| **RichText** | `RichText/**`, `tests/**/RichText/**` |
| **Storage** | `Storage/**`, `Data/Queries/*.Storage.cs`, `tests/**/Storage/**` |
| **Integrations** | `Net/**`, `Push/**`, `Webhooks/**`, `OpenGraph/**`, `Features/PushSubscriptions/**`, `Features/UnfurlLinks/**`, `Views/PushSubscriptions/**`, `Data/Queries/*.Integrations.cs`, `tests/**/Integrations/**` |
| **Rooms & Messages** | `Features/RoomsFeatures.cs`, `Features/{Rooms,Messages,Boosts,Sidebar,Searches,Welcome,Autocomplete}/**`, `Views/{Rooms,Messages,Boosts,Sidebar,Searches,Welcome,Autocomplete}/**`, `Data/Queries/*.Rooms.cs`, `tests/**/Rooms/**` |
| **Accounts & People** | `Features/AccountFeatures.cs`, `Features/{Sessions,FirstRun,Users,Profiles,Avatars,Accounts,Pwa,QrCodes,Health}/**`, `Views/{Sessions,FirstRun,Users,Profiles,Accounts}/**`, `Data/Queries/*.Accounts.cs`, `tests/**/Accounts/**` |

Files marked `CONTRACT STUB` define signatures other workstreams already call. Implement the bodies;
**do not change existing public signatures** (adding members/overloads is fine).

### Building while others are building

Always build and test with your own artifacts directory so parallel builds never collide:

```sh
cd dotnet
dotnet build src/Campfire.Web --artifacts-path /tmp/campfire-<workstream>
dotnet test tests/Campfire.Tests --artifacts-path /tmp/campfire-<workstream> --filter "FullyQualifiedName~Campfire.Tests.<Area>"
```

Other workstreams' files may be mid-edit and fail to compile. **Judge your work only by errors in
files you own**; ignore the rest (they don't stop the compiler reporting yours). Before finishing,
make sure none of your files produce errors or warnings.

## Conventions

### C#

- **Namespace shadowing.** Feature namespaces (`Campfire.Web.Features.Messages`, `.Rooms`, `.Sessions`,
  `.Users`, test namespaces like `Campfire.Tests.Rooms`) have the same names as query classes. Inside
  `Campfire.Web.Features.*`, `Messages.Create(...)` would bind to the sibling *namespace*, because C#
  checks enclosing namespaces' members before top-of-file `using`s. Put `using Campfire.Web.Data.Queries;`
  **after** the file-scoped `namespace` line (those usings are checked first), or use an alias.

- `sealed record` for data (domain, view models, DTOs, job payloads). `sealed class` for services.
  No mutable public state; services registered as singletons must be thread-safe.
- Prefer `IReadOnlyList<T>`/`IReadOnlyCollection<T>` on public surfaces; build with `List<T>`/arrays.
- `[GeneratedRegex]` for every regex; `FrozenDictionary`/`FrozenSet` for static tables.
- File-scoped namespaces matching folders (`Campfire.Web.Features.Rooms`).
- Comments explain *why* (the Rails code is full of good "why" comments — carry them over).
- No new NuGet packages without a strong reason (state it in your report). Available: AngleSharp,
  HtmlSanitizer (Ganss.Xss), NetVips (+ native libvips), BCrypt.Net-Next, QRCoder, RazorSlices.

### Data access

- `Sql` (one pooled connection) is registered **scoped**: handlers take `Sql sql` as a parameter.
  Cable connections and jobs open their own: `using var sql = database.Open();`.
- Queries live in static partial classes in `Data/Queries` (`Users.Find(sql, id)`), with column lists
  and readers in `Rows`. Add your own queries in your own partial file, e.g.
  `Data/Queries/Messages.Rooms.cs` → `public static partial class Messages { ... }`.
- The API is synchronous (SQLite is in-process; Microsoft.Data.Sqlite's async is sync anyway).
- Bind times as `DateTime` (UTC); `Sql` formats them. Read with `SqlTime.Get(reader, ordinal)`.
  `SqlTime.UtcNow()` is truncated to microseconds (storage precision).
- `IN` lists: `... IN (SELECT value FROM json_each(@ids))` with `("@ids", IdList.Json(ids))`.
- Multi-statement writes go through `sql.Transaction(tx => ...)` (BEGIN IMMEDIATE; nestable).
- Connections are long-lived and pooled by `Database`, each with a cache of prepared statements
  keyed by SQL text. So **never interpolate values into SQL** — bind parameters; interpolate only
  constants and column lists (a handful of fixed variants is fine).
- Writes are serialized in-process (`WriteGate`). Hot write paths under concurrency (posting a
  message) go through `WriteBatcher.RunAsync(tx => ...)`: group commit, one savepoint per caller,
  completed after the commit. The work may only use the `Sql` it is handed and must not await.
- Polymorphic columns use Rails class names: `record_type` is `'Message'`, `'User'`, `'Account'`;
  attachment names are `'attachment'`, `'avatar'`, `'logo'`; rich text name is `'body'`.
- Rails callbacks become explicit steps in the service that performs the write (see `Messages.Create`,
  `Users.Create`, `Boosts.Create` for the patterns: touches, search index, open-room grants).

### HTTP / endpoints

- Each workstream exposes `Add<Module>(IServiceCollection)` and `Map<Module>(IEndpointRouteBuilder)`
  (already wired in `Program.cs`). Map routes exactly as `config/routes.rb` defines them; build URLs
  only with `Views/Paths.cs` (add missing helpers in your own static class if needed and report it).
- `CampfireRequestMiddleware` already does what `ApplicationController` did: authentication (session
  cookie, or `{bot_key}` route value for endpoints that `.AllowBots()`), deny bots, CSRF, banned IPs,
  browser check, version headers. Default policy = signed-in humans only. Use
  `.AllowUnauthenticated()`, `.RequireUnauthenticated()`, `.AllowBots()`, `.SkipCsrf()`.
- Handlers get request state by declaring a `Current current` parameter (`current.User`,
  `current.Account`, `current.Platform`, `current.RemoteIp`). Pages get a `PageContext` via
  `PageContext.For(httpContext)` (build it *before* returning the result: it may set the CSRF cookie).
- Forms: read with `var form = await request.ReadFormAsync();` and Rails parameter names
  (`form["message[body]"]`, `form["user_ids[]"]`). Don't use `[FromForm]`/`IFormFile` binding.
  `_method=patch|put|delete` overrides are applied before routing (`UseHttpMethodOverride`), so map
  `MapPatch`/`MapPut`/`MapDelete` as Rails does. Rails merges query and form params — so do you
  where Rails relies on it (e.g. `button_to rooms_directs_path(user_ids: [id])` puts ids in the query).
- Responses (`Http/Responses.cs`): `Respond.Redirect(context, url, notice:, alert:)` (303 after
  non-GET, so fetch never replays DELETE/PATCH), `Respond.TurboStream(html)`,
  `Respond.Head(status)`, `Results.RazorSlice<TProxy, TModel>(model, statusCode)` for pages.
  `request.WantsTurboStream()` ≈ Rails' `format.turbo_stream`.
- Rails' `ActiveRecord::RecordNotFound` → `Results.NotFound()` (the 404 page is served for HTML GETs).
- JSON: each module declares **one** source-generated context of its own (e.g.
  `internal sealed partial class RoomsJsonContext : JsonSerializerContext` with
  `[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]`) and
  registers it from its `Add…` method:
  `services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Add(RoomsJsonContext.Default))`.
  (Splitting `[JsonSerializable]` attributes across `partial` declarations of one context makes the
  generator emit duplicate files.) Names are snake_case, matching jbuilder output.

### Views (RazorSlices)

- Templates live in `Views/<Area>/<Name>.cshtml`; the generated proxy is
  `Campfire.Web.Views.<Area>.<Name>` with `Create(model)`. Return it from a handler
  (`return Views.Rooms.Show.Create(model);`) or wrap with `Results.RazorSlice<...>(model, status)`.
- Page template skeleton:

  ```cshtml
  @inherits RazorSlice<ShowRoomModel>
  @implements IUsesLayout<Campfire.Web.Views.Layouts.Application, LayoutModel>

  <div>...</div>

  @functions {
      public LayoutModel LayoutModel => new(Model.Page, Title: "...", BodyClass: "sidebar");

      protected override Task ExecuteSectionAsync(string name)
      {
          if (name == "nav")
          {
              <div class="flex-item-justify-start">@LinkBackTo(Paths.Root)</div>
          }
          return Task.CompletedTask;
      }
  }
  ```

  Sections the layout renders: `head`, `nav`, `footer`, `sidebar` (Rails' `content_for`).
  If a section needs async partials, make `ExecuteSectionAsync` `async` and `await` them.
- Partials: `@(await RenderPartialAsync<Campfire.Web.Views.Messages.Message, MessageModel>(model))`
  — generic calls **must** be wrapped in `@( )`. Partials receive everything through their model;
  templates never touch `HttpContext` (broadcast rendering has none).
- Helpers are imported statically (`Views/ViewHelpers.cs`): `@ImageTag("check.svg", new Img(Size: 20, AriaHidden: true))`,
  `@AvatarTag(user)`, `@ButtonTo(url, content, method: "delete", cssClass: "...", csrfToken: ..., confirm: "...")`,
  `@LocalDatetimeTag(time, "date")`, `@TurboStreamFrom(StreamNames.Rooms)`, `@TranslationButton("room_name")`,
  `@LinkBackTo(path)`, `@CsrfField(Model.Page.CsrfToken)`, `@MethodField("patch")`, `@Raw(html)`.
  `Paths.*` for URLs, `DomId.For(record, "prefix")` for ids, `QrCodes.Path(url)`.
- Forms: `<form action="..." method="post">@MethodField("patch")@CsrfField(Model.Page.CsrfToken) ...</form>`.
  Inside **cached fragments** (message/boost partials) omit the CSRF field — Turbo sends the
  `X-CSRF-Token` header from the layout's meta tag on every non-GET submission, and a cached token
  would leak one user's token to others.
- Razor gotchas (the Web SDK compiles .cshtml with MVC defaults):
  - `Html`, `Url`, `Json`, `Component`, `ModelExpressionProvider` are injected MVC properties — don't use those names.
  - Directives can't be used as identifiers after `@`: never name a variable `page`, `model`,
    `section`, `layout`, `inject`, `functions`, `using`, `namespace`, `attribute`, `inherits`,
    `implements`, `preservewhitespace`, `typeparam`, `rendermode`. (`@page.X` turns the file into a Razor Page.)
  - Conditional attributes: `class="@x"` omits the attribute when `x` is null; `checked="@flag"` renders `checked="checked"` when true.
  - Text inside code blocks: `<text>...</text>` or `@:text`.
  - Write pre-rendered UTF-8 (cached fragments) with `@{ WriteLiteral(bytes.AsSpan()); }` —
    `@bytes` would HTML-encode them.
- Rendering outside a request (broadcasts, Turbo Stream bodies, fragment caches):
  `await Slices.RenderAsync(Views.Messages.Message.Create(model))` (string) or `Slices.RenderUtf8Async`.
  Both use `MinimalHtmlEncoder` (escapes `& < > " '` only, like ERB).
- HTML output should match the ERB output closely: same elements, ids, classes, data attributes and
  text. Attribute order and whitespace don't matter.

### Turbo and Cable

- Stream names (`Turbo/StreamNames.cs`): `RoomMessages(roomId)` (RoomMessagesChannel only),
  `Rooms` (shared rooms list), `UserRooms(userId)`, `UserReads(userId)`, `UserUnreads(userId)`, `Typing(roomId)`.
- Pages subscribe with `@TurboStreamFrom(name)` or `@TurboStreamFrom(StreamNames.RoomMessages(id), "RoomMessagesChannel")`.
- Publish with `CableServer.BroadcastTurboStream(stream, TurboStream.Append(target, html))` or
  `CableServer.Broadcast(stream, json)` for plain channel messages (`{"roomId":1}`, `{"room_id":1}`).
- DOM ids: `DomId.For(message)` = `message_{client_message_id}`; rooms use `room_{id}` for every
  type (e.g. `list_room_5`, `messages_room_5`, `involvement_room_5`).

### Jobs

`BackgroundQueue.Enqueue(job)` with a `sealed record XJob(...) : IBackgroundJob`; `ExecuteAsync`
receives a scoped `IServiceProvider` (resolve `Database` and open your own `Sql`). Jobs carry ids
and reload state; a missing record means the job has nothing to do.

### Tests

`tests/Campfire.Tests/<Area>/`. Use `IClassFixture<CampfireApp>`; `app.Fixtures` seeds the Rails
fixtures (David, Jason admins; JZ, Kevin members; Bender bot; rooms Pets/HQ open, Watercooler
("All Talk")/Designers closed, three direct rooms). `app.SignedInAs(user)` gives a client with a
session cookie and automatic CSRF header; `app.Anonymous()` for signed-out requests. Port the
relevant Rails tests from `test/` as a guide to what must hold.

### Secrets

Never log or print secrets (SECRET_KEY_BASE, VAPID private key, bot tokens, session tokens).
`LogScrubbing.Scrub` redacts bot keys from paths.
