# TraeTool

Trae（trae.cn）多账号效率工具 —— WPF + WebView2，`net8.0-windows`，**数据全本地**。

多账号每日签到、数据看板、用量统计，以及 Trae 桌面客户端登录态一键切换。与 ByteDance / Trae 官方无关，请遵守 Trae 服务条款，风险自负。

## 功能

| 页面 | 能力 |
|---|---|
| **仪表盘**（默认首页） | 四项统计卡、连签天数、可翻月日历、近 14 天趋势图、账号切换、快捷签到 |
| **账号管理** | WebView2 扫码/验证码登录（本机客户端凭据为降级通道）、分组筛选、参与签到开关、Token 查看复制、从客户端导入凭据、刷新状态（含学生认证与健康徽标） |
| **每日签到** | 一键批量签到、每日定时自动签到（迟到补跑、防重复）、连续失败自动暂停、签到记录筛选、运行日志 |
| **客户端切换** | 桌面客户端登录态 vault 备份/回写，软件内切换后打开客户端即另一账号；建档向导、档案校验 |
| **用量统计** | 按会话的积分/tokens 消耗、缓存命中率、模型分布、临期套餐提醒 |
| **出站代理** | 跟随系统 / 不使用 / 手动指定（http://），只约束本工具自身请求，带连通性测试 |
| **本机网关** | 只监听 127.0.0.1 的 HTTP 接口：状态查询、触发签到、OpenAI Chat Completions / OpenAI Responses / Anthropic Messages 三种兼容对话转发、模型列表（9 列表格，可增删改） |
| **设置** | 主题（浅/深色）、开机自启、托盘、备份导入导出（可选口令加密）、设备标识重置、环境自检、检查更新、日志查看、EULA |

其他：单实例运行、系统托盘常驻、exe 图标、自动更新（GitHub Releases / version.json 双源）、本机管理网关 API（默认关闭）。

## 构建与运行

```powershell
git clone https://github.com/floaritay/traetool.git
cd traetool
dotnet build TraeTool.sln -c Release
dotnet test TraeTool.Tests\TraeTool.Tests.csproj -c Release
.\TraeTool\bin\Release\net8.0-windows\TraeTool.exe
```

依赖：.NET 8 SDK、Windows 10/11（WebView2 运行时随系统）。同一套命令在 `.github/workflows/ci.yml` 里对 push / PR 自动执行。

发版打包（版本号取自 `TraeTool/TraeTool.csproj` 的 `<Version>`，产出 zip 与同名 `.sha256`）：

```powershell
python -I tools\make_release.py                  # 目录模式（bin\Release 全目录）
python -I tools\make_release.py --single-file    # 单文件模式（先 dotnet publish，见下）
```

打包默认只保留 Windows 原生库（`runtimes/win-*/`）：完整 `runtimes/` 未压缩约 24.5 MB，其中只有 `win-*` 会在 Windows 上加载，其余会让更新包大出数倍。需要完整 `runtimes/` 时加 `--all-runtimes`。

单文件发布使用 `TraeTool/Properties/PublishProfiles/win-x64-singlefile.pubxml`（框架依赖，目标机需装 .NET 8 桌面运行时）：

```powershell
dotnet publish TraeTool\TraeTool.csproj -p:PublishProfile=win-x64-singlefile
```

启动参数：`--page=dashboard|accounts|checkin|switch|usage|proxy|gateway|settings` 指定默认页；`--wizard-preview` 向导演示；`--selftest-update=<feed>` / `--selftest-import` 内部自检。

## 数据位置

全部运行数据落在本机，**不含任何上传**：

```
%APPDATA%\TraeTool\
├── config.json        # 配置（Token/Session/DeviceId 经 DPAPI 当前用户加密）
├── data\checkin.db    # SQLite 签到记录与用量缓存
├── logs\              # traetool-yyyyMMdd.log
├── vault\             # 客户端登录态档案（按账号 Id 键控）
└── switch\            # 切换状态与回滚快照
%LOCALAPPDATA%\TraeTool\WebView\account_<id>\   # 每账号独立 WebView2 目录
```

