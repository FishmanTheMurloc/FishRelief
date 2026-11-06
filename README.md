# FishRelief 发鱼站

一个自托管的模型网关中间件：客户端发来的是一条 OpenAI 形状的 `/v1/chat/completions` 请求，但其中 system prompt 必须是清单定义的信封结构，模型与工具也必须命中清单；网关据此按清单规则和 Liquid 模板重建 system prompt、首条 user 消息与工具声明，执行准入后把改写后的请求发往上游 LLM。

> 面向没有大肥鱼的人，提供有限的、特定的大肥鱼；有多余大肥鱼的人可以把自己的上游额度共享出来。由于设计是直接奔着裸奔的公共服务去的，防护放在“限制用途”而不是“限制身份”上，失败的话发鱼站就当场倒闭啦。

## 工作方式

```text
客户端 ── POST /v1/chat/completions ──▶ 网关
                                        │ 1. 端点与请求体上限检查
                                        │ 2. 消息结构校验 + system 信封解析
                                        │ 3. 按清单重写 system / user / tools，校验 model
                                        │ 4. 准入（全局并发闸门 + 有界等待）
                                        │ 5. 改写后的请求发往上游 /v1/chat/completions
                                        │ 6. 上游响应回写 + 记录
                                        ▼
                                     上游 LLM
```

启动时按顺序自检，任一步失败都拒绝监听并退出（退出码 1，日志为 `网关启动失败`）：

1. 配置：`Upstream:Endpoint`、`Upstream:ApiKey`、`Manifest:Path`、`Audit:Path` 必须存在。
2. 上游探测：请求上游 `/v1/models`，超过 10 秒算失败。
3. 清单：读取、严格反序列化、跨字段校验、读取模板。
4. 模型对账：清单里出现的每个模型名都必须在上游模型清单中。

因此容器健康检查只需要判断端口是否可连：端口开始监听时，以上四项已经全部通过。

## 快速开始

镜像内只包含 demo 清单（模型名 `deepseek-flash` 为占位，需替换为上游实际暴露的模型），真实清单通过目录挂载在运行时提供。

```bash
# 在仓库 deploy/ 目录下准备部署目录
mkdir -p fish-relief/manifests/local
cp docker-compose.runtime.yml .env.example fish-relief/
cp -r <清单包目录>/* fish-relief/manifests/local/

cd fish-relief
cp .env.example .env
vi .env   # 至少填写 Upstream__Endpoint 与 Upstream__ApiKey

# private 包才需要登录；public 包可跳过
# echo <PAT> | docker login ghcr.io -u <用户名> --password-stdin

export FISH_RELIEF_IMAGE=ghcr.io/<owner>/fish-relief-gateway:latest
docker pull "$FISH_RELIEF_IMAGE"
docker compose -f docker-compose.runtime.yml up -d
docker compose -f docker-compose.runtime.yml ps
curl http://127.0.0.1:5289/v1/models
```

宿主端口默认 `5289` → 容器 `8080`。启动后的 `start_period`（60 秒，含最长 10 秒的上游探测）内会显示 `(health: starting)`，之后应为 `Up (healthy)`。

`/v1/models` 应返回清单内所有任务的模型名并集；裸的 chat 请求（`messages` 只有一条 `user`）会因缺少 system 信封返回 400，这是预期行为。

镜像仅发布 `linux/amd64`；arm 的请自行尝试。

## 客户端接入契约

目前只实现了 `POST /v1/chat/completions` 与 `GET /v1/models`。客户端不需要真实凭证，网关不校验入站 `Authorization`。

客户端发的仍是一条 OpenAI 形状的 `/v1/chat/completions` 请求，但 `system` 必须是清单信封，`model` 与申报的工具必须命中清单。最终发给上游的是网关按清单重建的请求：`system` 与首条 `user` 由清单生成，申报的工具按清单同名定义重建，首条 `assistant` 之后的历史消息保持不变；客户端写的 `system` 文本不会作为提示词发出，`tools` 定义也不被采用。

### 1. 请求体

网关只读取 `model`、`stream`、`messages`、`tools` 四个字段，其余字段一律忽略：

- `model` 必填，必须命中当前任务的 `availableModels`。
- `stream` 为 `true` 时按 SSE 输出，否则返回 JSON。
- `tools` 只使用 `type: "function"` 的 `function.name`；描述与参数 schema 由清单重建。
- `temperature`、`top_p`、`max_tokens`、`tool_choice` 等不会发往上游；当前也不会替客户端补 `max_tokens`，上游默认值生效。

