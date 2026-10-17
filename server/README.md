# astral-watch-service

给《Astral Party / 吉星派对》的 **WatchHandMod** 提供"四人实时手牌"查询的独立服务端。

- **输入**：一个观战码
- **输出**：四人当前手牌的 JSON
- **原理**：用**第二个账号**登录游戏服务器，走官方"观战"协议（`WatchJoinRoom` / `WatchRefreshRoomState`），
  把服务器下发给观战者的整房快照解析出来。

> **为什么要服务端**：游戏客户端本地拿不到队友的**实时**手牌
> （`heroCards` / `cardContainer` 只有快照，不随出牌更新），
> 唯一实时来源是服务器发给**观战者**的房间状态。所以必须有一个独立进程用第二个账号去看。

---

## 一、你要准备什么

| 项 | 说明 |
|---|---|
| **一台能长期在线的机器** | Windows / Linux 都行。它要能访问游戏服务器 `101.132.186.71:8800` |
| **Python 3.9+** | 实测用 3.13 |
| **一个专门用来观战的游戏账号** | **手机号 + 密码**。不要用你自己的主号（主号观战自己的房间会被拒）|
| **自己定一个随机 token** | 用来防止别人调用你的服务。随便一串 32 位十六进制即可 |

---

## 二、部署步骤

### 1. 装依赖

```bash
pip install protobuf certifi
```

> `certifi` 是给 HTTPS 拉回放用的。系统证书链完整的话不装也行，但装了更省事。

### 2. 准备依赖与目录

