# Privacy

AgentBell has no cloud server, account system, telemetry, analytics, advertising SDK, or data sale. Windows and Android communicate directly over the same trusted LAN.

Windows stores a DPAPI-protected pairing token, a non-secret device identifier, protocol configuration, up to 100 sanitized recent events, pairing artwork, and bounded sanitized diagnostics under the current user's LocalAppData Known Folder. Events may contain a project directory name and a whitespace-normalized assistant summary of at most 160 Unicode text elements; they never store the full working path, original JSON, prompt, input messages, or raw session/turn IDs.

Android stores the server address, protocol metadata, encrypted pairing credential, resume sequence, and bounded recent notification history in app-private storage. The pairing key is protected with Android Keystore. AgentBell does not read source files or Codex transcripts.

Diagnostics are local, bounded, and sanitized by default. A diagnostic export occurs only when the user requests it and should still be reviewed before sharing.

Windows data can be deleted through the installer's explicit delete-data option or by removing the AgentBell data directory after the program exits. Android data can be removed with system “Clear storage” or uninstall.

The current HTTP/WebSocket LAN transport is authenticated but not TLS and not end-to-end encrypted. Use AgentBell only on a trusted private LAN; do not expose its port or pairing material to public or guest networks.