### 2. 消息结构

必须是严格的单轮任务形态，不是通用多轮对话：

1. 第一条必须是 `system`，内容为渲染信封（JSON）。
2. 第二条必须是 `user`，且只能有一条；其正文会被网关替换为清单的 `firstUserMessage`。
3. 从第三条起只允许 `assistant` 与 `tool`：
   - 每条 `assistant` 必须携带至少一个 `tool_calls`；
   - 旁白（纯文本）必须与 `tool_calls` 写在同一条 `assistant` 消息里，单独的纯文本中间步骤会被 400；
   - 每个 `tool` 消息的 `tool_call_id` 必须与最近一条 `assistant` 的调用配对，且每条调用都要在进入下一条 `assistant` 前得到结果；
   - 历史中的 `tool_calls.function.name` 必须在当前任务工具集内。
4. 轮次没有单独限制，历史长度只由清单的 `limits.maxMessages` 间接约束。

### 3. 渲染信封

`system` 消息的文本必须是一个 JSON 对象，不能包在自然语言里：

```json
{
  "model": "deepseek-flash",
  "stream": false,
  "messages": [
    {
      "role": "system",
      "content": "{\"task\":\"dispatch\",\"data\":[{\"name\":\"entities\",\"keys\":[\"电子大肥鱼\",\"泡泡大肥鱼\"]}]}"
    },
    { "role": "user", "content": "占位内容，网关会覆盖" }
  ]
}
```

这段 JSON 只用于选择任务与上报键；最终 `system` prompt 由网关渲染，不会把这段文本原样发给上游。

信封字段：

- `task` 必填，必须是清单 `tasks` 里的 key。
- `data` 可省略；省略等价于一条规则都没上报。若存在必须是数组，每项形如 `{"name": "规则名", "keys": [...]}`。
- `name` 必须命中清单 `rules[].name`，且不得重复；未知或重复直接 400。
- `keys` 中的每个值都必须与 `cases[].keys` 的某个字面量完全相等，任何一个未命中都会导致整个请求 400（没有模糊匹配，也不会静默丢弃）。
- `required: true` 的规则必须上报；`keys` 的长度与元素长度还要满足该规则自己的 `jsonSchema`。

### 4. 响应

- 非流式：返回 OpenAI `chat.completion` 形状的 JSON，由 SDK 模型重新序列化，字段集合受 SDK 模型面限制；不逐字节原样返回，也不回传上游响应头。
- 流式：`text/event-stream`，每条为 `data: {...}`，以 `data: [DONE]` 结束。
- 上游错误：状态码与响应体原样返回。
- 准入拒绝：429，`Retry-After` 为清单值与 `0.8–1.2` 抖动的乘积。
- 校验失败：400，响应体为 `{"error":{"message":...,"type":"invalid_request_error","param":null,"code":null}}`。
- 服务端清单问题（如模板渲染失败）：500。

调试时可开启 `Debug__EndpointsEnabled=true`：

- `GET /debug/manifest`：返回 `project` 与任务 id 列表。
- `POST /debug/rewrite`：输入与线上同形，返回 `{task, data, request}`，展示最终发往上游的请求；不进入准入、不调用上游、不写台账。

## 清单（manifest）

一份清单描述一个项目：任务、模型、工具、规则词表、模板、限制与准入策略。`manifest.json` 与它引用的模板必须位于同一目录，模板只能通过相对路径引用且不能越出目录。

设计思路是通过更换清单，就能切换到另一套业务场景。清单只在启动时加载，改完需重启进程（无热加载）；其中的提示词模板由 Liquid 渲染。

### 最小骨架

```json
{
  "project": "fish-relief",
  "tasks": {
    "dispatch": {
      "availableModels": ["deepseek-flash"],
      "firstUserMessage": "请按照名单发鱼",
      "promptAssembly": { "templateFile": "main.liquid" },
      "tools": [{ "name": "CheckStock", "description": "查询库存", "parameters": { "type": "object" } }]
    }
  },
  "rules": [{
    "name": "entities",
    "strategy": "arrayKeys",
    "required": true,
    "jsonSchema": { "type": "array", "items": { "type": "string", "maxLength": 128 }, "minItems": 1, "maxItems": 12 },
    "cases": [{ "keys": ["全能型大肥鱼"], "liquidObject": { "tags": ["做饭", "家务", "战斗"], "description": "什么都会的大肥鱼，适用于什么都不会的宅宅" } }]
  }],
  "limits": { "maxMessages": 300, "maxBodyBytes": 1048576 },
  "admission": { "maxConcurrentGlobal": 16, "waitSeconds": 5, "maxWaiters": 32, "retryAfterSeconds": 8 },
  "endpoints": ["/v1/chat/completions"]
}
```