**本仓库的 `server/` 目录不包含 `astral/` 与 `assets/`** —— 它们来自第三方项目
[AstralParty_Dex](https://github.com/ZtyanCrany/AstralParty_Dex)（MIT），本项目不重复分发。

**二选一**：

- **A. 用 Releases 的 zip**（推荐）：下载后 `astral/` 和 `assets/` 已经就位，直接跳到第 3 步
- **B. 自己补依赖**：克隆 AstralParty_Dex，把它的 `astral/` 和 `assets/` 复制到这里

补齐后目录结构应该是：

```
astral-watch-service/
├── astral_watch_service.py
├── astral/                 从 AstralParty_Dex 获取（MIT，其 LICENSE 一并保留）
├── assets/
│   ├── data/               卡牌/英雄/怪物等数据（来自 AstralParty_Dex）
│   └── proto/              protobuf 描述符（来自 AstralParty_Dex）
├── requirements.txt
└── README.md
```

> **`astral/` 和 `assets/` 必须与 `astral_watch_service.py` 同级**，否则启动会报找不到模块/描述符。

### 3. 启动

```bash
python astral_watch_service.py \
  --tel <你的观战小号手机号> \
  --password <你的观战小号密码> \
  --token <你自己定的随机串> \
  --http-port 8765 \
  --listen 0.0.0.0
```

启动成功的标志（日志里看到这几行）：

```
sid 就绪 (len=96)
连接游戏服 101.132.186.71:8800 (clientVer=3.3.0, xor=none, extra=bn) ...
游戏服登录成功 sessionId=xxxxx
HTTP 服务已启动: http://0.0.0.0:8765   token=xxxx
```

### 4. 验证

```bash
curl "http://127.0.0.1:8765/health?token=<你的token>"
# 期望: {"ok": true, "login": "ok", "sessions": {}, "wanted": [], "last_error": ""}
```

### 5. 让它在后台常驻

**Windows**（计划任务，重启后自动起）：

```cmd
schtasks /create /tn AstralWatch /tr "C:\path\to\start.cmd" /sc onstart /ru SYSTEM /rl HIGHEST
```

`start.cmd` 内容（把参数换成你自己的）：

```cmd
@echo off
cd /d C:\path\to\astral-watch-service
"C:\Program Files\Python313\python.exe" astral_watch_service.py --tel <手机号> --password <密码> --token <token> --http-port 8765 --listen 0.0.0.0 >> service.log 2>&1
```

**Linux**：用 `systemd` 或 `nohup`、`tmux` 任选。

### 6. 放行端口

- **云厂商控制台的防火墙**：放行 TCP `8765`
- **系统防火墙**（Windows）：

```powershell
New-NetFirewallRule -DisplayName "astral-watch 8765" -Direction Inbound -Protocol TCP -LocalPort 8765 -Action Allow
```

---

## 三、配置 WatchHandMod

在 `AstralParty_ModLoader\mods\WatchHandMod\` 里新建 `watch_service.json`：

```json
{
  "url": "http://<你的服务器地址>:8765",
  "token": "<你自己定的随机串>"
}
```

- **记事本直接编辑**，UTF-8 保存
- 改完**不用重启游戏**，mod 每 5 秒检查一次
- 没有这个文件 = 不启用（界面只显示"手牌加载失败: 服务未配置"）

进局后**按 Q** 显示队友手牌，再按 Q 收起。

---

## 四、HTTP 接口

所有接口都要带 `token` 参数（和启动时 `--token` 一致）。

| 接口 | 说明 |
|---|---|
| `GET /health?token=` | 登录状态、当前观战会话、最近错误 |
| `GET /hands?code=<观战码>&token=` | **主接口**，返回四人手牌 |
| `GET /show?uid=<UID>&token=` | 查该玩家最近对局列表（含 `replayId`）|
| `GET /replay?rid=<回放号>&token=` | 拉回放原始数据（base64）|
| `GET /probe?token=` | 发一次 `GetPlayerSimple(5263)` 做链路自检 |

**`/hands` 返回示例**：

```json
{
  "ok": true,
  "code": "C1F9B12",
  "room_id": 397906,
  "room_server_id": 203,
  "age": 2.0,
  "err": "",
  "players": [
    {"id": 2838867, "nick": "水獭", "is_bot": false, "count": 3, "cards": [10003, 10004, 10008]},
    {"id": 1649158, "nick": "玩家A", "is_bot": false, "count": 7, "cards": [10004, 20005, 20002, 21012, 21002, 10004, 10010]}
  ]
}
```

- `age` = 距上次成功刷新的秒数
- `ok:false` 时看 `err`；`pending:true` 表示服务正在加入房间，2 秒后重试即可
- 第一次请求可能返回 `pending`，属正常

---

## 五、常见问题

### `err=11079`（RoomNotRunning）

服务能认出这个房间但拒绝加入，实测有三种成因：

1. **同一个设备号被占** —— 如果你在别处跑过同一个账号/设备号，会冲突。
   本服务默认自己推导 device_id，正常情况下不会踩。
2. **同时观战了多个房间** —— 服务器**一个账号只允许一个观战会话**。
   本服务已处理（加入新房间前先退旧的）。
3. **上次加入失败的记录卡住** —— 本服务会每 10 秒自动重试，日志里能看到
   `加入观战失败 ... (将在 10s 后重试)`。实在不行重启服务。

### `err=11001`（RoomNotExist）

对局已经结束了。观战码只在**房间存活期间**有效。

### `err=11002`（RoomPlayerAlreadyJoin）

**你用的账号就在这个房间里**。换一个不在这局里的账号来观战。

### 登录被拒 `err=10012`（ClientVerErr）

游戏客户端版本变了。服务会自动从 `catalog_<ver>.json` 读版本；如果读不到，
用 `--client-ver 3.3.0` 手动指定（换版本时改这个值）。

### 请求全部超时、但登录成功

**心跳没发**。游戏服务器靠 `CMDID 5003` 心跳判断会话存活，不发的话
**之后所有业务请求会被静默丢弃**。本服务已内置每 5 秒心跳，改动代码时别把它删了。

### 拿不到手牌 / 一直是 pending

看 `/health` 里的 `sessions` 和 `err`。若 `err` 里有 `11079` 见上；
若是空且一直 `pending`，说明加入请求还没返回，通常是网络到 `101.132.186.71:8800` 不通。

---

## 六、关于观战账号

- **必须是与房间无关的账号**（不能在这局里，否则 `11002`）
- **同一时刻只能观战一个房间** —— 所以**一个服务实例服务一个玩家**。
  要多个人同时用，就多开实例（每个实例一个观战账号 + 一个端口 + 一个 token）
- 服务会把 `sid` 缓存在脚本同目录的 `.astral_watch_sid.json`，减少重复登录
  （服务器对反复登有限流）。删除该文件即强制重新登录。
