# Academic Sentinel System Documentation

## 1. Overview
Academic Sentinel is a two-project .NET 10 WPF + ASP.NET Core system for monitored academic assessments. It consists of:

- **AcademicSentinel.Client** — the desktop client built with WPF.
- **AcademicSentinel.Server** — the ASP.NET Core API, SignalR hub, background services, and PostgreSQL data layer.

The system supports two main roles:

- **Student** — uses the Secure Assessment Client (SAC).
- **Instructor** — uses the Instructor Monitoring Console (IMC).

The core purpose of the system is to manage reusable course rooms, run monitored exam sessions, detect suspicious behavior, record violations, and provide real-time instructor oversight.

---

## 2. High-Level Architecture

### Client side
The WPF client provides:

- account registration and login
- profile management
- image upload for avatars
- student course browsing and secure assessment entry
- instructor dashboard and room management
- live monitoring and post-session review
- trash/archive management for past sessions
- help and consent windows

### Server side
The ASP.NET Core server provides:

- JWT authentication and logout
- registration, verification, and password reset endpoints
- room and session lifecycle management
- student enrollment by code or instructor assignment
- real-time monitoring with SignalR
- violation persistence and reporting
- image upload/download/delete endpoints
- server time endpoint for clock-integrity checking
- background cleanup and disconnect sweeps

### Persistence
The server uses **PostgreSQL** through Entity Framework Core and applies migrations at startup.

### Real-time communication
The server hosts a SignalR hub at:

- `/monitoringHub`

This is used for live student/instructor monitoring, session state updates, heartbeats, and alert broadcasts.

---

## 3. Solution Structure

### Projects
- `AcademicSentinel.Client\AcademicSentinel.Client.csproj`
- `AcademicSentinel.Server\AcademicSentinel.Server.csproj`

### Client notable packages / references
- `MaterialDesignThemes`
- `Microsoft.AspNetCore.SignalR.Client`
- `QuestPDF`
- `log4net`
- `System.Management`
- `UIAutomationClient`
- `UIAutomationTypes`

### Server notable technologies
- ASP.NET Core Web API
- SignalR
- Entity Framework Core
- Npgsql/PostgreSQL
- JWT Bearer authentication
- Swagger/OpenAPI in development
- Cloudinary or local disk image storage

---

## 4. Client Application Documentation

## 4.1 Startup Flow
When the client starts, it shows the consent window first. If consent is accepted, it opens the login window.

Startup sequence:

1. `ConsentWindow`
2. `LoginWindow`
3. role-specific dashboard after successful login

### App mode
`AcademicSentinel.Client.Constants.AppMode.Role` controls whether the client is in:

- `Teacher`
- `Student`
- `All`

`All` means both login roles are visible.

## 4.2 Shared Client Windows

### `ConsentWindow`
A mandatory first-run consent screen. The app only proceeds when the user agrees.

### `LoginWindow`
Handles:

- email/password sign-in
- role-based routing
- wrong-role protection
- login error handling
- navigation to registration
- prototype password-reset notice

If login succeeds:

- instructors go to `TeacherDashboard`
- students go to `StudentDashboard`

### `RegisterWindow`
Handles:

- full name, email, password, confirm password
- role selection
- email-domain validation
- account creation via the server

Current client behavior reflects the prototype setup:

- allowed domains: `@fit.edu.ph`, `@feutech.edu.ph`, and `@gmail.com`
- after successful registration, the app returns to login
- the verification flow is present in the server API, but the current client path does not continue into a code screen because the prototype auto-verifies accounts

### `EmailVerificationWindow`
Present in the client project for the verification flow.

### `ForgetPasswordWindow`
Present in the client project for the password reset flow.

### `ForgetPasswordWindowCodeVerification`
Used for reset-code verification.

### `ForgetPasswordWindowChangePass`
Used for setting a new password after code validation.

### `HelpGuideWindow`
Shows teacher or student help content depending on the selected mode.

## 4.3 Student Side: Secure Assessment Client (SAC)

### `StudentDashboard`
This is the student landing page after login. It supports:

- profile details
- profile avatar display/upload
- course/room listing
- search/filtering of enrolled rooms
- room waiting-room view
- joining a session when the room becomes active
- navigation to student help
- logout