Liquid 模板示例（渲染结果即完整 system prompt）：

```liquid
给予大家「鱼之救济」吧！

## 本次名单
{% for item in entities -%}
- {{ item.term }}：{{ item.tags | join: "、" }}，{{ item.description }}
{% endfor -%}
```

### 规则与模板约定

- 每条规则按清单声明序渲染，模板变量名等于 `rules[].name`。
- 规则产出对象数组；每个条目由内核注入 `term`（命中的上报键），并合并该分支的 `liquidObject` 字段。
- 未上报的可选规则绑定为空数组，模板循环自然为空。
- 只使用 Liquid 内置过滤器；渲染输出统一为 LF，并去掉末尾换行。
- 信封里的 `task` 只用于选定任务模板；渲染上下文只包含清单 `rules[]` 声明的变量，其内容由清单分支与已上报键决定。客户端 system 除信封字段外的文本不会进入该上下文，模板也就拿不到。

### 字段速查

<details>
<summary>展开字段表</summary>

| 字段 | 必填 | 约束与说明 |
|---|---|---|
| `project` | 是 | `^[a-z][a-z0-9-]*$`；当前仅作项目标识与调试展示，不参与请求路由 |
| `tasks` | 是 | 至少一个任务，key 为 task id（`^[a-z][a-zA-Z0-9_-]*$`） |
| `tasks[].availableModels` | 是 | 至少一个，必须是上游真实模型名；启动时逐一与上游 `/v1/models` 对账 |
| `tasks[].firstUserMessage` | 是 | 非空；网关覆写首条 user 时使用 |
| `tasks[].promptAssembly.templateFile` | 是 | `.liquid` 文件，相对清单目录，禁止绝对路径与 `..` |
| `tasks[].tools` | 是 | 至少一个；含 `name`、`description`；`parameters` 可省略（省略时不向上游转发该字段） |
| `rules` | 是 | 至少一条；含 `name`、`strategy`、`required`、`jsonSchema`、`cases` |
| `rules[].strategy` | 是 | 当前只支持 `arrayKeys` |
| `rules[].jsonSchema` | 是 | 受控关键词子集，只描述该项负载，不描述信封 |
| `rules[].cases[].keys` | 是 | 字面量词表，同一规则内一个键只能出现在一个分支 |
| `rules[].cases[].liquidObject` | 是 | 并入模板条目的字段，值为字符串或字符串数组 |
| `limits.maxMessages` | 是 | ≥ 2，按 `messages` 数组总长度计算 |
| `limits.maxBodyBytes` | 是 | ≥ 1，请求体字节上限 |
| `admission.maxConcurrentGlobal` | 是 | ≥ 1，进程内全局并发上限 |
| `admission.waitSeconds` | 是 | ≥ 0，拿到名额前的最长等待 |
| `admission.maxWaiters` | 是 | ≥ 0，同时等待的请求上限 |
| `admission.retryAfterSeconds` | 是 | ≥ 1，429 时的基准 `Retry-After` |
| `endpoints` | 是 | 至少一项；必须包含 `/v1/chat/completions`，否则该路径返回 404 |

清单对象均禁止未知字段；`tools[].parameters` 与 `rules[].jsonSchema` 的内容按各自契约校验。字段的权威约束见 [manifests/schema.json](manifests/schema.json)。

</details>

### 升级提示

清单采用严格反序列化：旧版本中已被移除的字段、或任何拼错的字段，都会导致 `清单解析失败` 并拒绝启动。升级时请以仓库中最新的 `manifests/schema.json` 为准清理清单，不要保留多余字段。

可运行的完整示例见 [manifests/demo/manifest.json](manifests/demo/manifest.json) 与 [manifests/demo/main.liquid](manifests/demo/main.liquid)。

## 部署与运维

### 环境变量

配置通过 compose 的 `env_file: .env` 注入；键名对应 `appsettings.json` 的层级，`__` 表示层级分隔。

