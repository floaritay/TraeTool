# Services/Gateway — 本机网关（管理接口 + 对话转发）

只监听 127.0.0.1 的 HTTP。两条线：

- **管理端点**：`GET /status`、`POST /checkin`（`X-TraeTool-Token` 或 `?token=` 鉴权），`/` 与 `/healthz` 无鉴权。
- **对话转发**：`GET /v1/models` 与三协议的 `POST /v1/chat/completions`、`/v1/messages`、`/v1/responses`，
  在账号池内取号调用 Trae 上游，失败按处置表重试同号或换号。

模块：

| 模块 | 职责 |
|---|---|
| `LocalGateway` | HTTP 监听与生命周期、请求分发入口、响应写回与 CORS |
| `LocalGateway.Admin` | `/status` 状态汇总、`/checkin` 异步受理，及两套鉴权 |
| `LocalGateway.Routes` | `/v1/models` 与三协议对话端点：解析请求体 → 取上游结果 → 按协议渲染 |
| `LocalGateway.Failover` | 非流式取号与失败处置（同号重试 / 换号 / 直接失败）、错误码映射 |
| `LocalGateway.Streaming` | 流式转发：首个可转发增量到手前不写响应，之后逐帧下发 |
| `ChatProtocol` / `AnthropicProtocol` / `ResponsesProtocol` | 各协议的请求解析、响应与 SSE 帧构造、错误类型归一化 |
| `GatewayJson` | 三协议共用的序列化选项（非 ASCII 不转义） |
| `TraeChatUpstream` | 上游适配：建连、发请求、收流、观测响应头、重试等待 |
| `TraeChatUpstream.Body` | 客户端模型名 → 上游 `config_name`/`model_name`，请求头版本标识 |
| `TraeChatUpstream.Parsing` | 非流式响应解析、文本抽取与上游错误描述 |
| `TraeSseParser` | 上游 SSE 逐行解析：纯逻辑，喂字符串即可测 |
| `TraeUpstreamContracts` | `IChatUpstream` 与增量/结果类型（测试替身替换点） |
| `AccountPool` | 取号：候选过滤（启用 ∩ 有凭据 ∩ 未熔断 ∩ 未冷却）+ 轮询 + 失败冷却 |
| `RetryPolicy` | 失败处置纯函数表：动作 / 同号重试等待 / 该账号冷却时长 |
| `ModelCatalog` | 模型列表四层聚合（人工 > 官网 > 内置 > 推断）与名称归一化解析；分层见 `.Layers`、内置表见 `.Builtin`、官网拉取见 `.Sync`、条目类型见 `ModelEntry` |

入口在「网关」页（开关、端口、令牌、OpenAI base URL、模型列表与人工作覆盖项）。