请勿将 `config.json`、`vault\` 等目录内容提交到公开仓库。

## 出站代理

侧边栏「出站代理」页可选 **跟随系统（默认）/ 不使用 / 手动指定**（仅 `http://`——.NET 出站通道不支持 SOCKS）。只约束本工具自己的网络请求——签到、状态查询、更新下载、WebView2 登录页——不拦截其他软件的流量，也不引入任何证书或解密环节。手动模式下本机回环地址不走代理：.NET 侧在 `WebProxy.BypassList` 显式登记回环，浏览器侧靠 Chromium 自带的回环白名单（因此不能给它传 `--proxy-bypass-list=<-loopback>`，那是把回环从白名单里去掉、反而强制走代理）。设置页「测试连通」会经当前配置实测一次出网，环境自检里也会显示生效配置。

## 本机管理网关

侧边栏「本机网关」页，默认关闭；启用后只监听 `127.0.0.1:<port>`（默认 8766），供本机脚本/面板读取状态或触发签到。`/` 与 `/healthz` 免鉴权，其余接口都要带令牌（设置页可复制）：管理接口用 `X-TraeTool-Token`（或 `?token=`），`/v1/*` 另外接受 `Authorization: Bearer`（OpenAI 系）与 `x-api-key`（Anthropic 系）。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/`、`/healthz` | 端点索引与存活探针（无鉴权） |
| GET | `/status?limit=20` | 账号健康、今日签到、剩余额度、近期记录；不含 Token/会话/设备号 |
| POST | `/checkin` | 触发批量签到，`202` 异步受理，`409` 表示已有批次在跑；结果见 `/status` 的 `lastRun` |
| GET | `/v1/models` | OpenAI 兼容模型列表：上游注册表的真实 id + 别名 `trae`（需开启转发）；「名称推断」层只保留解析能力，不列在列表里 |
| POST | `/v1/chat/completions` | OpenAI 兼容对话，在账号池内取号转发（需开启转发） |
| POST | `/v1/messages` | Anthropic Messages 兼容对话（Claude 系客户端），同一池、同一流式语义 |
| POST | `/v1/responses` | OpenAI Responses 兼容对话（`input` 字符串或 item 数组，支持 `stream`） |

```powershell
curl http://127.0.0.1:8766/status -H "X-TraeTool-Token: <令牌>"
curl -X POST http://127.0.0.1:8766/checkin -H "X-TraeTool-Token: <令牌>"
curl http://127.0.0.1:8766/v1/models -H "Authorization: Bearer <令牌>"
curl http://127.0.0.1:8766/v1/messages -H "x-api-key: <令牌>" -d "{\"model\":\"trae\",\"max_tokens\":64,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"
curl http://127.0.0.1:8766/v1/responses -H "Authorization: Bearer <令牌>" -d "{\"model\":\"trae\",\"input\":\"hi\"}"
```

把任意 OpenAI 客户端的 base URL 指向 `http://127.0.0.1:8766/v1`（设置页可直接选中复制）、API key 填访问令牌，即可走账号池发起对话；Claude 系客户端把 base URL 指到根路径 `http://127.0.0.1:8766`，走 `/v1/messages`。监听端口在设置页可自行填写，留空用默认 8766。

