# Contributing

Contributions are licensed under Apache-2.0 as described by [LICENSE](LICENSE). By submitting a contribution, you represent that you have the right to provide it under those terms.

Keep changes inside the requested milestone and preserve the local-first privacy model. Never commit a Token, pairing URL/QR, real IP or user path, Codex payload, prompt, response, config/events/hooks data, diagnostic archive, keystore, certificate private key, or build output.

Before opening a pull request:

```powershell
dotnet format .\AgentBell.sln --verify-no-changes
dotnet restore .\AgentBell.sln
dotnet build .\AgentBell.sln -c Release --no-restore
dotnet test .\AgentBell.sln -c Release --no-build
Push-Location .\android\AgentBell
.\gradlew.bat testDebugUnitTest lintDebug
Pop-Location
.\scripts\audit-public-release.ps1
```

Pull requests from forks never receive signing secrets and must not attempt a public release. Explain security/privacy effects and add tests for untrusted input, Unicode, concurrency, persistence, or path behavior where relevant. Follow [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