| 键 | 必填 | 默认值 | 说明与坑 |
|---|---|---|---|
| `Upstream__Endpoint` | 是 | 空 | 必须以 `/chat/completions` 结尾；网关据此推导 `/v1/models` |
| `Upstream__ApiKey` | 是 | 空 | 上游 token；`.env` 含密钥，建议 `600` 权限且不要提交 |
| `Manifest__Path` | 否 | 镜像内 `manifests/demo/manifest.json` | `.env.example` 设为 `/manifests/local/manifest.json`；宿主机 `./manifests` 整目录挂载到 `/manifests`，模板按清单所在目录解析 |
| `Audit__Path` | 否 | `logs/audit-.jsonl` | 台账文件，相对进程工作目录（容器内即 `/app/logs`） |
| `Audit__RetainedFileCountLimit` | 否 | `90` | 台账按天滚动保留份数 |
| `ASPNETCORE_ENVIRONMENT` | 建议 | `Production`（镜像内已设） | 设为 `Development` 会读到 `appsettings.Development.json` 的 `localhost:3000` 与本地清单路径 |
| `TZ` | 否 | 容器默认 UTC | 运行日志与台账的时间戳时区；`.env.example` 用 `Asia/Shanghai` |
| `Debug__EndpointsEnabled` | 否 | `false` | 开启 `/debug/manifest` 与 `/debug/rewrite`；默认不注册，访问为 404 |

### 数据持久化与备份

- **日志卷**：`gateway-logs` 挂载到 `/app/logs`，卷名为 `<project>_gateway-logs`（可用 `docker volume ls` 查看）。
- **运行日志**：`fish-relief-YYYYMMDD.log`，按天滚动，保留 14 份。
- **台账**：`audit-YYYYMMDD.jsonl`（JSON Lines），按天滚动，保留份数由 `Audit__RetainedFileCountLimit` 控制（默认 90）。
- **备份建议**：先定位宿主目录 `docker volume inspect <卷名> --format '{{.Mountpoint}}'`，备份其中的 `audit-*.jsonl`；同时备份清单目录 `manifests/`（含 `manifests/local/`）。运行日志可选备份。
- **恢复方式**：停止容器 → 把备份文件放回卷目录与清单目录 → `docker compose up -d`。日志与台账不参与启动校验，恢复只影响追溯能力。

### 常见问题

启动失败会直接退出，并由 `restart: unless-stopped` 反复重启；先看 `docker compose logs --tail=100 gateway` 中的 `网关启动失败` 异常。

<details>
<summary>展开排障表</summary>

| 日志中的消息 | 原因与处理 |
|---|---|
| `缺少配置 Upstream:Endpoint` / `缺少配置 Upstream:ApiKey` | `.env` 不在 compose 同目录，或键名拼写错误 |
| `Upstream:Endpoint 必须以 /chat/completions 结尾` | 地址缺少后缀 |
| `上游模型探测失败（HTTP 401）` | key 无效或未授权该模型 |
| `上游模型探测失败（HTTP 404）` | 上游地址路径错误 |
| `上游模型探测超时（10 秒）` | 网络不通；出站未显式配置代理 |
| `清单模型未在上游模型清单中暴露：xxx` | 清单 `availableModels` 与上游 `/v1/models` 不一致 |
| `清单解析失败` / `清单校验失败` / `任务 "xxx" 的模板读取失败` | 清单格式、字段或模板文件有问题（模板必须与清单同目录） |
| `缺少配置 Manifest:Path` / `Audit:Path` / 台账路径写入失败 | 挂载路径不存在，或目录不可读/不可写 |

</details>

## 限制与安全模型

**无。很难完全不被薅，实在不行就倒闭吧** 

## 开发

- `src/FishRelief.Gateway`：宿主与 HTTP 端点（入站编解码、错误映射、台账）。
- `src/FishRelief.Kernel`：清单加载与校验、信封解析、提示词组装、准入、上游传输。
- `tests/FishRelief.Tests`：xUnit 测试；测试项目会拷贝 `manifests/**` 供清单与模板测试使用。
- 主要依赖：.NET 8、Fluid.Core（Liquid）、JsonSchema.Net、Microsoft.Extensions.AI 与 OpenAI SDK、Serilog。

## 许可证

本项目采用 [MIT 许可证](LICENSE)。Copyright (c) 2026 FishRelief Contributors。