Important behaviors:

- it periodically syncs course and room state from the server
- it can show whether a room is joinable or not
- it opens the secure exam window only when the room is active and the session is valid

### `SecureAssessmentClientWindow`
This is the active exam/monitoring window used while the student is in a live room session. It includes:

- SignalR connectivity to the room
- heartbeats to the server
- detector startup and polling
- behavior report collection
- compact soft-lock mode
- countdown/active monitoring states
- session completion / leave approval flow
- student disconnect/reconnect handling
- instructor disconnect/reconnect notifications
- detection module enablement based on room settings

It is the main SAC runtime and coordinates the secure exam experience.

### `AddCourseCodeDialog`
Used by students to join a room using an enrollment code.

## 4.4 Instructor Side: Instructor Monitoring Console (IMC)

### `TeacherDashboard`
Instructor landing page after login. It supports:

- profile and avatar display
- room tiles/cards
- room browsing and search-like navigation
- connectivity banner behavior
- navigation into room detail and live monitoring
- logout
- instructor help guide

### `MainWindow`
Basic WPF shell entry present in the client project. It currently contains only the standard window initialization.

### `Teacherdashboard`
Main dashboard code-behind for instructor course/room management.

### `RoomDetailWindow`
Room-specific management screen. Based on the system features and server endpoints, it supports room-level operations such as:

- editing room information
- uploading room images
- managing students
- starting sessions
- viewing past sessions
- viewing trash
- opening live monitoring
- configuring detection settings

### `CreateRoomWindow`
Room creation window used to define a new reusable room.

### `EditCourseDialog`
Edits room/course details.

### `CreateCourseDialog`
Room/course creation dialog.

### `AddStudentDialog`
Adds a student to a room.

### `CreateSessionSetupWindow`
Used to configure a session before starting live monitoring.

### `LiveSessionMonitoringWindow`
The real-time IMC monitoring dashboard. It supports:

- live participant list
- activity and risk views
- alert feed
- reconnect/disconnect state
- join approval handling
- hand-raise handling
- log replay after reconnect
- session timers and countdowns
- teacher heartbeat to the server
- end-session behavior

### `StudentListWindow`
Displays student lists for a room.

### `SessionHistoryListWindow`
Lists historical sessions for a room.

### `SessionArchiveDetailWindow`
Shows detailed information about a past session archive.

### `StudentLogsPreviewDialog`
Preview dialog for a student's raw monitoring logs.

### `StudentViolationSummaryDialog`
Summary dialog for a student's violations and risk classification.

### `SessionTrashWindow`
Shows trashed session archives and supports:

- bulk restore
- bulk permanent delete
- refresh
- select all / clear selection

### Monitoring help windows
- `InstructorMonitoringHelpWindow`
- `MonitoringHelpInfoWindow`
- `StudentDetailsHelpWindow`

These provide contextual help for the monitoring workflow.

### Additional IMC support windows and dialogs
- `StudentListWindow` — room student listing and management.
- `CreateSessionSetupWindow` — session configuration before start.
- `AddStudentDialog` — manual room enrollment by instructor.
- `CreateCourseDialog` — alternate room creation flow.
- `EditCourseDialog` — edit subject/course details.
- `CreateRoomWindow` — alternate room creation flow.

## 4.5 Student-side and instructor-side UI assets
The client includes:

- custom icons (`FourCUDAIcon16/32/48/64/256.ico`)
- splash screen image
- bundled fonts under `Resources\Noto` and `Resources\Roboto`
- a `Shared.zip` resource bundle

---

## 5. Server Application Documentation

## 5.1 Startup and Hosting Behavior
The server is configured to run correctly both locally and in container/PaaS environments.

It supports:

- `PORT` environment variable binding
- `DATABASE_URL` PostgreSQL connection parsing
- JWT configuration via environment variables or appsettings
- CORS allowlist from `CORS_ALLOWED_ORIGINS`
- Cloudinary image storage via `CLOUDINARY_URL`
- automatic EF Core migration on startup
- Swagger in development
- `/healthz` health check

### Important startup values
- JWT signing key is required
- PostgreSQL is the database backend
- image uploads are capped at 10 MB request size
- archive retention is validated at startup and must be 15 or 30 days

