# Conversation Assistant

Windows WinUI 3 / .NET 10 桌面应用。**支持转写卡片与回复联动、行动请求自动提交和右键编辑**，继续使用目标资源租户中的 Microsoft Entra ID 身份访问 Azure AI Speech，不使用资源 Key。Azure Speech 与 Work IQ 共用登录入口，分别管理各自凭据；也可显式选择 Whisper 离线模式。转写与问答独立运行。

## 准备

- Windows 11（x64）、.NET 10 SDK、Windows App SDK 构建依赖（由 NuGet 还原）；打包调试需要 Windows 开发者模式。使用麦克风时需在 Windows 隐私设置中允许桌面应用访问。**不再要求安装 Windows SAPI 语言包**。
- Azure 模式需要 **Azure AI Services / Speech 自定义资源 Endpoint、组织工作帐号及资源 RBAC 权限**，不需要 Key、客户端密码或 Azure OpenAI。当前支持 Azure 公有云 `*.cognitiveservices.azure.com`。监听音频会发送到配置的 Azure 资源，可能计费；不会修改资源禁用 Key 的策略。
- 仍内置离线 `ggml-small.bin`（约 466 MiB），供显式选用 Whisper 时使用；其 SHA-1 为 `55356645c2b361a969dfd0ef2c5a50d530afd8d5`。Whisper 模式优先 Vulkan GPU，后备本地 CPU，不会上传音频。模型来源：[whisper.cpp 官方模型列表](https://github.com/ggml-org/whisper.cpp/blob/master/models/README.md)，许可在 `App\Models\LICENSE-*.txt`。
- 输入默认 **中文（zh-CN）**，可选择 **English（en-US）**；这里提供的是语音转文字，不是中英文之间的翻译。更改语言需暂停再恢复。答案语言中的 `Auto` 仅控制 Work IQ 回答语言。
- 安装[官方 Work IQ CLI](https://github.com/microsoft/work-iq) 并取得 `workiq.exe`。应用使用可执行文件（而非 `.cmd` 脚本），通过参数列表安全地传递提问。若不在 PATH 中，设置环境变量 `CONVERSATIONASSISTANT_WORKIQ_PATH` 为 `workiq.exe` 的**绝对路径**；重启应用使其读取新变量。新变量未设置时仍兼容旧变量 `MEETINGCOPILOT_WORKIQ_PATH`。本机若安装了 Microsoft Scout，也会尝试发现其附带的官方 CLI。
- 使用前阅读并接受 [Work IQ 评估条款](https://github.com/microsoft/work-iq/tree/main/EULA)，按官方说明在终端执行 `workiq accept-eula`。Work IQ CLI 管理自己的 OAuth 缓存；本应用不读取其令牌文件。Entra 登录取得的工作帐号用于 CLI 的 `--account` 参数，后续问题也显式指定同一帐号。Work IQ 的管理员同意与 Microsoft 365 Copilot 许可独立于 Azure RBAC；参阅[官方租户启用指南](https://github.com/microsoft/work-iq/blob/main/ADMIN-INSTRUCTIONS.md)。
- 富文本答案使用 Windows 上的 **Microsoft Edge WebView2 Runtime**，通常已随 Windows 11 安装；如果缺失，应用显示原始答案并提示错误。应用创建 InPrivate WebView2 会话并禁用脚本、宿主对象及自动外部资源请求。

## 配置 Azure Speech Entra 身份

1. 由资源管理员为**登录用户**在目标 Azure AI 资源上授予 **Cognitive Services Speech User**（或 **Cognitive Services Speech Contributor**）角色。普通资源 Contributor、Work IQ 登录成功或取得 Entra 令牌，不等于具有 Speech 数据访问权限。应用不会代你提升权限。
2. 打开 **Settings**，选择 **Azure Speech · Entra ID（在线）**，填写根 Endpoint，例如 `https://your-resource.cognitiveservices.azure.com/`。**不需要额外 Resource ID 或 region**：SDK 1.51 的 `SpeechConfig.FromEndpoint(Uri, TokenCredential)` 支持此模式。不能使用区域 `*.stt.speech.microsoft.com` 代替自定义资源域名；当前未配置国家云的单独登录 authority。
3. **Tenant ID 必填，必须是 Azure AI 资源所在订阅的目录 ID。** 在 Azure 门户打开该资源所属的订阅，在订阅属性中查看目录/租户 ID。这不是 region（如 eastus），也不一定是工作帐号或 Work IQ 的主租户。跨目录用户需要在资源租户中具有成员/来宾资格并获授 Speech 角色。Client ID 仍可选，生产推荐填写资源租户批准的公共客户端应用 GUID；该应用必须能在目标租户登录。留空时使用 SDK 开发客户端，可能被组织限制；这不是绕过审批的办法。
4. 自有应用注册应配置 **Mobile and desktop applications**，系统浏览器重定向 URI 为 **`http://localhost`**，按租户规则启用公共客户端并批准所需委托访问。遵循[微软桌面应用注册说明](https://learn.microsoft.com/entra/identity-platform/scenario-desktop-app-configuration)及 [Speech Entra 认证说明](https://learn.microsoft.com/azure/ai-services/speech-service/how-to-configure-azure-ad-auth)。本应用请求的范围为 **`https://cognitiveservices.azure.com/.default`**；不得填写客户端密码。
5. 填写 Endpoint、资源 Tenant ID，勾选音频上传授权并保存，再点击顶部 **Microsoft 登录**：先在指定资源租户获取 Entra 身份，再用同一工作帐号执行 Work IQ CLI 登录及无私有数据的 `2+2` 可用性检查。**一个按钮不代表只出现一次授权窗口或两个服务在同一租户**。SDK 帐号缓存按 Tenant ID / Client ID 隔离；改 Tenant ID 后旧令牌失效，必须重新登录。
6. 点击 **测试已保存连接（不录音）**检查资源连接；不会开启麦克风。`BadRequest/HTTP 400 [TenantMismatch]` 明确表示令牌租户与资源租户不同，应修改资源 Tenant ID 并重新登录，**不是要求换 Endpoint 或启用 Key**。普通 HTTP 400 不能直接归因为地址错误。`AuthenticationFailure` 需检查授权；`Forbidden` 需检查目标租户里的 Speech RBAC 及网络规则。
7. 改变帐号、Tenant ID、Client ID 或识别服务前先 **End Conversation**。Azure Identity 使用 OS 加密令牌缓存，Speech SDK 自动请求续期；后台不自动弹登录窗口，若 MFA/条件访问要求重新交互，请结束对话后再点 Microsoft 登录。离线模式登录按钮仅处理 Work IQ。

实现使用官方 `Azure.Identity.InteractiveBrowserCredential` 和 Speech SDK 的 `TokenCredential` 重载，不拼接旧式 `aad#...` 字符串，也不把 M365/Graph/Work IQ token 交给 Speech。HTTPS 自定义资源 Endpoint 交由 SDK 路由与刷新令牌。输入仍为 16 kHz / 16-bit / mono PCM，音频不落盘。

设置以 DPAPI `CurrentUser` 保存在 `%LOCALAPPDATA%\MeetingCopilot\speech-settings.bin`。为兼容已有安装，应用改名后保留旧数据目录、加密用途标识及帐号缓存名称，不会因改名丢失已保存设置。旧版本加密文件里的 `ApiKey` 字段会被忽略，**保存新设置时移除**，不会继续用于任何请求。Entra 帐号元数据另存为 DPAPI 加密的 `entra-account-*.bin`；访问/刷新令牌由 Azure Identity 的独立 OS 加密缓存管理，禁止退回明文缓存。不要导出、分享缓存或令牌。

## 构建与运行

从此目录执行：

```powershell
dotnet restore .\ConversationAssistant.sln
dotnet build .\ConversationAssistant.sln -p:Platform=x64
dotnet test .\Tests\ConversationAssistant.Tests.csproj
dotnet run --project .\App\ConversationAssistant.App.csproj -p:Platform=x64
```

最后一条使用官方 WinUI 模板的 MSIX 调试身份，需要启用 Windows 开发者模式。若只需在未启用开发者模式的机器上检查界面，可执行：

```powershell
dotnet run --project .\App\ConversationAssistant.App.csproj -c Release -p:Platform=x64 -p:WindowsPackageType=None -p:EnableWinAppRunSupport=false --no-launch-profile
```

生成 x64 MSIX（**未签名，不可直接分发安装**）：

```powershell
dotnet publish .\App\ConversationAssistant.App.csproj -c Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false -p:AppxBundle=Never
```

本次版本为 `1.4.0.0`；请关闭旧版再运行上述 Release 命令。产物位于 `App\AppPackages\`。正式分发时须使用受信任的签名证书并将清单中的 `Publisher` 替换为证书主题，按[微软 MSIX 打包说明](https://learn.microsoft.com/windows/msix/package/packaging-uwp-apps)签名。MSIX 当前仍未签名，不能直接安装。

## 使用与隐私

1. 点击 **Microsoft 登录**，Azure Entra 与 Work IQ 的状态分别显示。`Azure Entra 令牌就绪` 不表示 Azure Speech 资源 RBAC 已通过；`Work IQ ✓` 表示当时可应答，不保证有权访问每项 M365 数据。登录失败时仅显示脱敏错误码，不显示令牌。
2. 顶部选定输入语言后点 **Start Conversation**，等到 **Listening** 再说话。Azure 模式显示 “Connecting to Azure Speech”，在线临时/最终字幕由 SDK 推送，分句静音参数为 800 ms，实际完成时间受网络与服务影响。Whisper 模式仍需首次预热，保留原先的静音分段/5 秒无新语音收尾保护。录音时语言选择锁定；如需切换，先 **Pause Listening**、改选语言，再 **Resume**。
3. 顶部 **Auto Ask Work IQ** 默认开启；先完成 Microsoft 登录，否则自动提问会显示登录错误而不会发送。只对最终字幕判断中英文问题及行动请求，例如“帮我找一下相关文档”“请总结一下刚才的讨论”或“Help me find the related files”，不要求句尾有问号。行动请求保留原句及相关上下文，仍通过官方 Work IQ CLI `ask` 发送；每次请求均指定联合登录的帐号。普通陈述不会单独触发 Work IQ，重复问题或请求有抑制。不做说话人分离。可关闭 Auto Ask，使用底栏 **Ask**；**End Conversation** 清除瞬时转写、上下文和回答历史。
4. Work IQ 首先根据原始转写与最近上下文重构问题，输出 `UNDERSTOOD QUESTION`（理解后的问题），然后给出 `SUGGESTED ANSWER`、`KEY POINTS`、`SOURCES / CONTEXT`。原始问题仍保留在历史中，不会被重写覆盖。提示词要求只纠正有上下文依据的同音字、断句和残缺表述，保留名称、数字、否定和限制；不确定时标注假设，关键歧义需澄清，而非自行编造。
5. 答案面板渲染 Markdown 标题、列表、代码、表格、链接及图片。富文本加载成功且正文非空后才隐藏原文；加载失败或缓慢时仍可阅读原始回复，**查看原文 / 格式化**可随时切换。历史通过答案区顶部下拉框选择；**Settings** 在窗口右上角，避免挤占正文空间。安全的内嵌 PNG/JPEG/GIF/WebP 图片可直接显示；远程图片默认仅显示文字与链接，**不会自动发起请求**。点击受支持的 Microsoft 来源图片时会先显示完整 URL，明确同意后才尝试加载为内存中的预览；其他链接经确认后交给浏览器。需要 Microsoft 365 登录的图片可能无法内嵌，可在浏览器中打开。

## 转写卡片

左侧每条最终转写显示为可点击卡片，临时转写仍单独显示，不会提前触发问题。卡片按稳定的转写 ID 关联右侧回复，不用相似文本猜测匹配。

| 卡片背景 | 状态与左键点击行为 |
| --- | --- |
| 白色 | 尚未获取回复；点击将该转写及它当时的相关上下文排入 Work IQ 队列 |
| 淡黄色 | 等待或正在获取回复；点击定位对应请求，连续点击不重复提交 |
| 淡绿色 | 已有回复；点击直接在右侧显示原回复，不再次请求 |
| 淡棕色 | 请求失败；点击复用原问题和上下文重试，保留同一条历史 |

每张卡片都有文字状态，选中时显示蓝色边框，也可用键盘聚焦并按 Enter/Space 操作。选择一张历史卡片后，新自动问题或后台回复不会抢走右侧焦点；点击 **Dismiss** 后恢复跟随新问题。手动输入的问题仍保留独立历史。

**右键单击任意颜色的转写卡片**会将卡片全文复制到底栏 **Ask** 输入框，替换原有输入并将光标移到末尾；不会自动提交、重新排队或切换当前回答。修改后点击 **Ask** 或按 **Ctrl+Enter** 手动提交。

卡片左键点击是明确的手动提问：即使该转写是陈述句，或 Auto Ask 已关闭，左键点击白色卡片也会排队。查看旧卡片时只使用该语句之前的有限上下文，不夹带之后的讨论。**Clear** 清除转写卡片与活动上下文，保留已有回答历史；**End Conversation** 清除本次对话的卡片、上下文和回答。

应用不将原始音频、完整字幕、明文令牌或完整回答保存到本地文件；日志不记录 token、SDK 原始认证详情或敏感字幕。SDK 的令牌缓存使用 OS 加密。Azure 模式下，**监听音频持续发送到 Azure**；Work IQ 只接收自动检测的问题或行动请求、手动输入/左键点击的文本及有限上下文。右键复制到 Ask 本身不会发送内容；复制答案由用户手动触发。不会读取 Work IQ 的 token 缓存，也不会绕过组织策略。

## 已知限制与测试

- **CLI 的 `workiq ask --json` 返回完整答案**，没有已确认的逐 token 流式接口；当前界面提供等待/完成状态，不能声称满足实时 token 流式显示。首次提问由官方 CLI 隐式创建会话，此后复用返回的 `conversationId`；没有编造单独的“创建空会话”接口。
- Work IQ CLI 的 `ask -q` **没有独立的 system-role 参数**：应用将“系统风格”的结构化输出要求作为普通问题的一部分传入，而不是具有更高优先级的真正系统提示词；Work IQ 也可能不完全遵循格式。Markdown 会在本地经过 HTML 白名单过滤并受 WebView2 CSP 约束；原始 HTML、脚本、非 HTTPS 链接、超大图片及自动加载外部图片会被限制。
- **5 秒收尾保护仅适用于原有离线分段逻辑，不是 Azure 或 Work IQ 回复 SLA。** 在线转写及问答受服务负载、网络、授权和配额影响，不能保证 5 秒内获得最终答案。
- 官方 CLI 没有可确认的 **Web grounding 禁用参数**。提示词要求不浏览网页，但这不是强制安全控制；有严格禁止 Web grounding 要求的环境应暂停 Work IQ 功能，直到官方提供可执行的控制。设置面板如实标明该限制。
- 官方 CLI 的单次问答通过 `-q` 参数接收文字；运行期间有权限查看本机进程命令行的用户可能看到所提交的问题和上下文。若这不符合组织的数据处理政策，请勿启用 Work IQ 问答。
- 只采集选中的**麦克风**，不采集通话软件的扬声器回放。离线识别使用有界内存队列与本地语音活动分段；对极轻的声音、持续噪声、多人同时说话及中英混说仍可能漏检或误检。CPU 较慢时字幕可能滞后；积压过多会显示错误而不是静默丢失。首次 Vulkan 推理需编译着色器，耗时可能明显高于后续片段。
- 自动提问使用规则而非外部 LLM，可能漏检或误检；可关闭并用手动提问。断网会使 Azure 在线转写不可用；需要离线使用时请显式切换 Whisper。
- **Endpoint、令牌获取、资源租户和资源授权是不同阶段。** 返回 TenantMismatch 时必须先修正资源租户，应用不能猜出目录 ID 或替用户授予权限。本次卡片改动使用模拟转写与回复验证，没有更改 Azure/Work IQ 认证或用户配置。
- 该环境下打包调试运行被未开启的开发者模式阻止；未打包的 WinUI 窗口已可启动。本机用离线合成语音分别验证了中文与英文的模型推理，并验证实际麦克风长时间监听；**尚未以真人语料量化识别准确率**。出厂 MSIX 仅为未签名构建，不是可安装的发布包。

测试 `dotnet test .\Tests\ConversationAssistant.Tests.csproj` 覆盖中英疑问句和行动请求、普通与否定陈述、Auto Ask 开关、旧加密设置与缓存兼容、最终片段优先于过期临时片段、5 秒无语音强制收尾、上下文到期、去重、CLI JSON 会话 ID、手动重试、断网、暂停、资源清理，以及 **A 的 Work IQ 答案未完成时字幕继续出现，B 在队列等待**。富文本测试检查问题重构提示词、原文保留、受控本地 HTML 导航、表格与 HTTPS 来源保留、脚本/危险链接剔除、远程图片不可自动抓取、内嵌图片大小/格式限制。现场还需分别录入英语、中文、长时间讲话、静音与背景噪声，并验证麦克风拔出/权限拒绝及组织登录失败场景。

卡片相关回归覆盖自动/手动点击并发去重、历史上下文、失败重试、已清除/跨对话卡片不可提交、空回复不得标绿及四色状态。界面验证需检查真实 Button 背景绑定、左键事件、左右选择联动与后台更新不抢焦点，以及右键后 Ask 被填入但不提交，修改后可通过 Ask 或 Ctrl+Enter 手动提交。

参考：[Azure Speech 持续识别](https://learn.microsoft.com/azure/ai-services/speech-service/how-to-recognize-speech)、[Speech 音频输入流](https://learn.microsoft.com/azure/ai-services/speech-service/how-to-use-audio-input-streams)、[Azure 自定义域名与 SDK 配置](https://learn.microsoft.com/azure/ai-services/speech-service/speech-services-private-link)、[Windows DPAPI](https://learn.microsoft.com/dotnet/api/system.security.cryptography.protecteddata)、[whisper.cpp 本地模型](https://github.com/ggml-org/whisper.cpp/blob/master/models/README.md)、[官方 Work IQ CLI](https://github.com/microsoft/work-iq)。
