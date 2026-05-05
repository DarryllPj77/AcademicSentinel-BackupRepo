# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Solution layout

`AcademicSentinel.slnx` aggregates three projects:

- **AcademicSentinel.Server** (`net10.0`, ASP.NET Core Web API) — backend: REST controllers, EF Core, JWT auth, SignalR hub, image storage, SMTP-based password reset.
- **AcademicSentinel.Client** (`net10.0-windows`, WPF + MaterialDesign) — the *active* desktop app. Contains BOTH instructor (`Views/IMC`, `Services/...`) and student (`Views/SAC`, `Services/SAC`) flows in one binary.
- **SecureAssessmentClient** (`net9.0-windows7.0`, WPF) — legacy/standalone student detection client kept for reference. It explicitly excludes `AcademicSentinel.Server\**` from compile (the nested `AcademicSentinel.Server/` folder there is a documentation snapshot, not built).

Different TargetFrameworks are intentional. `SecureAssessmentClient/Directory.Build.props` centralizes package versions for net9 vs net10 — it only applies under that directory, so the active Client/Server projects manage their own `<PackageReference>` entries.

## Common commands

Run from the project's own directory unless noted.

```
# Server
cd AcademicSentinel.Server
dotnet restore
dotnet ef database update                # apply migrations to Postgres
dotnet ef migrations add <Name>          # create new migration
dotnet run                               # https://localhost:7123 + Swagger in Development

# Active WPF client (IMC + SAC together)
cd AcademicSentinel.Client
dotnet run

# Legacy SAC
cd SecureAssessmentClient
dotnet run

# Whole solution
dotnet build AcademicSentinel.slnx
```

There is no test project in this solution. `SecureAssessmentClient/Testing/` contains scenario runners (`DetectionTestConsole.cs`, `TestProgram.cs`, `TestScenarioRunner.cs`) compiled into that exe, not xUnit/NUnit tests.

## Database

PostgreSQL via `Npgsql.EntityFrameworkCore.PostgreSQL` — recently migrated from SQLite (commit `af0527e`). Connection string lives in `AcademicSentinel.Server/appsettings.json` under `ConnectionStrings:DefaultConnection` and currently points at `localhost:5432/academicsentinel`. Any leftover `*.db*` files in the server directory are residue from the SQLite era and are not used.

`AppDbContext` (`AcademicSentinel.Server/Data/AppDbContext.cs`) defines 10 DbSets: `Users`, `Rooms`, `RoomDetectionSettings`, `RoomEnrollments`, `SessionAssignments`, `SessionParticipants`, `ExamSessions`, `MonitoringEvents`, `ViolationLogs`, `RiskSummaries`. There is no `OnModelCreating` — relationships are inferred by EF conventions. When changing models, generate a new migration; never edit existing migration files in `Migrations/`.

## Auth + SignalR wiring (non-obvious)

JWT Bearer is configured in `Program.cs`. The hub at `/monitoringHub` requires the JWT in the **query string** as `?access_token=...` rather than the `Authorization` header — this is set up in the `JwtBearerEvents.OnMessageReceived` block. SignalR clients (both `AcademicSentinel.Client` and `SecureAssessmentClient`) must pass the token via `HubConnectionBuilder.WithUrl(..., options => options.AccessTokenProvider = ...)`.

JWT lifetime is 3 hours (`AuthController.Login`). Tokens carry `ClaimTypes.NameIdentifier` (user id), `ClaimTypes.Role` (`Instructor` / `Student`), and `ClaimTypes.Name` (email). Hub methods read role/id from these claims and enforce that students can only act on their own `studentId`.

`MonitoringHub.MonitoringStates` is a `static ConcurrentDictionary<int, bool>` — monitoring on/off state is **process-local in-memory** and resets on server restart. Multi-instance deployment would need a backplane.

Hub broadcast contract (group key = `roomId.ToString()`):
- `MonitoringStateChanged`, `MonitoringPaused`, `MonitoringResumed`, `MonitoringCountdownStarted`
- `StudentJoinedOrReconnected`, `StudentDisconnected`, `StudentConnectionLost`, `StudentLeftSession`
- `ViolationDetected`, `ReceiveHardwareStateUpdate`
- `LeaveRequested`, `LeaveGranted`, `LeaveApprovalUpdated`
- `SessionEnded`, `SessionInterrupted`, `JoinFailed`

`OnDisconnectedAsync` has special handling: if the disconnecting user is the room's *instructor*, the room is force-ended (`Status = "Ended"`) and the active `ExamSession` is closed. Student disconnects flip `SessionParticipant.ConnectionStatus` to `Disconnected` unless it was already `Completed` (clean leave via `NotifyStudentLeftSafely`).

Room status state machine: `Pending → Countdown → Active → Ended`. `RoomDetectionSettings` is locked once a room leaves `Pending`.

## Client → Server coupling

`AcademicSentinel.Client/Constants/ApiEndpoints.cs` hardcodes `BaseUrl = "https://localhost:7123"` and concatenates it into endpoint constants. **Changing the server port or host requires editing this file** — there is no config file fallback on the client.

`AcademicSentinel.Client/Services/SessionManager.cs` is a `static` class holding the JWT and `CurrentUser` in-memory only. State does not persist across app restarts; logging back in is required after every relaunch. Most service classes assume `SessionManager.JwtToken` is set before they are used.

The legacy `SecureAssessmentClient` reads its server URL from `Config/AppSettings.json` via `Config/ServerConfig.cs` and uses `Utilities/TokenManager.cs` for JWT — different conventions from the active client; do not copy patterns between them blindly.

## File-storage and email side-effects

`ImageStorageService` writes to `AcademicSentinel.Server/wwwroot/images/` and the server serves them via `UseStaticFiles()`. Profile and room images are referenced by URL in DB rows; deleting users/rooms should also clean up the file (check the controller logic before assuming).

`OutlookEmailSender` is registered as `IEmailSender` and is used by the forgot-password flow in `AuthController`. SMTP credentials in `appsettings.json:Email` are blank by default — that flow will fail silently/throw until they are configured (or via user secrets).

## Conventions worth knowing

- C# `Nullable` is **enabled** on the server and SAC, **disabled** on the active `AcademicSentinel.Client` (`<Nullable>disable</Nullable>` in csproj). Don't add `?` annotations expecting them to be enforced in the client project.
- Server uses file-scoped namespaces (`namespace AcademicSentinel.Server.Xxx;`).
- The repo is on Windows; paths in scripts and configs use backslashes, and PowerShell is the default shell. WPF projects only build on Windows.
- Branch convention from recent history: `feature/...` and `fix/...` prefixes; commit subjects use `type: description` (e.g., `chore:`, `fix/style:`).
