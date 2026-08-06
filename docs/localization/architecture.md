# Localization architecture

AgentBell 0.6 uses one localization mechanism per platform while keeping protocol and diagnostics contracts language-neutral.

## Windows

`AgentBell.Localization` is a UI-framework-independent .NET 10 library shared by Desktop Core and the WinForms Tray. `Resources/Strings.resx` is the default English resource and `Strings.zh-CN.resx` is the only satellite resource. `ResourceAppLocalizer` provides `Get` and `Format`; missing localized values fall back to English and a missing key never appears as user-facing text.

`AppLanguageService` accepts the persisted values `system`, `en-US`, and `zh-CN`. System mode resolves to Simplified Chinese only when the Windows UI culture is exactly `zh-CN`; all other cultures resolve to `en-US`. It sets `CurrentUICulture` and `DefaultThreadCurrentUICulture` before WinForms controls are created. An unsupported explicit setting falls back to `system` and records only the stable diagnostic code `invalid_language_fallback`, never the raw value.

The selected value is stored as `language` in the existing `%LOCALAPPDATA%\AgentBell\config.json`. It is updated through the existing atomic config store and pairing session, so switching language does not replace the DPAPI-protected pairing token, device ID, LAN port, events, or Codex integration. The Tray rebuilds its context menu, tooltip, main-window controls, status projection, dialogs, future privacy-safe completion balloons, and pairing-page responses immediately. Completion balloons contain only a generic localized title and body; they never include event content or identifiers.

The LAN pairing page is a resource-backed HTML template. The server injects JSON-encoded localized text from the same `.resx` resources for every page request. Protocol message names, API routes, error codes, and event fields remain unchanged.

## Android

Android uses the platform resource system: default English in `values/strings.xml` and Simplified Chinese in `values-zh-rCN/strings.xml`. Compose uses `stringResource`; services, notification channels, clipboard labels, and notifications use `Context.getString`.

`AppLanguageController` maps UI choices to AppCompat per-app locales: empty locale list for `system`, `en` for `en-US`, and `zh-CN` for Simplified Chinese. AppCompat persists the selection on Android 12 and earlier through its metadata service and uses the framework per-app language API on Android 13 and later. The Activity is recreated to recompose text; the foreground service and WebSocket are not restarted. Configuration callbacks refresh channel names and the ongoing notification.

View models and network code continue to expose enums, stable codes, and structured state. `UiText` maps those values to Android resource IDs only at the UI boundary. Known Codex completion titles are rendered locally; protocol payload text and user content are not translated or rewritten.

## Installer and uninstaller

Inno Setup declares `en` with the compiler's `Default.isl` and `zhcn` with the vendored official `installer/Languages/ChineseSimplified.isl` (Inno Setup 6.5.0+). Every AgentBell-specific prompt, task description, shortcut label, install error, trust prompt, uninstall option, and cleanup error has matching `en.<Key>` and `zhcn.<Key>` entries in `[CustomMessages]`. Pascal Script uses `CustomMessage` and `FmtMessage`; technical logs remain stable English.

Setup initially follows the Windows UI language and allows language selection before installation. Inno Setup records that language for the uninstaller. Installer language does not change the AgentBell application setting, whose first-run value remains `system`.

## Compatibility boundary

Localization does not change JSON property names, enum values, routes, WebSocket message types, HTTP headers, CLI or Hook arguments, registry keys, filenames, environment variables, error/log codes, protocol version, ports, tokens, hashes, or user/Codex content.