## 5.2 Authentication
The server uses JWT Bearer authentication with role-based access control.

### Auth endpoints
- `POST /api/auth/login`
- `POST /api/auth/logout`
- `POST /api/auth/register`
- `POST /api/auth/verify-email-code`
- `POST /api/auth/resend-verification-code`
- `POST /api/auth/forgot-password`
- `POST /api/auth/verify-reset-code`
- `POST /api/auth/reset-password`
- `GET /api/auth/profile`
- `PUT /api/auth/profile`
- `POST /api/auth/change-password`

### Authentication features
- role-based login for students and instructors
- 8-hour JWT lifetime
- single-device login lock
- device-bound ghost-lock recovery
- profile fetching and updates
- password change
- email verification flow
- password reset flow

### Current prototype behavior
The codebase contains the verification and reset endpoints, but the active client flow is still partially prototype-oriented because outbound email delivery is not fully configured in the current setup.

## 5.3 Room Management
Room management is the core server feature set.

### Room endpoints
- `POST /api/rooms`
- `GET /api/rooms/{id}`
- `PUT /api/rooms/{id}`
- `DELETE /api/rooms/{id}`
- `GET /api/rooms/instructor`
- `GET /api/rooms/student`
- `GET /api/rooms/my`
- `GET /api/rooms/{roomId}/status`
- `GET /api/rooms/{roomId}/history`
- `GET /api/rooms/{roomId}/trash`
- `GET /api/rooms/{roomId}/settings`
- `POST /api/rooms/{roomId}/settings`
- `PUT /api/rooms/{roomId}/settings`
- `GET /api/rooms/{roomId}/participants`
- `DELETE /api/rooms/{roomId}/unenroll/{studentId}`
- `POST /api/rooms/{roomId}/sessions/remove/{studentId}`
- `POST /api/rooms/{roomId}/request-join`
- `POST /api/rooms/{roomId}/join`
- `POST /api/rooms/enroll-code`
- `POST /api/rooms/enroll`
- `POST /api/rooms/{sessionId}/generate-code`
- `POST /api/rooms/{roomId}/enroll-email`
- `POST /api/rooms/{roomId}/start-session`
- `POST /api/rooms/{roomId}/start`
- `PUT /api/rooms/sessions/{sessionId}/end`
- `POST /api/rooms/{sessionId}/end`
- `POST /api/rooms/{roomId}/force-reset`
- `DELETE /api/rooms/sessions/{sessionId}`
- `POST /api/rooms/sessions/bulk-delete`
- `POST /api/rooms/sessions/{sessionId}/restore`
- `POST /api/rooms/sessions/bulk-restore`
- `POST /api/rooms/sessions/bulk-purge`

### Room behavior
Rooms are reusable, subject-based containers. They are not one-time sessions.

Rooms support:

- instructor ownership
- room status lifecycle
- enrollment code generation
- manual student enrollment
- student code enrollment
- detection setting configuration
- room image/logo uploads
- session history and trash
- participant tracking
- live session monitoring state

## 5.4 Session Management
Each room can have multiple exam sessions over time.

### Exam session properties
- session number
- room link
- start time
- end time
- status
- exam type
- soft-delete timestamp

### Session lifecycle
- create session
- start monitoring after countdown
- mark completed
- soft-delete into trash
- restore from trash
- permanently purge when retention expires

## 5.5 Enrollment and Participation
The server tracks:

- room enrollment source (`Manual` or `Code`)
- join timestamps
- disconnected timestamps
- current connection status
- join approval state
- student activity state
- final risk level

Student access can happen via:

- manual instructor assignment
- enrollment code

## 5.6 Detection Configuration
Each room can store detection settings.

### Configurable detection options
- clipboard monitoring
- process detection
- idle detection
- focus detection
- virtualization check
- strict mode
- idle threshold
- LMS exam URL
- allowed apps CSV

### Important rule
Detection settings can only be modified when the room is not actively monitoring.

## 5.7 Violation Tracking and Reporting
The server stores monitoring and violation data and exposes reporting endpoints.

### Report endpoints
- `GET /api/reports/room/{sessionId}`
- `GET /api/reports/student/{sessionId}/{studentId}`
- `GET /api/reports/rooms/{roomId}/sessions`
- `GET /api/reports/sessions/{sessionId}/students`

