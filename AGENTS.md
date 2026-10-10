# AGENTS.md — SyncWatch

Server-authoritative "watch together" app. ASP.NET Core 10 + SignalR + EF Core (Postgres/Neon) + Cloudflare R2, with a framework-free vanilla JS frontend served from `wwwroot`. `SyncWatch/README.md` is the author's narrative — it is not a build reference.

## Commands

Run from the repo root unless noted.

- Build: `dotnet build`
- Test all (60 tests, ~7s): `dotnet test`
- One test / class: `dotnet test --filter "FullyQualifiedName~PlaybackServiceTests"` (also `--filter "FullyQualifiedName~SeekAsync"`)
- Run app: `dotnet run --project SyncWatch` → http://localhost:5003 (see `SyncWatch/Properties/launchSettings.json`)
- EF commands need `--project SyncWatch` (or a `cd` into it). Run bare from the wrapper root they fail with `No project was found`, because the root holds two projects and no startup project: `dotnet ef database update --project SyncWatch`. Npgsql is the real provider; the `SqlServer` package reference is vestigial.
- `dotnet ef migrations add X --project SyncWatch` scaffolds against the **current snapshot**. If the model change was already captured by an earlier migration, the new one comes out empty (no `Up`/`Down` body) and applying it only writes a history row — check `Migrations/` before assuming it does something.
- Docker build context is `SyncWatch/` (Dockerfile does `COPY . .` and runs `dotnet publish` on one project): `docker build -t syncwatch SyncWatch`

No CI, no lint/format config, no dotnet-tools manifest. Verification = `dotnet build` + `dotnet test`.