**模型列表（四层来源聚合，高优先级在前、整体去重）**：「本机网关」页的模型列表可直接增删改——1) 人工维护（覆盖项 `ModelOverrides`）→ 2) 官网同步（`POST /api/ide/v1/batch_get_detail_param`，取 `function_configs[].config_info_list[]` 的 `config_name`，只收 `config_switch=true` 且有显示名的项，30 分钟缓存，同步失败保留上次在线结果）→ 3) 内置参考（内置 `KnownIds`）→ 4) 名称推断（本网关收到过的请求名自动累积，最多保留最近 32 条、单名不超过 64 字符，超出丢弃最早的；解析时再做归一化推断：去上下文标记 `trae[1M]`、去厂商前缀 `openai/`、忽略大小写与分隔符，`claude-sonnet-4` 可推断到 `custom_claude-sonnet-4`）；该层只在解析请求名时参与匹配，不进入 `/v1/models` 的列表）。**别名 `trae` 与目录里没有的名字会回落到默认项**：`PreferredModel` 在列表中时用它，否则取合并列表首项（人工覆盖项优先，其次官网同步结果，再次内置表）——即默认模型取决于列表顺序而非固定值；被替换的名字（别名与空名除外）会记一行日志（`pool` 标签）便于查证。`fast_apply` / `summary` / `title_generation` 这类没有显示名的是功能开关，不进列表。「更新模型列表」按钮清缓存后强制重新同步官网源并重建四层。设置页把结果渲染成 9 列表格：模型 ID / 展示名 / 厂商 / 倍率 / 思考档位 / 默认上下文 / 长上下文 / 图片 / 操作（复制 / 编辑 / 隐藏或恢复 / 删除），其中展示名、倍率、图片、思考档位取自官网响应（`display_config.display_name`、`display_contact_config.consumption_rate.data.rate`、`display_config.multimodal`、`reasoning_effort_config`），厂商与上下文窗口上游不返回，由内置条目补齐；同 id 时以先到层为准、空字段由后到层补齐，所以覆盖项只填想改的字段即可，其余仍由低层补齐。

**模型列表的增删改**：「新增模型」加一条自建条目（只有它能改模型 ID）；「编辑」展开表单改展示名 / 厂商 / 倍率 / 思考档位 / 默认上下文 / 长上下文 / 图片（图片是三选一：跟随 / 支持 / 不支持），字段留空＝交回低层补齐。「隐藏」＝从列表与 `/v1/models` 里消失，且官网同步、内置、推断都带不回来（按归一化 id 挡住同名变体），勾选「显示已隐藏」后会灰显列出并可「恢复」。「删除」只清掉该条的本地覆盖（官网/内置层有同名条目时显示回到上游的值）。改动即时生效：网关每次请求都重新读取配置，不需要重启网关。旧版的「手工维护模型」文本框（逗号/换行分隔的名字清单）会在加载配置时迁移成覆盖项，随后该字段清空。

**账号池转发的行为**：候选池 = 已启用 ∩ 持有凭据 ∩ 未被签到熔断 ∩ 不在冷却中，按轮询取号；转发前会先确认凭据可用，确认不了（未登录/会话过期）只跳过该账号、继续试池内下一个，不会因为池里有一个坏账号就让请求失败；候选耗尽回 `503 insufficient_quota`。凭据校验结果按账号复用 5 分钟（`TokenRenewer.VerifyTtl`），因此转发不会每条对话都多打一次上游状态接口；上游以认证失败回应时会作废该账号的校验缓存，下次请求重新探测或续期。设置项「启用 OpenAI 兼容对话转发」默认关闭，需与网关同时开启。

**失败处置（`RetryPolicy`，只作用于「还没向客户端写出任何内容」的阶段）**：按 HTTP 状态与响应文本决定「同号退避重试 / 换号 / 直接失败」——401·403 与 `auth_error` 换号并冷却 30 分钟；429 有 `Retry-After` 按其同号退避一次（上限 10 秒），没有则 1/2/3 秒，仍限流换号冷却 5 分钟；503/529 同号退避（单次上限 10 秒，同一请求内累计不超过 10 秒）后换号；502 同号重试一次；HTTP 200 携带的业务 `code` 与 400 文本里的额度/受限词（`积分不足`、`额度不足`、`insufficient credit`、账号受限码如 `11140`）换号冷却；400 里的 `thinking.signature` 同号重试一次仍败即失败；其余 4xx（请求本身的问题，例如上下文超长）直接回错误、不烧别的账号；空完成（有终止帧但零正文）与无法解析的响应按账号问题换号。已向客户端写出内容之后不再重试，由各协议补发合法的收尾帧。

