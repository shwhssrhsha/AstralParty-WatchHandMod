# AstralParty-WatchHandMod — 变更日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [1.0.0] - 2026-10-17

首个发布版本。**四人实时手牌可用**。

### 新增

- **mod（`mod/`）**
  - 实时显示四名玩家手牌，按 `Q` 显示 / 再按 `Q` 收起
  - **默认不渲染**（零帧率开销）
  - 服务地址与令牌通过 `watch_service.json` 配置，**内置不含任何人的凭据**
  - 配置热加载：改完 json 最多 5 秒生效，不用重启游戏
  - 取不到手牌时在对应位置显示 **`手牌加载失败: <原因>`**

- **服务端（`server/`）**
  - 用第二个账号走官方观战协议，返回整房四人手牌
  - 提供的 HTTP 接口：
    - `/health` 状态查询
    - `/hands?code=` **主接口**
    - `/show?uid=` 查最近对局（含回放号）
    - `/replay?rid=` 拉回放原始数据
    - `/probe` 链路自检
  - 每 5 秒心跳保活
  - sid 缓存（减少重复登录被限流）

### 修复 / 已规避的问题

这些是开发过程中踩到并已解决的坑，记录在此避免回退：

- **缺心跳导致会话假死** —— 不发 `CMDID 5003` 时服务器判定会话已死，之后所有业务请求被**静默丢弃**（表现是"登录成功但一个回应都没有"）
- **`client_ver` 写死** —— 游戏客户端版本变了会 `10012 ClientVerErr`，改为从 `catalog_<ver>.json` 动态读取
- **`device_id` 与游戏客户端冲突** —— 服务端固定设备号会与正在玩的客户端撞车，观战被拒 `11079`；改为按本机推导
- **一个账号只能同时观战一个房间** —— 服务端在加入新房间前先 `WatchExitRoom(5195)` 退出旧的
- **一次加入失败就永久卡死** —— 原逻辑把失败记录留在会话表里，既不再重试也不刷新，只能重启服务；改为按 10 秒节流自动重试
- **连接断开后不自愈** —— 增加断线检测与自动重连（30 秒冷却），live 连接死掉时重建并重新登录
- **`Environment.TickCount` 在 IL2CPP 下不增长** —— 用它算"数据新鲜度"会永远判定过期，界面全是"已停止更新"；改用帧计数
- **`HttpWebRequest.Timeout` 在 IL2CPP 下不生效** —— 请求线程会永久挂起，`busy` 卡在 1；增加 `Abort()` 看门狗强制中断
- **FairyGUI `GTextField` 没有 `fontSize` 属性** —— 必须取现有 `textFormat` 改 size/color 再赋回，`new TextFormat()` 会清空 font 导致文字不渲染
- **`DateTimeOffset.ToUnixTimeSeconds` 被 IL2CPP 裁剪** —— 调用会抛 `MissingMethodException`，且若包在空 catch 里会完全静默

### 已知限制

- **本地拿不到队友实时手牌** —— `Hero.Cards` 与手牌容器对队友是快照，不随出牌更新（实测相隔 32 秒完全一致）。**必须依赖观战**，这是本方案存在的前提
- **一个观战账号只能同时看一个房间** —— 一个服务实例只够一个人用；多人需多开实例
- **观战号不能在这局里** —— 否则服务器回 `11002 RoomPlayerAlreadyJoin`
- **需要 PVE 类对局** —— 观战码只在有观战的对局中存在
- **依赖 CesiumLoader** —— 本项目不附带 mod 加载器

### 许可与第三方

- 原创代码：**GNU AGPL-3.0**（见 `LICENSE`）
- **`astral/` 与 `assets/`（服务端依赖）**：来自 [AstralParty_Dex](https://github.com/ZtyanCrany/AstralParty_Dex)，
  **MIT**，**本仓库不重复分发** —— 请自行获取，其 MIT 声明随该项目提供
- **CesiumLoader**（AGPL-3.0）与 **CesiumLoader.SDK**（MIT）：**不随本项目分发**，需自行安装