Note the layout: the working directory here is a wrapper. `SyncWatch/` is itself the real git repo (it has its own `.git` and the project's history), and only `SyncWatch.Tests/`, `.gitignore` and `SyncWatch.slnx` are tracked by the outer repo, which records `SyncWatch` as a gitlink. App-side `git status`/`git log`/`git commit` must be run inside `SyncWatch/`.

## Required secrets (app will not start without them)

Set via user-secrets (id `ce46c0c4-…`, declared in `SyncWatch/SyncWatch.csproj`):

```
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<postgres>"
dotnet user-secrets set "R2:AccessKey" / "R2:SecretKey" / "R2:Endpoint" / "R2:BucketName"
```

`appsettings.json` holds the tunable limits (`StorageSettings:MaxRoomBytes` 500 MB/room, `UploadSettings:MaxFileBytes`, `UploadSettings:TicketLifetimeMinutes`, `ParticipantsSettings:MaxParticipants` = 2). Services read these with `configuration.GetValue<T>(...)` with a fallback default in code — if you add a limit, follow that pattern (default in code, not a required key).

## Architecture that isn't obvious from filenames

Layering is `Models` → `Dtos` → `Services` (+`Services/Exception`) → `Controllers`, with `Hubs/PlaybackHub` as the other entrypoint. Controllers and the hub are thin; all logic lives in services. Registration is manual in `Program.cs` (`IMovieService`/`IRoomService`/`IPlaybackService`/`IChatService` scoped, `PresenceTracker` singleton, `RoomExpirationService`/`PlaybackSyncService` as hosted services) — there is no DI by convention, so a new service must be added there or it won't resolve.

- **The server is the only source of truth.** Clients never negotiate; they send commands and listen. Every mutation of `PlaybackState` must `Version += 1` and set `UpdatedAt = DateTime.UtcNow`. `PlaybackSyncService` re-broadcasts an extrapolated position every 5s for rooms in `Playing`, so client-side "time" is always derived, never authoritative.
- **A participant dropping pauses the room.** `PlaybackHub.OnDisconnectedAsync` marks them Offline, broadcasts `ParticipantsUpdated` *with* a payload (`{ displayName, status }`), then calls `PlaybackService.PauseAtCurrentPositionAsync` and re-broadcasts `PlaybackUpdated`. `MarkPresentAsync` does the same on the way back in. The gate on both is `SetParticipantStatusAsync` returning the participant's status *before* the change (null = unknown token): only a real `Offline`→`Online` transition is announced, because `Join` runs on every reconnect and anything looser tells the room someone is back each time they refresh a page they never left. Don't relax that comparison to a null/bool check. That pause is also what stops `PlaybackSyncService` from extrapolating forever into a room nobody is watching. Play is a normal command from there, and the reconnecting client gets the current state from `Join`/`Reconnect`, so it picks up wherever the room got to.
- **SignalR groups are named `roomId.ToString()`**, not room codes. Hub broadcasts use `Clients.Group(roomId.ToString())` and always send the event name `PlaybackUpdated`. `Join`/`Reconnect` send the current state only to `Clients.Caller`.
- **Auth is a bare participant token** passed as an HTTP header (`token` for participant ops, `hostToken` for host-only). No ASP.NET auth middleware despite `UseAuthorization()` being in the pipeline.
- **Error convention:** one small exception class per file in `SyncWatch/Services/Exception/`, all with default messages. Services throw; controllers `catch` the specific type and map to a status code with a bare-string body. The hub catches them and rethrows as `HubException` (SignalR cannot serialize custom exceptions).
- **Enums go over the wire as strings** — `JsonStringEnumConverter` is registered twice in `Program.cs` (MVC and the SignalR JSON protocol). Both are required; don't remove one.
- **Uploads are three steps:** presigned PUT ticket → browser PUTs bytes straight to R2 → `upload-complete` verifies. On completion the server re-checks object key ownership (must be `{roomId}/{guid}.mp4`), size vs. what R2 stored, and sniffs the first 64 bytes for an MP4 `ftyp` box, then deletes the object on any failure. The legacy `POST /api/Movie/upload/{roomId}` server-proxy path also exists; the frontend does not use it.
- **Chat is persisted, not just broadcast.** `ChatMessage` rows cascade-delete with the room (close-room and `RoomExpirationService` both rely on it). `ChatMessage.Id` is a **v7** GUID so history orders by `Id` — `SentAt` is far too coarse to break same-tick ties. History is trimmed to `ChatService.HistoryLimit` (200) on write, and the Hub replays it to `Clients.Caller` on `Join`/`Reconnect` as `ChatHistory`.
- **Any migration needs applying for real:** `dotnet ef database update --project SyncWatch` against the Neon DB. Generating one (`dotnet ef migrations add`) only needs the design-time context, so it can succeed while the live database is still on the old schema.
- `SyncWatch/Uploads/` and the `File.Exists(movie.StorageLocation)` branch in `RoomService.BackfillMissingDurationsAsync` are leftovers from when files lived on local disk. `StorageLocation` is now an R2 key, so that path is effectively dead for new uploads — don't build on it.

## Frontend

`wwwroot/js/`: `api.js` (fetch wrappers, error parsing, presigned PUT, localStorage session), `room.js` (~30 KB, the player + SignalR client + drift correction + chat), `create.js`, `myrooms.html` flows. Same-origin, no build step, SignalR client from CDN. `api.js` errors are read with `res.text()` first — the API returns plain-string bodies and calling `res.json()` throws.

## Tests

`SyncWatch.Tests`, xUnit + Moq + EF InMemory. `TestHelper.CreateDbContext()` returns a fresh uniquely-named InMemory `DatabaseContext` per test. `IAmazonS3` is always mocked (see `MovieServiceTests.CreateService`); MP4 fixtures are hand-built byte arrays (`CreateMp4Header`, `Mp4DurationReaderTests.BuildBox`), never real media files. Test naming convention is `Method_Condition_ExpectedResult`.

`PlaybackHubTests` drives the hub directly — `Hub.Clients`, `Hub.Groups` and `Hub.Context` are all public settable, so Moq substitutes them and no `TestServer` is needed. Two .NET 10 API changes to know: the group manager is `IGroupManager` (was `IHubGroupManager`) and `Clients.Caller` is `ISingleClientProxy` (was `IClientProxy`). Hub `SendAsync` lands in Moq's `Invocations` as `SendCoreAsync`. `PlaybackSyncService` (background) remains untested.
