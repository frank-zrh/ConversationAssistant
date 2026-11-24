# Conversation Assistant · 千问会话助手

Windows 11 x64 desktop tool for live transcription, meeting summaries, recommendations, and contextual Q&A. Transcription and analysis both use the Qwen AI Platform (千问 AI 平台) pay-as-you-go APIs.

## 快速开始

1. 在[千问 AI 平台 API Keys](https://platform.qianwenai.com/home/api-keys)的 **按量付费** 区域创建 API Key（`sk-ws-` 开头，早期 Key 为 `sk-`），复制创建时显示的完整值。列表中的掩码和密钥 ID 无法使用。请确认所属业务空间有权调用所选模型。
2. 打开右上角 **设置 → 千问 AI 平台 · 按量付费**，粘贴 API Key 并保存。默认模型已选好，不需要配置 Endpoint、Workspace ID 或第二个密钥，也不需要微软账号或 Work IQ CLI。
3. 可选：点击 **测试两个模型**。它会建立一个不含音频的转录连接，并发送一条简短的测试文本；不会上传会话内容。
4. 点击 **开始会话**。更换模型前先结束会话并等待排队任务完成；只更换模型时，可将密钥框留空以保留原 Key。

| 用途 | 默认模型 | 可选模型 |
| --- | --- | --- |
| 实时语音转录 | `qwen3-asr-flash-realtime` | `qwen-audio-3.1-asr-flash-streaming` |
| 会话分析与问答 | `qwen-plus` | `qwen-flash`、`qwen-max` |

保存结果显示在设置面板底部。应用在加密写入并重新读取验证成功后才清空输入框；如果失败，请按提示检查输入、文件写入权限、占用或磁盘空间。反馈问题时可提供提示文字和错误码，不要发送 API Key。

Both services use `maas.qianwenaiapi.com`: analysis uses [`/compatible-mode/v1/chat/completions`](https://platform.qianwenai.com/docs/api-reference/chat/openai-chat) and speech uses the [Realtime](https://platform.qianwenai.com/docs/api-reference/speech-recognition/qwen-asr-realtime/websocket-api) or [ASR Streaming](https://platform.qianwenai.com/docs/api-reference/speech-recognition/qwen-audio-asr-streaming/websocket-api) WebSocket API. See the [API key guide](https://platform.qianwenai.com/docs/api-reference/preparation/api-key).

**Token Plan 个人版：**本应用只调用按量付费接口，不扣除 Token Plan Credits，也不接受 `sk-sp-` 开头的套餐密钥。

## Features and privacy

- Capture the microphone, system audio, or both via shared WASAPI. Allow desktop microphone access in Windows privacy settings. System audio captures **all** apps playing through the selected output. Raw audio is not saved locally.
- Selected audio, transcripts, and questions can be sent to Alibaba Cloud. Obtain participant consent before recording; usage may incur charges. The available real-time models do not identify speakers; labels in older records are preserved.
- Analyze the whole conversation or a group, review stage summaries and recommendations, and explicitly submit a suggested or manual question. Automatic whole-conversation analysis can be turned off in Settings. Qwen uses the supplied context and general model knowledge, without searching the web or retrieving private Microsoft 365 data.
- Previous JSON records remain importable. Import creates a separate local copy and does not send anything to Qwen automatically. New analysis uses the selected Qwen model. Rich answers use WebView2 when available, with a text fallback.
- The key is not stored in conversation records or prompts. Conversation JSON and `.qwen.log` diagnostics are readable local files and may contain transcripts, prompts, and replies; keep them confidential.
- If a connection fails after saving, confirm that the key is enabled, its workspace can use the selected models, and the network permits HTTPS and WebSocket traffic to `maas.qianwenaiapi.com`. For a long transcript, try analyzing a shorter group or select a longer-context analysis model.

## 本地数据

应用为当前 Windows 用户自行管理 `%LOCALAPPDATA%\ConversationAssistant`，缺少目录时自动创建。不依赖 Entra ID，不读取或同步 OneDrive。可从 **设置 → 打开本地数据文件夹** 直接查看。

| 路径 | 用途 |
| --- | --- |
| `qwen-settings.bin` | 千问 API Key 和模型选择，使用当前 Windows 用户的 DPAPI 加密 |
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

[Historical demos](docs/demos/README.md) show older versions of the interface. Any previous service or sign-in labels shown there do not apply to this build.