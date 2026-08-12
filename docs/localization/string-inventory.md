# User-visible string inventory

The inventory was taken before migration from the 0.5 source tree. Logs, protocol/API strings, test data, filenames, stable error codes, and technical exception text are explicitly not UI resources.

| Original source area | Original examples | User-visible | Resource destination | English / 简体中文 | Retained raw text |
|---|---|---:|---|---|---|
| `MainForm.cs` status group | `状态`, `AgentBell 版本`, `本地 Hook 服务`, `LAN 服务` | Yes | `Main_*`, `Status_*` | Status / 状态; AgentBell version / AgentBell 版本 | AgentBell, Hook, LAN |
| `MainForm.cs` status rows | address, clients, last event, sequence, startup, Codex integration, APK | Yes | `Status_LanEndpoint` through `Status_AndroidApk` | Complete matching resources | protocol terms and values |
| `MainForm.cs` pairing | computer name, credential warning, regenerate QR, copy URL | Yes | `Pairing_*` | Complete matching resources | QR, URL, Token |
| `MainForm.cs` actions | repair, startup, APK, diagnostics, services, exit | Yes | `Action_*`, `Common_Exit` | Complete matching resources | Codex, APK |
| `MainForm.cs` dialogs | no LAN address, no URL, copy warning, startup failure, operation failure | Yes | `Pairing_*`, `Error_*` | Complete matching resources | paths/codes remain runtime values |
| `TrayApplicationContext.cs` menu | open, show QR, status, startup, integration, folders, diagnostics, about, exit | Yes | `Tray_*`, `Status_*`, `Action_*`, `Common_*` | Complete matching resources | AgentBell product name |
| `TrayApplicationContext.cs` dialogs | runtime, integration, export, folder, about errors | Yes | `Error_*`, `Codex_*`, `Diagnostics_*`, `About_*` | Complete matching resources | stable error code as argument |
| `PairingUrlDisclosurePolicy.cs` | pairing URL credential warning | Yes | `Pairing_UrlDisclosureWarning` | Matching resource | URL, Token concepts |
| `TrayStatusProjection.cs` | enum `ToString()` values | Yes | `Common_*`, `Status_Integration*` | Localized at projection boundary | enums stay unchanged internally |
| Windows completion balloon | generic completion title and body | Yes | `WindowsNotification_*` | Complete matching resources | event content and identifiers are deliberately excluded |
| Windows action-required history and balloons | permission, reply, confirmation, attention, settings | Yes | `EventHistory_*`, `EventType_*`, `*Required_*`, `Settings_Notify*` | Complete matching resources | only project basename and safe enum-derived text |
| `PairingPage.html` | connection, computer, events, project, reconnect, errors | Yes | `PairingPage_*` injected into template | Complete matching resources | JSON/message types stay unchanged |
| `DesktopApplication.cs` | listener and LAN console diagnostics | Developer console | none | English technical output | retained as internal diagnostics |
| `CodexEventTransformer.cs` title | existing event title in protocol payload | Protocol compatibility | none | rendered locally by Android/browser | retained to avoid payload change |
| `MainActivity.kt` unpaired/scanner | instructions, validation, camera permission | Yes | `pairing_*` | Complete matching Android resources | pairing URL user input unchanged |
| `MainActivity.kt` paired | computer, connection, address, permission, battery, events | Yes | `computer_*`, `connection_*`, `notification_*`, `battery_*`, `events_*` | Complete matching resources | address/port/sequence values |
| `MainActivity.kt` event cards | title, fallback summary, metadata labels | Yes | `event_*` | Complete matching resources | event summary and unknown title unchanged |
| `MainActivity.kt` settings | language page and options | Yes | `common_settings`, `settings_language`, `language_*` | Complete matching resources | English option remains `English` |
| `MainUiProjection.kt` | connection-state English labels and raw error codes | Yes | resource-ID `UiText` mappings | Stable state-to-resource mapping | state/error codes retained internally |
| `AgentBellNotificationManager.kt` | channel names/descriptions, foreground/completion text | Yes | `notification_*`, `event_turn_ended` | Complete matching resources | channel IDs unchanged |
| Android action-required notifications | action channel, four action types, local settings | Yes | `notification_action_required_*`, `notification_*_required_*`, `settings_notifications_*` | Complete matching resources | no raw Hook or assistant content |
| `MainViewModel.kt` clipboard label | AgentBell diagnostics | Yes | `diagnostics_clip_label` | Matching resource | diagnostic content remains technical English |
| `AgentBell.iss` tasks/run/icons | startup, shortcut, APK folder, launch | Yes | `Task*`, `AndroidApkFolder`, `LaunchAgentBell` | `en.*` and `zhcn.*` | filenames and task IDs unchanged |
| `AgentBell.iss` install prompts | integration failure and Hook trust | Yes | `CodexIntegrationFailed`, `CodexTrustReview` | Matching CustomMessages | stage and exit code arguments |
| `AgentBell.iss` uninstall prompts | initialization, delete data, optional/critical cleanup | Yes | `Uninstall*`, `DeleteUserData` | Matching CustomMessages | logs remain technical English |

All original user-visible literal occurrences represented above were migrated. The generated audit report records permitted product-name/resource-key exclusions and requires the remaining hardcoded user-visible count to be zero.

The current inventory contains **236 unique semantic resource keys**: 129 Windows,
95 Android, and 12 installer keys. M6 added 28 Windows keys and 24 Android keys;
the installer reused its existing bilingual keys with revised text. The generated
audit separately reports 219 application strings after excluding the 11 Windows
and six Android localization-selection strings.
