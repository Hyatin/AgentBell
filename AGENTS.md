# AGENTS.md — AgentBell

## Mission

Build AgentBell for Codex: a local-first, low-latency notification bridge that sends an Android system notification when Codex emits `agent-turn-complete`.

The first release supports only:

- Codex
- Windows 10/11 x64
- Android
- Same-LAN communication
- User-level Codex `Stop` Hook
- Legacy Codex `notify` JSON input compatibility in the M0 executable only
- Android heads-up notifications

Read `docs/DEVELOPMENT_SPEC.md` before changing code.

## Scope guardrails

Do not implement or introduce:

- Claude Code
- Other coding agents
- iOS
- Cloud services
- User accounts
- Firebase
- Public internet relay
- OCR or screen capture
- Terminal log polling
- Real progress estimation
- Remote control of Codex
- A database
- A complex dashboard
- A formal Codex hooks plugin before the Stop-Hook-based MVP works

Do not expand scope unless the user explicitly changes the specification.

## Engineering priorities

In order:

1. Reliability
2. Low latency
3. Simple deployment
4. Privacy
5. Small dependency surface
6. Maintainability
7. Visual polish

Prefer direct, boring implementations over abstraction-heavy designs.

## Technology constraints

Windows:

- C#
- .NET 10 LTS
- ASP.NET Core Minimal API
- WinForms tray integration or an equivalently lightweight native option
- `System.Text.Json`
- xUnit
- Self-contained `win-x64` publish

Android:

- Kotlin
- Native Android
- Jetpack Compose for minimal UI
- One mature WebSocket client
- Foreground service for persistent connection
- Android Keystore for pairing credentials

Installer:

- Inno Setup
- Implement only after the end-to-end path works

## Security rules

- Bind Codex ingestion only to `127.0.0.1`.
- Require a random token on every LAN endpoint.
- Never send `input-messages` to the phone.
- Never log prompts, full assistant messages, raw Codex payloads, full paths, or pairing tokens.
- Do not overwrite existing Codex configuration or Hook definitions.
- Do not modify, replace, remove, or chain an existing Codex `notify` command.
- Back up configuration before modifying it.
- Treat all external JSON and WebSocket data as untrusted.
- Do not claim HTTP/WS LAN traffic is end-to-end encrypted.

## Codex payload rules

The primary integration is the user-level `Stop` command Hook. It invokes
`AgentBell.Hook.exe --codex-stop-hook` and supplies one JSON object on stdin.
Known fields are `session_id`, `turn_id`, `cwd`, `hook_event_name`,
`last_assistant_message`, `stop_hook_active`, `permission_mode`, and `model`.
Only `hook_event_name == "Stop"` is accepted.

The executable retains the legacy `notify` compatibility mode, where Codex
supplies one `agent-turn-complete` JSON string as a command-line argument.
Every external field must be treated as optional, and unknown fields must be
ignored. Never read `transcript_path` or log raw payload content.

## Workflow

Work on one milestone at a time:

- M0: Codex event probe
- M1: Windows local bridge
- M2: LAN WebSocket
- M3: Android MVP
- M4: installer and one-click configuration
- M5: release hardening

Do not begin the next milestone automatically.

Before coding:

1. State the current milestone.
2. State the exact files you plan to modify.
3. State what remains out of scope.

After coding:

1. Run formatting.
2. Run build.
3. Run tests.
4. Report exact results.
5. Report known risks.
6. Stop and wait for the next instruction.

## Code quality

- Nullable reference types enabled.
- Warnings treated seriously.
- Public contracts documented.
- Use cancellation tokens for I/O.
- Set explicit timeouts.
- Avoid static mutable global state.
- Use dependency injection only where it improves testing.
- Keep the Hook executable small and fast.
- Do not block Codex on unavailable desktop or mobile components.
- Use atomic file replacement for local JSON state.
- Use stable error codes rather than exposing exception text.
- Test Unicode and emoji truncation correctly.

## Definition of done

A milestone is not done unless:

- It builds from a clean checkout.
- Tests pass.
- Manual test steps are documented.
- No secret or private content is written to logs.
- The implementation stays inside the milestone scope.