### Violation endpoints
- `POST /api/violations`
- `GET /api/violations/room/{roomId}`

### Risk classification
The current scoring model uses:

- `S1` = 10 points
- `S2` = 20 points
- `S3` = 50 points

Risk levels are reported as:

- `Safe`
- `Suspicious`
- `Possible Dishonesty`

## 5.8 Images
The server supports profile and room image storage.

### Image endpoints
- `POST /api/images/profile`
- `GET /api/images/profile/{userId}`
- `DELETE /api/images/profile`
- `POST /api/images/room/{roomId}`
- `GET /api/images/room/{roomId}`
- `DELETE /api/images/room/{roomId}`
- `GET /api/images/profile`
- `GET /api/images/room/{roomId}/details`

### Storage modes
- **Local disk** in development
- **Cloudinary** in production when `CLOUDINARY_URL` is configured

### Supported upload rules
- PNG, JPG, JPEG only
- 5 MB max for stored images

## 5.9 Server Time
Endpoint:

- `GET /api/server/time`

This returns UTC time and ISO-8601 time for client-side integrity checks.

## 5.10 SignalR Hub
The hub is located at `/monitoringHub`.

### Main responsibilities
- student heartbeats
- teacher heartbeats
- room join
- monitoring state updates
- session countdown / start / end broadcasts
- teacher disconnect/reconnect handling
- student disconnect/reconnect handling
- raised-hand support
- violation alert broadcasts

### Important hub state
- active student connection map
- active instructor connection map
- room disconnected instructor map
- raised-hand map

### Background interaction
The hub is coordinated with background services so dropped connections and stale locks are detected even when SignalR transport callbacks are delayed.

---

## 6. Background Services

### `DisconnectSweeperService`
Scans heartbeat state every 2 seconds and delegates stale student/instructor disconnects to `DisconnectService`.

### `DisconnectService`
The canonical handler for disconnect processing. It handles:

- participant row updates
- monitoring events
- broadcasts
- instructor disconnect flags
- idempotency and deduplication

### `GhostLockSweeperService`
Releases stale single-device login locks when a user has been logged in too long without activity and is not currently alive on the hub.

### `ArchiveCleanupService`
Hard-deletes trashed exam sessions when their retention window expires.

---

## 7. Data Model Documentation

## 7.1 Core Tables

### `Users`
Stores user account and profile data.

Important fields include:
- `Id`
- `Email`
- `FullName`
- `PasswordHash`
- `Role`
- `CreatedAt`
- profile image fields
- password reset fields
- email verification fields
- single-device lock fields

### `Rooms`
Stores reusable room definitions.

Important fields include:
- `Id`
- `SubjectName`
- `InstructorId`
- `Status`
- `EnrollmentCode`
- `CreatedAt`
- room image fields
- `IsMonitoringActive`

### `ExamSessions`
Stores individual exam runs for a room.

Important fields include:
- `Id`
- `RoomId`
- `SessionNumber`
- `StartTime`
- `EndTime`
- `Status`
- `ExamType`
- `DeletedAt`

### `RoomDetectionSettings`
Stores per-room detector configuration.

Important fields include:
- `RoomId`
- clipboard/process/idle/focus/virtualization toggles
- idle threshold
- strict mode
- LMS exam URL
- allowed apps CSV
- `CreatedAt`

### `RoomEnrollments`
Tracks who is enrolled in which room and how they were enrolled.

### `SessionAssignments`
Tracks manual instructor assignment of students.

### `SessionParticipants`
Tracks active and historical participation in a session.

Important fields include:
- `RoomId`
- `StudentId`
- `JoinedAt`
- `DisconnectedAt`
- `ConnectionStatus`
- `FinalRiskLevel`
- `JoinApprovalStatus`
- `IsCurrentlyActive`

### `MonitoringEvents`
Stores the live stream of behavioral events and state events.

### `ViolationLogs`
Stores persistent violation entries received from the SAC and used for alerts.

### `RiskSummaries`
Stores computed end-of-exam summary data.

## 7.2 Additional DTOs
The server and client exchange DTOs for:

