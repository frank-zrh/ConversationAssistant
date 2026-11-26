# Conversation Assistant · 千问会话助手

Windows 11 x64 desktop tool for transcription, meeting summaries, recommendations, and contextual Q&A. Pay-as-you-go supports live transcription. Experimental Token Plan supports text-only conversations or segmented transcription using its own speech model and API.

## 快速开始

1. 打开右上角 **设置 → 千问 AI 平台 → 千问计费方式**，选择 **按量付费**（默认）或 **Token Plan（试验性）**。设置页只显示当前模式的密钥和模型，不会覆盖另一模式的配置。
2. 在[千问 AI 平台 API Keys](https://platform.qianwenai.com/home/api-keys)获取完整密钥。按量付费密钥以 `sk-ws-`（旧版 `sk-`）开头，Token Plan 密钥以 `sk-sp-` 开头。已保存的密钥不回填，留空保留原密钥。
3. 点击 **保存千问设置**立即保存，或保持应用打开，停止修改 3 分钟后自动保存。保存不调用云接口；可另行运行连接测试，测试不会上传音频或会话内容。
4. 两种模式分别选择 **语音转录模型**。按量付费继续使用实时流式转录；Token Plan 可选 `qwen-audio-3.0-asr-flash`，每 8 秒提交一段音频，暂停或结束时提交剩余音频。Token Plan 默认关闭转录，保留 **开始文字会话** 和打开/导入已有记录的功能，不会连接按量付费接口。

| 用途 | 默认模型 | 可选模型 |
| --- | --- | --- |
| 实时转录（按量付费） | `qwen3-asr-flash-realtime` | `qwen-audio-3.1-asr-flash-streaming` |
| 分段转录（Token Plan） | 关闭（仅文字） | `qwen-audio-3.0-asr-flash` |
| 分析和问答（按量付费） | `qwen-plus` | `qwen-flash`、`qwen-max` |
| 分析和问答（Token Plan） | `qwen3.8-flash` | `qwen3.8-max` |

分析和问答使用 OpenAI 兼容接口：按量付费域名为 maas.qianwenaiapi.com，Token Plan 域名为 token-plan.maas.qianwenaiapi.com。两种模式不会相互回退。参见[千问 AI 平台 Token Plan 使用规范](https://platform.qianwenai.com/docs/token-plan/token-plan-permissions-and-usage)。

启用自动会话分析时，分析请求也使用所选计费方式。不要在排查问题时分享 API Key。

## Features and privacy

- Capture the microphone, system audio, or both via shared WASAPI. Allow desktop microphone access in Windows privacy settings. System audio captures **all** apps playing through the selected output. Raw audio is not saved locally.
- Selected audio, transcripts, and questions can be sent to Alibaba Cloud. Obtain participant consent before recording; usage may incur charges. The available real-time models do not identify speakers; labels in older records are preserved.
- Analyze the whole conversation or a group, review stage summaries and recommendations, and explicitly submit a suggested or manual question. Automatic whole-conversation analysis can be turned off in Settings. Qwen uses the supplied context and general model knowledge, without searching the web or retrieving private Microsoft 365 data.
- Previous JSON records remain importable. Import creates a separate local copy and does not send anything to Qwen automatically. New analysis uses the selected Qwen model. Rich answers use WebView2 when available, with a text fallback.
- The key is not stored in conversation records or prompts. Conversation JSON and `.qwen.log` diagnostics are readable local files and may contain transcripts, prompts, and replies; keep them confidential.
- If a connection fails, check model access and verify that the network permits HTTPS to the selected analysis host (and WebSocket connections to maas.qianwenaiapi.com for live transcription). For Token Plan, also check your subscription status and remaining Credits. For a long transcript, try analyzing a shorter group or select a longer-context analysis model.

## 本地数据

应用为当前 Windows 用户自行管理 `%LOCALAPPDATA%\ConversationAssistant`，缺少目录时自动创建。不依赖 Entra ID，不读取或同步 OneDrive。可从 **设置 → 打开本地数据文件夹** 直接查看。

| 路径 | 用途 |
| --- | --- |
| `qwen-settings.bin` | 计费方式和两套模型配置；两种 API Key 分别使用 Windows 当前用户的 DPAPI 加密，发请求时才按需解密，不以明文落盘。 |
| `Conversations\` | 会话 JSON、分析日志 `.qwen.log`，以及保存时的临时文件 |
| `WebView2\` | 富文本答案查看器的本地浏览器配置和缓存，首次初始化查看器时自动创建，保留 InPrivate 模式 |

此版本沿用已有的本地设置和会话目录，不需要迁移，也不会删除旧文件。如果旧记录只在 OneDrive，请先用 Windows 将需要的 JSON 保存为本地文件，再复制到 `Conversations\`，应用不会代为访问 OneDrive。备份时请保护会话内容；加密的密钥文件不能直接供其他 Windows 用户使用。

## Build and run

Requires Windows 11 x64 and the .NET 10 SDK. The desktop app and test project no longer include Whisper, sherpa-onnx, offline speech models, Windows Speech, Azure Speech, Entra ID, or the Work IQ CLI. WinUI, audio capture, and the Qwen implementation remain. Older records can still be opened, including their original speaker labels.

The app references the WinUI and Windows App SDK Runtime components rather than the full SDK bundle. Publish to an empty folder to avoid retaining old binaries from earlier builds.

```powershell
dotnet restore .\ConversationAssistant.sln
dotnet build .\ConversationAssistant.sln -p:Platform=x64
dotnet test .\Tests\ConversationAssistant.Tests.csproj
dotnet run --project .\App\ConversationAssistant.App.csproj -p:Platform=x64 --no-launch-profile
```

[Historical demos](docs/demos/README.md) show older versions of the interface. Any previous service or sign-in labels shown there do not apply to this build.[Historical demos](docs/demos/README.md) show older versions of the interface. Any previous service or sign-in labels shown there do not apply to this build.