**流式转发（`stream: true`）**：上游 SSE 边收边发——读到一帧就渲染一帧并 `Flush`，不是攒完整段再一次性发。首个可转发增量到手前全程「干跑」不写响应（HTTP 状态只有一次提交机会），首个增量到手才发 `200` + `text/event-stream` 并撤掉首字节期限；首字节等待超过 10 秒（`LocalGateway.FirstTokenTimeout`）视为该账号失败并换号。OpenAI 侧外发思考（`reasoning_content`）与 `tool_calls`，结束帧带真实用量（上游没给则记 0）；Anthropic 侧只外发正文（思考块回传需要签名，上游不给，索性不发），`stop_reason` 按 Anthropic 词表映射；Responses 侧按 `response.created` → `response.output_item.added` → `response.output_text.delta` → `response.output_item.done` → `response.completed` 发事件（出错时 `response.failed`），没有 `data: [DONE]` 标记。

**错误类型归一化**：内部错误类型（`auth_error` / `upstream_error` / `insufficient_quota` 等）在出口按各自协议的词表转换——`/v1/chat/completions` 用 OpenAI 词表（`authentication_error` / `api_error` / `insufficient_quota`…），`/v1/messages` 用 Anthropic 词表（`authentication_error` / `permission_error` / `rate_limit_error` / `api_error`…），客户端按 `error.type` 分支时不会拿到未知值。

**对话转发请求体契约（已端到端验证）**：

- **必需字段**：`config_name` / `model_name` / `model` / `function` / `stream` / `max_tokens` / `prompt_max_tokens`(168000) / `mode` / `conversation_id`·`session_id`·`project_id`(uuid) / `user_id` / `device_id` / `ide_version`·`ide_version_code`·`app_id`·`package_type`
- `messages[].content` 必须是对象数组 `[{"type":"text","text":…}]`，发字符串 → 400 / code 4001
- `function` 按模型分视图：`doubao-seed-code`、`glm-5.3-flash`、`qwen3.8-flash`、`deepseek-v4.1-flash`、`kimi-k2.8-preview` 走 `solo_agent`，其余走 `solo_work_lite`；视图与模型不符 → 4001 / 4023
- `model → (config_name, model_name)` 见 `TraeChatUpstream.ModelPairs`；未收录按 `model_name = {model}__dev`
- 响应是 SSE：`event: metadata` / `timing_cost` / `output` / `done`，`output` 帧里 **`response` 是正式回答、`reasoning_content` 是思考过程**（逐帧累积后拼接）
- 请求前必须先续期——过期 JWT 会被判 4001/401
- 逐请求签名头（`x-machine-id`、`X-Helios` 等）未发送，请求同样被接受

## 实现要点

