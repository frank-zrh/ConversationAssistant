# Conversation Assistant

Windows 11 x64 desktop assistant: shared microphone/system-audio capture, local Whisper transcription and optional hosted Microsoft Work IQ analysis over HTTPS (Streamable HTTP MCP, not an undocumented REST chat endpoint). Audio remains local; selected transcript/question context is sent to Work IQ only when analysis or a question is requested.

## Installation and prerequisites

Use the complete, **signed** ClickOnce package on Windows 11 x64 (build 22000+). It bundles .NET 10, Windows App SDK, local VC++ runtime libraries, Whisper CPU/NoAVX libraries, the Whisper model, speaker ONNX models and licenses/notices. Users do **not** separately install .NET, VC++ Redistributable, Node.js, Work IQ CLI, Azure Speech, Vulkan drivers or WebView2 Runtime. A microphone or system-audio output and required desktop-app microphone permission are needed for capture. CPU inference speed depends on hardware. Browser authentication, network access, an eligible organizational Microsoft 365 Copilot license, and tenant-admin consent are still required for hosted Work IQ. A publisher-controlled trusted signing certificate is required for production distribution; one is not provided.

The hosted Work IQ endpoint is https://workiq.svc.cloud.microsoft/mcp; the application uses the public client and Ask scope documented by the official hosted plugin. Startup and the Microsoft sign-in button first try the encrypted per-user token cache, then the Windows account broker (WAM), including the current Windows account when no app account is cached. Multiple cached accounts require account selection. Only when silent authentication requires interaction or the broker is unavailable does the system browser open with an explicit account picker, using http://localhost:12798 for the loopback callback. Token renewal never opens a browser. Windows SSO depends on the public client's broker registration, consent and tenant policies; an existing Windows/browser sign-in alone does not guarantee silent access, especially on first use or when MFA is required. Tokens use the MSAL Windows protected cache, with no plaintext fallback.

Microsoft authentication and Work IQ capability are separate: successful sign-in does not submit a question or require MCP tool discovery. The first AI request discovers the allowed Ask tool; an unavailable/incompatible tool is reported as a Work IQ capability error rather than a failed Microsoft login, with no CLI fallback. Live Windows SSO/OAuth/Ask behavior has not been validated in an authorized tenant and **must be verified before production rollout**.

## Build and publish

A Windows x64 build host needs .NET 10 SDK, WinUI/Windows App SDK build tools, **Visual Studio full-framework MSBuild.exe** (ClickOnce's `UpdateManifest` does not work under `dotnet publish`), and restored NuGet dependencies. Before building, explicitly provision verified Microsoft VC runtime DLLs, the Whisper model and speaker models with the [preparation script](App/Deployment/Prepare-Deployment.ps1) in a build environment that permits approved scripts. This build host enforces signed PowerShell scripts: use an organization-approved signed copy or pre-provision the inputs through a trusted pipeline; **do not bypass execution policy**. The project validates pinned model and DLL hashes with MSBuild before building, and stops on missing/changed files. Users do not run scripts. First preparation may need network access; pre-cache once online for an offline build.

```powershell
# Provision deployment inputs through your approved build pipeline before this command.
dotnet restore ConversationAssistant.sln
dotnet test Tests\ConversationAssistant.Tests.csproj
& 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe' App\ConversationAssistant.App.csproj /t:Publish /p:Configuration=Release /p:PublishProfile=ClickOnceProfile /p:Platform=x64 /p:RuntimeIdentifier=win-x64
```

The ClickOnce output is configured under `App\bin\Release\net10.0-windows10.0.26100.0\win-x64\app.publish`. The MSBuild publish target checks the packaged models and VC runtime; [payload validation](App/Deployment/Test-Deployment.ps1) can also audit the finished payload in a trusted build pipeline. Keep **setup.exe, the `.application` manifest and the complete `Application Files` folder** together. Remove old version folders when preparing a clean release; do not distribute only an `.exe`. This repository currently produces an **unsigned test deployment**. To install on other PCs, a publisher must sign and distribute ClickOnce manifests with an organization-trusted certificate; no signing material is provided or manufactured by this repository.

## Using the application

1. Choose Chinese or English and microphone, system audio or both; start listening. Whisper transcribes locally. This is not translation or identity recognition. Capture uses shared-mode audio; system audio includes *all* apps playing through the selected output. Obtain participants’ consent.
2. Sign in with Microsoft for optional Work IQ analysis. Automatic analysis is triggered after seven new finalized turns or five minutes with new content. Whole-conversation and single-group analysis, summaries and product guidance remain available. Recommended questions never submit themselves; use Ask to send explicitly. Verify evidence, prices, licensing, owners and deadlines rather than treating AI output as confirmed facts.
3. Answers use native selectable rich text, with raw Markdown available as a fallback. Remote images are not fetched automatically; opening a link requires confirmation. Conversations, analyses and answers are stored as readable local JSON under %LOCALAPPDATA%\ConversationAssistant\Conversations. Import creates a new local copy without restarting capture or submitting requests. Older records may retain historical provider metadata, but all **new** AI requests use hosted Work IQ.

## Privacy and limitations

Raw audio, speaker embeddings and credentials are not saved in conversation JSON. Hosted AI receives the relevant transcript/question context, not raw audio. Companion .workiq.log files can contain readable transcripts, prompts and replies; protect them. The adapter discovers and invokes only a compatible, non-destructive Ask tool, limits response sizes and will not silently replay a timed-out request or discard conversation context. Long transcripts depend on a returned conversation ID. Mock protocol tests do not prove live tenant authorization or answer quality.

The earlier [animated demos](docs/demos/README.md) and [product brochure](docs/product/README.md) are fictional historical illustrations and may show removed Azure Speech/CLI options. They are not current installation instructions.
