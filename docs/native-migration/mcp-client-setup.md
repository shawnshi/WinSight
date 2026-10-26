# WinSight MCP 客户端接入说明（本地个人使用）

WinSight 的 MCP 桥是**本机自用**组件：默认随应用可用，不需要维护授权，也不对外网开放。

## 1. 位置

| 项 | 路径 |
|---|---|
| 桥可执行文件 | `<install>/app/bridge/winsight-mcp.exe`（安装后：`%LOCALAPPDATA%\Programs\WinSight Native Preview\install\deployment\versions\<manifest>\app\bridge\`） |
| 索引库 | `%LOCALAPPDATA%\Programs\WinSight Native Preview\data\index\index.sqlite` |
| 维护请求投递 | `…\data\bridge-jobs\inbox\`（桥写入，应用领取） |

## 2. 传输一：stdio（推荐，默认）

客户端配置（在 Settings → **Connect an MCP client** 里 Preview/Apply 会自动生成）：

```json
{
  "mcpServers": {
    "winsight": {
      "command": "C:\\Users\\<你>\\AppData\\Local\\Programs\\WinSight Native Preview\\install\\deployment\\versions\\<manifest>\\app\\bridge\\winsight-mcp.exe",
      "args": ["--stdio"]
    }
  }
}
```

- stdout 只有 JSON-RPC，日志在 stderr；
- 单条消息上限 1 MiB；
- 不需要令牌（本机进程间，只有你的账户能启动它）。

## 3. 传输二：环回 HTTP（需要显式开启）

```powershell
$env:WINSIGHT_MCP_TOKEN = '<至少 32 字符的随机串>'
& '<...>\app\bridge\winsight-mcp.exe' --index '<...>\data\index\index.sqlite' --http-port 18771
```

客户端配置里写 URL 与请求头（**不要**把令牌写进配置文件本身）：

```json
{
  "mcpServers": {
    "winsight": {
      "url": "http://127.0.0.1:18771/mcp/",
      "headers": { "Authorization": "Bearer ${WINSIGHT_MCP_TOKEN}" }
    }
  }
}
```

行为：
- 只监听 `127.0.0.1`；
- 无令牌 → `401`；`Origin` 非环回 → `403`；
- 令牌走环境变量，工具栏/日志/配置里都不落明文。

## 4. 工具

| 工具 | 说明 |
|---|---|
| `search_files` | `query`（≤4096 字符）、`topK` 1–20、可选 `mode`/`root`；返回文件级命中、真实分数、匹配类型、截断摘要、页码 |
| `read_file_context` | 只能读**命中引用过**的文件；越权 `excluded_file`，版本不符 `stale_reference` |
| `get_index_status` | 模态、文件/片段计数、状态分布、`lexical_degraded`、`http` 状态；不暴露库路径与全部监控根 |
| `index_directory` | 投递一条**维护请求**并返回 `job_id`；桥自己不写索引 |

## 5. 维护请求（`index_directory`）怎么真正生效

1. 客户端调用 `index_directory` → 桥在 `data\bridge-jobs\inbox\` 落一条请求；
2. 应用 Settings → **MCP maintenance requests** → **Drain requests**；
3. 应用检查路径是否在已授权的监控目录内：
   - 在范围内 → `accepted: true`，随后交给索引队列；
   - 不在范围内 → `accepted: false` + 原因（`refused: the path is outside the authorized folders`）；
4. 每条结果落在 `data\bridge-jobs\completed\<job>.result.json`，拒绝也留证据。

## 6. 模型与设备

Settings → **Models and devices** 可查看当前嵌入模型（名称/修订/profile/实际运行设备）与 OCR 模型（引擎版本/profile/所在进程）。嵌入默认走 CPU（加速器必须通过数值与计算节点证据才会被采用）；OCR 恒在独立进程内运行。

## 7. 已知边界

- HTTP 端点由客户端自行配置；Settings 里的配置助手目前只注入 stdio 条目。
- 桥的自动化夹具覆盖 stdio 与管道（18 项）；HTTP 与维护流的手工验证已做，但尚未固化成夹具断言。
- MCP 桥不做鉴权模型（本机单用户场景），HTTP 的 Bearer 令牌仍必须提供。