- **登录**：WebView2 打开 `www.trae.cn/dashboard#usage`，轮询 `localStorage['Cloud-IDE-Token']`（JWT）+ Cookie `X-Cloudide-Session`；JWT 失效经 `POST /cloudide/api/v3/common/GetUserToken` 静默换新
- **签到**：`POST /trae/api/v2/ug/checkin_credits/claim`，头部 `Cloud-IDE-JWT` + `X-User-Region: cn` + `x-device-id`
- **风控**：`x-device-id` 为 16 位数字且每账号独立（服务端按设备号每日去重）；9074 → 换设备号重试一次
- **客户端切换**：固定 15 项载体白名单备份/回写 `%APPDATA%\Trae CN`；建档先整备到暂存目录再整体换入（失败不破坏原档案），恢复只写档案清单内的文件；恢复前删 `code.lock` 与 stale WAL/SHM；成功判定轮询凭据键而非时间戳；切换成功后回写 live `storage.json`（refresh 轮换防 401，回写前校验 live 凭据键仍齐全）；回滚快照在 `%APPDATA%\TraeTool\switch\rollback_*`，过期快照启动时清理
- **配置**：新增密文字段必须同时登记 `ConfigStore.SecretFields`（漏一侧会"重启就失效"）
- **主题**：`Themes/Light|Dark.xaml` 双字典同键集，应用主题时先移除旧字典再追加（WPF 合并字典越靠后优先级越高）；控件样式集中在 `Themes/Controls.xaml`
- **凭证获取（双通道）**：WebView 登录窗与本机客户端只读探查同时启动，任一通道拿到可用凭据即采用；WebView2 运行时不可用或登录未完成时降级到客户端探查（降级前弹确认，避免误取客户端当前账号）
- **账号健康**：连续签到失败达到阈值（默认 3 次，可配置）当日暂停该账号，次日自动恢复；成功或已签即清零
- **备份**：配置 + 签到库快照 + 用量缓存打包 zip（不含客户端登录态档案 `vault\`，那部分按账号重新建档）；导入只受理这三类条目（白名单），备份包是外部输入，不做白名单等于允许它往数据目录写任意文件；勾选口令加密时整体封装为 `TTBAK1` 容器（AES-GCM + PBKDF2-SHA256 210k 次），可跨 Windows 账户/机器用口令恢复
- **更新包校验**：更新源必须提供 SHA256（GitHub 资产 digest / 同名 `.sha256` 资产 / version.json 字段），下载后比对；缺失校验和或不匹配都拒绝安装
- **HTTP**：全进程按超时分档共享 `HttpClients`；批次/切换占用互斥锁走 `TryEnterBatch` / `TryEnterSwitch` 原子占用
- **出站代理**：`ProxyConfig` 一份配置同时施加到 `.NET` 通道与 WebView2（`--proxy-server`）；手动模式地址无效则回退跟随系统，变更代理只重建共享实例
- **本机管理网关**：`HttpListener` 仅绑 `127.0.0.1`，回环以外不可达；令牌默认每台机器随机生成，`/checkin` 走 `TryEnterBatch` 与页面签到互斥
- **对话转发请求体**：`TraeChatUpstream.BuildBody` 负责 OpenAI 形态到上游必需字段的映射（模型名→`config_name`/`model_name`、按模型定 `function`、id 类字段），`ParseResponse` 按 `response`/`reasoning_content` 组装回答
- **账号池转发**：`AccountPool` 纯逻辑取号（候选过滤 + 轮询 + 失败冷却），`LocalGateway` 注入 `IChatUpstream` 与凭据校验（测试用假实现替换）；`RetryPolicy` 是纯函数失败处置表，`TraeSseParser` 是纯逻辑的逐行 SSE 解析器，两者都可离线逐条断言；协议层 `ChatProtocol`（OpenAI Chat Completions）、`AnthropicProtocol`（Messages）、`ResponsesProtocol`（Responses）各自解析、输出与错误类型归一化，`ModelCatalog` 四层聚合模型列表（人工/官网/内置/推断）并做名称推断解析

## 仓库结构

```
TraeTool.sln
TraeTool/                 # 主工程（Views / ViewModels / Services / Themes / Resources）
TraeTool.Tests/           # xUnit 单元测试（健康判定、备份加密、版本与校验和、设备号、JWT、代理、网关路由鉴权、三种对话协议与流式帧、上游 SSE 解析、失败处置表、模型解析与覆盖项、账号池取号）
tools/                    # 开发辅助脚本（图标生成、窗口截图、发版打包等）
.github/workflows/ci.yml  # Windows 上构建 + 跑测试
```

## 许可证

[MIT](LICENSE)