- login/register responses
- password reset
- verification codes
- room setup
- participant data
- image upload results
- room image/profile image details
- historical session data
- trash operations
- monitoring events

---

## 8. Secure Assessment Client Features

The SAC is the student-side monitoring runtime. It supports:

- login and profile management
- course/room discovery based on enrollment
- room waiting-room state
- active exam monitoring window
- heartbeats to the server
- focus/clipboard/process/idle/virtualization detection
- browser URL anchoring through UI automation
- soft-lock completion window
- reconnect and disconnect handling
- violation reporting
- session completion submission

### Detection runtime modules
The client project includes services for:

- `BehavioralMonitoringService`
- `DecisionEngineService`
- `EnvironmentIntegrityService`
- `HardwareSoftwareArtifactService`
- `KeyboardHookService`
- `MouseHookService`
- `BrowserUrlReader`
- `UrlAnchorValidator`

### Detection settings behavior
The SAC requests room-specific settings and only enables the modules the instructor has allowed.

---

## 9. Instructor Monitoring Console Features

The IMC supports:

- creating and editing rooms
- assigning students
- generating enrollment codes
- uploading room logos/images
- starting sessions with countdowns
- live monitoring of active students
- approving join/rejoin requests
- viewing participant connection quality
- recording hand raises
- viewing violation alerts and log feed
- reviewing archived sessions
- moving sessions to trash
- restoring or purging trashed sessions

### Live session monitoring behaviors
The live monitoring screen is designed to show:

- current active students
- disconnected students
- students waiting for approval
- students who completed the session
- real-time activity logs
- participant counts
- session timers and countdowns

---

## 10. API and Workflow Summary

### Student registration/login workflow
1. Student registers.
2. Server validates domain and role.
3. Server creates or updates the account.
4. Student logs in.
5. JWT is issued.
6. Student lands on the dashboard.

### Instructor room workflow
1. Instructor logs in.
2. Instructor creates or opens a room.
3. Instructor configures detection settings.
4. Instructor enrolls students manually or by code.
5. Instructor generates a session and starts monitoring.
6. Students join through the dashboard.
7. Live monitoring proceeds through SignalR.
8. Instructor ends the session.
9. Session is saved to history and may be trashed later.

### Student monitoring workflow
1. Student opens the dashboard.
2. Student selects an assigned room.
3. If the room is active, the student joins.
4. SAC begins heartbeats and detection.
5. Events are transmitted to the server.
6. If the student disconnects or finishes, the session state updates accordingly.

---

## 11. Configuration and Deployment

## 11.1 Server configuration
Important environment variables and settings include:

- `DATABASE_URL`
- `JWT_KEY`
- `JWT_ISSUER`
- `JWT_AUDIENCE`
- `CORS_ALLOWED_ORIGINS`
- `CLOUDINARY_URL`
- `Archive__RetentionDays`

### appsettings data
The local development configuration contains:

- PostgreSQL connection string
- JWT issuer and audience
- email SMTP settings
- archive retention days
- logging levels

## 11.2 Client configuration
The client uses `AcademicSentinel.Client.Constants.ApiEndpoints.BaseUrl` to point at the server.

The current code shows the local test URL as:

- `https://localhost:7123`

## 11.3 Deployment notes
The server is written to support container/PaaS hosting and reverse proxies. It handles forwarded headers, health checks, and HTTPS termination at the edge.

---

## 12. Resource and Asset Inventory

### Client assets
- splash screen image
- application icons
- Noto fonts
- Roboto fonts
- MaterialDesign theming resources

### Server web assets
- `wwwroot/images/profiles`
- `wwwroot/images/rooms`
- static files served through ASP.NET Core

---

## 13. Current System Characteristics

The current codebase reflects a system with:

- reusable room-based assessments
- real-time monitoring
- student and instructor dashboards
- session history and trash management
- role-based access control
- image upload and storage
- configurable detector settings
- heartbeat-driven disconnect detection
- background cleanup for stale data
- PostgreSQL persistence
- JWT authentication
- WPF desktop client

---

## 14. Notes on Current Build State
Some features are present in the codebase and API surface but are handled in a prototype-oriented way in the current client flow, especially around email verification and password recovery. The documentation above reflects the actual project structure and current implementation, not a hypothetical future version.
