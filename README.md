# AstralParty-WatchHandMod

给《Astral Party / 吉星派对》（Steam 国服）用的模组：**实时显示四名玩家的手牌**。

- **许可**：**AGPL-3.0**（见 `LICENSE`）
- **作者**：**shwhssrhsha**（<https://github.com/shwhssrhsha>）
- **开发方式**：**Vibe Coding** —— 本项目在 AI 编程助手（DeepSeek / DSH Harness）深度参与下完成，
  由作者负责需求、方向判断与实机验证
- **本包不含任何人的服务器地址、令牌或账号**

---

## 这是什么

游戏客户端**本身拿不到队友的实时手牌** —— `Hero.Cards` 和手牌容器里的队友牌是**快照、不随出牌更新**（实测相隔 32 秒完全一致）。

唯一的实时来源是**服务器发给「观战者」的房间状态**。所以这套东西由两部分组成：

```
┌─────────────┐                    ┌──────────────────┐
│ WatchHandMod │ ──HTTP 查询────▶  │ astral-watch-service│ ──观战协议──▶ 游戏服务器
│  （游戏内 mod）│ ◀──四人手牌 JSON── │  （你自己搭的服务端）│ ◀──房间快照──
└─────────────┘                    └──────────────────┘
```

- **mod**：读当前对局的观战码，每 2 秒问一次服务，把结果画在界面上
- **服务端**：用一个**第二账号**登录游戏，走官方观战协议，把整房手牌快照吐出来

**两个都是你自己部署的**，不依赖任何第三方服务。

---

## 目录结构

```
WatchHandMod/
├── LICENSE                      AGPL-3.0 全文
├── README.md                    本文件
├── CHANGELOG.md                 版本历史与已规避的坑
├── mod/                         mod 的源码
│   ├── ModEntry.cs
│   ├── CardTable.cs
│   └── WatchHandMod.csproj
└── server/                      服务端（Python）
    ├── astral_watch_service.py  ← 本项目原创
    ├── requirements.txt
    └── README.md                ← 服务端部署细节看这个
```

> **注意**：`server/` 里**不含** `astral/` 与 `assets/`。它们是第三方项目
> [AstralParty_Dex](https://github.com/ZtyanCrany/AstralParty_Dex)（MIT）的代码与资源，
> 本项目不重复分发。**运行服务端前请按 `server/README.md` 的说明自行获取这两项。**
> 编译好的成品（含全部依赖）请到本仓库的 **Releases** 下载。

---

## 快速开始

### 第一步：搭服务端

**你需要**：一台能联网的机器（Windows/Linux 都行）、**一个专门用来观战的游戏小号**（不要用你的主号）、
以及 **AstralParty_Dex 的 `astral/` 与 `assets/` 目录**（见下方说明）。

**获取依赖**（二选一）：

- **方式 A（推荐）**：直接下载本仓库 **Releases** 里的 zip —— 里面已经带好 `astral/` 和 `assets/`，解压即用
- **方式 B（从源码跑）**：自己获取 [AstralParty_Dex](https://github.com/ZtyanCrany/AstralParty_Dex)，
  把它的 `astral/` 和 `assets/` 放到 `server/` 下：

```
server/
├── astral_watch_service.py
├── astral/     ← 从 AstralParty_Dex 复制
├── assets/     ← 从 AstralParty_Dex 复制
├── requirements.txt
└── README.md
```

```bash
pip install -r server/requirements.txt

python server/astral_watch_service.py \
  --tel <观战小号的手机号> \
  --password <观战小号的密码> \
  --token <你自己随机生成的一串> \
  --http-port 8765 --listen 0.0.0.0
```

看到这几行就成功了：

```
sid 就绪 (len=96)
连接游戏服 101.132.186.71:8800 (clientVer=3.3.0, xor=none, extra=bn) ...
游戏服登录成功 sessionId=xxxxx
HTTP 服务已启动: http://0.0.0.0:8765   token=xxxx
```

**更多细节**（开机自启、防火墙、常见错误码）见 `server/README.md`。

### 第二步：配置 mod

1. **装 mod 加载器**：本项目**不附带** CesiumLoader，请自行从
   <https://github.com/higashitaniyume/CesiumLoader> 安装（它是 AGPL-3.0）
2. **把 `mod/bin/WatchHandMod.dll` 放进**：
   ```
   AstralParty_ModLoader\mods\WatchHandMod\WatchHandMod.dll
   ```
3. **新建配置文件** `AstralParty_ModLoader\mods\WatchHandMod\watch_service.json`：
   ```json
   {
     "url": "http://<你的服务端地址>:8765",
     "token": "<你启动服务时用的那个 token>"
   }
   ```
   - **记事本直接编辑，UTF-8 保存**
   - **改完不用重启游戏**（mod 每 5 秒检查一次配置）

### 第三步：使用

**进局后按 `Q`** 显示队友手牌，**再按 `Q`** 收起。

- **默认不显示**（零渲染开销，不影响帧率）
- 只有 PVE 类对局才有观战码
- 未配置或服务不可用时，界面显示 **`手牌加载失败: <原因>`**，不会显示陈旧数据冒充实时

---

## 重要限制

| 限制 | 说明 |
|---|---|
| **一个观战账号只能同时看一个房间** | 服务端已处理（换房时先退出旧的），但**一个服务实例只够一个人用**。多人使用需要**多开实例**（每个实例一个观战号 + 一个端口 + 一个 token）|
| **观战号不能在这局里** | 否则服务器回 `11002 RoomPlayerAlreadyJoin` |
| **需要 PVE 类对局** | 观战码只在有观战的对局中存在 |
| **依赖 mod 加载器** | 需要 CesiumLoader（AGPL-3.0，自行安装）|

---

## 许可与致谢

**本项目的原创代码**（mod 的 C# 代码、服务端的 Python 代码）以 **GNU AGPL-3.0** 发布 —— 见 `LICENSE`。

**第三方组件**：

| 组件 | 许可 | 说明 |
|---|---|---|
| **AstralParty_Dex 的 `astral/` 包** | **MIT** | 服务端的协议客户端与数据模型，来自 `ZtyanCrany/AstralParty_Dex`（Copyright (c) 2026 Nemophila & ZtyanCrany）。按 MIT 要求，其版权声明与许可文本随本包提供，见 `server/astral/LICENSE` |
| **CesiumLoader** | **AGPL-3.0** | **不随本包分发**，请自行安装 |
| **CesiumLoader.SDK** | **MIT** | mod 编译时引用的 SDK（不随本包分发）|

**协议实现参考**：观战流程的协议细节来自对游戏客户端与上述开源项目的研究，**未复制 CesiumLoader 内置模组的代码**。

---

## 免责声明

- 本项目**与游戏开发商、发行商无关**
- 使用第三方账号观战、修改游戏客户端可能违反游戏服务条款，**风险自负**
- 作者不对因使用本软件导致的任何后果负责（见 AGPL-3.0 第 15、16 条）
