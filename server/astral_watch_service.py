"""Spectator service: watch a room with a SECOND account and serve the hands over HTTP.

Why a service: the game server refuses to let a room member spectate their own
match (errId=11002 RoomPlayerAlreadyJoin) and only ever sends your own hand while
you play. A spectator account is not a room member, so the server hands it the
whole room snapshot -- every player's cards. This replaces the dead third-party
service that HandViewerMod used to call.

Run (local first):
    python astral_watch_service.py --tel 13800000000 --sms 123456 --port 8765
    # it prints a token; use it in requests
    curl "http://127.0.0.1:8765/health"
    curl "http://127.0.0.1:8765/hands?code=BFE6B38&token=<TOKEN>"

Endpoints:
    GET /                     -> short help
    GET /health               -> login / session / error state
    GET /hands?code=<CODE>    -> four players' hands as JSON (cached, refreshed in background)

Design notes:
  * All game-network traffic stays on ONE worker thread (AstralClient's send/recv
    is not built for concurrent callers); HTTP handlers only read the cache.
  * Sid expires eventually -- /health reports it and the process must be restarted
    with a fresh SMS code (or extended later with stored credentials).
  * Credentials are never written to disk.
"""

from __future__ import annotations

import argparse
import json
import os
import secrets
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse, parse_qs

_HERE = Path(__file__).resolve().parent
# 部署包结构: astral_watch_service.py 与 astral/ 同目录
# 开发环境结构: tools/astral_watch_service.py + refs/dex_src/AstralParty_Dex-main/astral
if (_HERE / "astral").is_dir():
    DEX_ROOT_DEFAULT = _HERE
else:
    DEX_ROOT_DEFAULT = _HERE.parent / "refs" / "dex_src" / "AstralParty_Dex-main"
DEFAULT_HOST = "101.132.186.71"
DEFAULT_PORT = 8800

CMD_WATCH_JOIN = 5191
CMD_WATCH_REFRESH = 5193
CMD_WATCH_EXIT = 5195           # 退出观战 —— 服务器一个账号只允许一个观战会话
JOIN_RETRY_SEC = 10.0           # v42: 加入失败后的重试间隔(秒)
RECONNECT_COOLDOWN = 30.0       # v1.0: 断线重连的冷却(秒), 避免频繁重新登录被限流
CMD_GET_SHOW_PLAYER = 5153      # 查玩家展示信息(含最近对局的 replayId)
REPLAY_HOST = "https://sereplaycn.feimogames.com/prod/%s"   # 国服回放服务器


def log(msg: str):
    print("[%s] %s" % (time.strftime("%H:%M:%S"), msg), flush=True)


# 放工作区 —— 沙箱只允许写工作区，写用户目录会被静默拒绝（实测缓存没落盘）
SID_CACHE = Path(__file__).resolve().parent / ".astral_watch_sid.json"   # 发布版: 存脚本同目录, 不写死任何人的路径


def load_sid(tel: str):
    """复用上次登录拿到的 sid —— 服务器对反复握手会限流(实测重复登录回 err=10000)。"""
    try:
        d = json.loads(SID_CACHE.read_text(encoding="utf-8"))
        if str(d.get("tel")) == str(tel) and d.get("sid"):
            return str(d["sid"]), float(d.get("ts") or 0)
    except Exception:
        pass
    return "", 0.0


def save_sid(tel: str, sid: str):
    try:
        SID_CACHE.write_text(json.dumps({"tel": str(tel), "sid": str(sid),
                                         "ts": time.time()}, ensure_ascii=False),
                             encoding="utf-8")
        os.chmod(SID_CACHE, 0o600)
    except Exception:
        pass


def detect_client_ver() -> str:
    """当前客户端版本号 —— 服务器用它校验握手（不对就回 err=10012 ClientVerErr）。

    依据：Addressables 目录文件名 catalog_<版本>.json（游戏更新时会换新版本号）。
    取不到就退回已知的最新版本。
    """
    import re
    base = (Path.home() / "AppData/LocalLow/feimo/AstralParty_CN"
            / "com.unity.addressables")
    vers = []
    try:
        for p in base.glob("catalog_*.json"):
            m = re.match(r"catalog_(\d+(?:\.\d+)+)\.json$", p.name)
            if m:
                vers.append(tuple(int(x) for x in m.group(1).split(".")))
    except Exception:
        pass
    if vers:
        return ".".join(str(x) for x in max(vers))
    return "3.3.0"


class WatchService:
    def __init__(self, args):
        self.args = args
        self.client = None
        self.proto = None
        self.lock = threading.Lock()
        self.sessions = {}          # code -> session dict
        self.wanted = set()         # codes the mod asked about
        self.login_state = "starting"
        self.last_error = ""
        self.stop = False
        # 上次发游戏心跳(5003)的时间 —— 停发会被服务器判定会话已死
        self._last_beat = 0.0
        self._beat_fails = 0          # v1.0: 心跳连续失败次数
        self._last_reconnect = 0.0    # v1.0: 上次重连时刻
        self.login_data = ""      # ConnectS2C.data(可能含游戏服列表)

    # ── 登录 ──
    def login(self):
        sid = ""
        if not self.args.no_sid_cache:
            cached, ts = load_sid(self.args.tel)
            if cached and (time.time() - ts) < self.args.sid_ttl:
                sid = cached
                log("复用缓存 sid (age=%.0f 秒, 免重新握手)" % (time.time() - ts))

        if not sid:
            from astral import sdk_login
            log("SDK init ...")
            sdk_login.sdk_init(self.args.channel)
            if self.args.password:
                # 不用 sdk_login.password_login —— 它用 str(sid).startswith('04') 猜"是不是
                # authorize_code"，access_token 恰好以 04 开头时会把 access_token 当成
                # authorize_code 交给游戏服(实测 err=10000, sid=116 字符)。
                # 这里改成：login_type=18 拿 access_token 后**强制**再走一次 login_type=2 换票。
                log("authorize (login_type=18 手机号+密码) ...")
                sid, r = sdk_login._submit(self.args.tel, sdk_login.pwd_hash(self.args.password),
                                           "18", self.args.channel, True, "password")
                good, msg = sdk_login._check(r)
                if not good:
                    self.login_state = "sdk-login-failed"
                    raise SystemExit("SDK 密码登录失败: %s"
                                     % (msg or json.dumps(r, ensure_ascii=False)[:300]))
                tok = sdk_login.pick(r, "content.access_token", "content.accessToken",
                                     "data.access_token", "data.accessToken", "accessToken")
                if tok:
                    log("access_token 已拿到 (len=%d), 强制走 login_type=2 换 authorize_code"
                        % len(str(tok)))
                    sid, r = sdk_login.token_login(tok, self.args.tel, self.args.channel,
                                                   do_init=True)
                    good, msg = sdk_login._check(r)
                    if not good:
                        self.login_state = "sdk-login-failed"
                        raise SystemExit("换票失败: %s"
                                         % (msg or json.dumps(r, ensure_ascii=False)[:300]))
                log("sid 就绪 (len=%d)" % len(str(sid)))
            else:
                log("authorize (login_type=3 短信验证码) ...")
                r = sdk_login.authorize(self.args.tel, self.args.sms,
                                        login_type=3, channel=self.args.channel)
                good, msg = sdk_login._check(r)
                if not good:
                    self.login_state = "sdk-login-failed"
                    raise SystemExit("SDK 登录失败: %s"
                                     % (msg or json.dumps(r, ensure_ascii=False)))
                sid = ""
                for path in ("content.authorize_code", "authorize_code",
                             "content.access_token", "data.access_token"):
                    try:
                        sid = sdk_login.pick(r, path) or ""
                    except Exception:
                        sid = ""
                    if sid:
                        log("sid 取自 %s (len=%d)" % (path, len(str(sid))))
                        break
                if not sid:
                    raise SystemExit("没找到 sid: %s"
                                     % json.dumps(r, ensure_ascii=False)[:300])

        from astral.client import AstralClient
        from astral import proto_loader
        from astral import frame as aframe
        from astral.sdk_login import DEFAULT_DEVICE_ID, DEFAULT_EXTRA
        self.proto = proto_loader
        ver = self.args.client_ver or detect_client_ver()
        # device_id 按机器名推导, 换机器(Linux 云服务器)会变; 用 --device-id 固定成客户端那台的
        device_id = self.args.device_id or DEFAULT_DEVICE_ID
        xor = {"none": aframe.XOR_NONE, "body": aframe.XOR_BODY,
               "full": aframe.XOR_FULL}.get(self.args.xor, aframe.XOR_NONE)
        log("连接游戏服 %s:%d (clientVer=%s, xor=%s, extra=%s) ..."
            % (self.args.host, self.args.port, ver, self.args.xor, DEFAULT_EXTRA))
        self.client = AstralClient(self.args.host, self.args.port, xor_mode=xor).connect()
        # device_id / extra 必须带上 —— Dex 的 GUI 一直传(DEFAULT_DEVICE_ID + 'bn'),
        # 它的命令行入口和早期实现没传, 实测那会导致后续请求收不到任何回复。
        s2c = self.client.login_china(str(sid), device_id=device_id,
                                      extra=DEFAULT_EXTRA, client_ver=ver)
        log("游戏服登录成功 sessionId=%s (device=%s…)" % (s2c.sessionId, str(device_id)[:12]))
        # 登录响应里可能带"游戏服列表"(观战要连房间所在的那台服) —— 留证据
        try:
            self.login_data = s2c.data or ""
            log("ConnectS2C.data = %s" % (self.login_data[:300] if self.login_data else "<空>"))
        except Exception as exc:
            self.login_data = ""
            log("读 ConnectS2C.data 失败: %s" % exc)
        save_sid(self.args.tel, sid)      # 游戏服认了这个 sid 才缓存
        self.login_state = "ok"

    # ── 观战 ──
    def join(self, code: str):
        try:
            self.client.heartbeat()      # 查询前补一次心跳(照 Dex 的做法)
        except Exception:
            pass
        m = self.proto.new_msg("protocol.WatchJoinRoomC2S")
        m.watchCode = code
        rsp = self.client.send(CMD_WATCH_JOIN, m, wait=15.0)
        err = rsp.get("err")
        if err:
            return None, "join-rejected err=%s" % err
        join = self.proto.new_msg("protocol.WatchJoinRoomS2C")
        join.ParseFromString(rsp["body"])
        sess = {
            "code": code,
            "room_id": int(getattr(join, "room_id", 0) or 0),
            "room_server_id": int(getattr(join, "room_server_id", 0) or 0),
            "players": self.players_of(getattr(join, "players", None)),
            "updated": time.time(),
            "err": "",
            "joins": 1,
        }
        return sess, ""

    def exit_other_sessions(self, keep: str):
        """v41: 服务器**一个账号只允许一个观战会话**。

        症状(实测): 换局后新房间一直被拒 err=11079 RoomNotRunning, 必须重启服务才恢复
        —— 因为旧会话还挂在服务器上。所以在加入新房间前, 先把其它会话显式退掉。
        """
        with self.lock:
            stale = [c for c in self.sessions if c != keep]
        for c in stale:
            try:
                self.client.heartbeat()
                m = self.proto.new_msg("protocol.WatchExitRoomC2S")
                rsp = self.client.send(CMD_WATCH_EXIT, m, wait=6.0)
                log("已退出旧观战会话 code=%s err=%s" % (c, rsp.get("err")))
            except Exception as exc:
                log("退出旧观战会话 code=%s 异常: %s" % (c, exc))
            with self.lock:
                self.sessions.pop(c, None)
                self.wanted.discard(c)

    def refresh(self, sess: dict):
        try:
            self.client.heartbeat()      # 查询前补一次心跳
        except Exception:
            pass
        m = self.proto.new_msg("protocol.WatchRefreshRoomStateC2S")
        m.room_id = sess["room_id"]
        m.room_server_id = sess["room_server_id"]
        rsp = self.client.send(CMD_WATCH_REFRESH, m, wait=10.0)
        if rsp.get("err"):
            return "refresh-rejected err=%s" % rsp.get("err")
        s2 = self.proto.new_msg("protocol.WatchRefreshRoomStateS2C")
        s2.ParseFromString(rsp["body"])
        room = getattr(s2, "room", None)
        players = getattr(room, "players", None) if room is not None else None
        if players is None:
            return "refresh-no-room"
        sess["players"] = self.players_of(players)
        sess["updated"] = time.time()
        sess["err"] = ""
        return ""

    @staticmethod
    def players_of(players) -> list:
        out = []
        try:
            for p in players or []:
                hero = getattr(p, "hero", None)
                cards = []
                for c in (getattr(hero, "cards", None) or []):
                    cards.append(int(getattr(c, "cardId", 0) or 0))
                out.append({
                    "id": int(getattr(p, "id", 0) or 0),
                    "nick": str(getattr(p, "nick", "") or ""),
                    "is_bot": bool(getattr(p, "isBot", False)),
                    "count": len(cards),
                    "cards": cards,
                })
        except Exception as exc:
            log("players_of 异常: %s" % exc)
        return out

    # ── 按 UID 查最近对局(含 replayId) ──
    def show(self, uid):
        """GetShowPlayer(5153): 返回该玩家最近对局列表, 每局带 replayId。

        QQ 上那些查询 bot 就是走这条路: UID -> replayId -> 拉回放 -> 四人手牌。
        不需要观战码、也不挑模式(训练 9 也行)。
        """
        self.client.heartbeat()
        m = self.proto.new_msg("protocol.GetShowPlayerC2S")
        m.player_id = int(uid)
        rsp = self.client.send(CMD_GET_SHOW_PLAYER, m, wait=15.0)
        if rsp.get("err"):
            return {"ok": False, "uid": int(uid), "err": "err=%s" % rsp.get("err")}
        out = self.proto.new_msg("protocol.GetShowPlayerS2C")
        out.ParseFromString(rsp["body"])
        sp = out.showData
        recs = []
        for r in list(getattr(sp, "record", []) or []):
            recs.append({
                "time": int(getattr(r, "time", 0) or 0),
                "rank": int(getattr(r, "rank", 0) or 0),
                "hero_id": int(getattr(r, "heroId", 0) or 0),
                "map_type": int(getattr(r, "mapType", 0) or 0),
                "index": int(getattr(r, "index", 0) or 0),
                "replay_id": str(getattr(r, "replayId", "") or ""),
            })
        return {"ok": True, "uid": int(uid), "player_id": int(getattr(sp, "player_id", 0) or 0),
                "record": recs}

    # ── 拉回放原始数据 ──
    def replay(self, rid):
        """从回放服务器抓一局的完整数据(含四人逐轮手牌)。"""
        import ssl
        import urllib.request
        rid = str(rid).strip()
        if not rid:
            return {"ok": False, "err": "rid 为空"}
        url = REPLAY_HOST % rid
        try:
            ctx = None
            try:
                import certifi
                ctx = ssl.create_default_context(cafile=certifi.where())
            except Exception:
                ctx = ssl.create_default_context()
            req = urllib.request.Request(url, headers={"User-Agent": "astral-watch"})
            with urllib.request.urlopen(req, timeout=20, context=ctx) as resp:
                raw = resp.read()
            log("回放 %s: %d 字节" % (rid, len(raw)))
            return {"ok": True, "rid": rid, "url": url, "bytes": len(raw),
                    "body_b64": __import__("base64").b64encode(raw).decode("ascii")}
        except Exception as exc:
            return {"ok": False, "rid": rid, "url": url,
                    "err": "%s: %s" % (type(exc).__name__, str(exc)[:200])}

    # ── 探针：区分"链路/加密问题"与"观战消息问题" ──
    def probe(self, xor=None):
        """发一个已知可用的查询做对照, 再发观战请求, 看回包差异。
        xor 可传 none/body/full —— 动态切换混淆模式, 省得反复重启握手。"""
        if xor:
            from astral import frame as aframe
            self.client.xor_mode = {"none": aframe.XOR_NONE, "body": aframe.XOR_BODY,
                                    "full": aframe.XOR_FULL}.get(xor, aframe.XOR_NONE)
            log("probe: 已切换 xor=%s" % xor)
        out = {"xor": getattr(self.client, "xor_mode", "?")}
        try:
            m = self.proto.new_msg("protocol.GetPlayerSimpleC2S")
            m.player_id = int(self.args.probe_uid)
            rsp = self.client.send(5263, m, wait=8.0)
            out["GetPlayerSimple(5263)"] = {"err": rsp.get("err"),
                                            "body_len": len(rsp.get("body") or b"")}
        except Exception as exc:
            out["GetPlayerSimple(5263)"] = {"error": str(exc)[:200]}

        code = self.args.probe_watch
        if code:
            try:
                with self.lock:
                    self.wanted.add(code)
                    self.sessions.pop(code, None)
                sess, err = self.join(code)
                out["WatchJoinRoom(5191)"] = (
                    {"ok": True, "room_id": sess["room_id"], "players": len(sess["players"])}
                    if sess else {"ok": False, "error": err}
                )
            except Exception as exc:
                out["WatchJoinRoom(5191)"] = {"error": str(exc)[:300]}
        return out

    # ── 工作线程：所有游戏网络调用都在这里 ──
    def reconnect(self, why: str):
        """v1.0: 连接断了就重建。

        背景(实测): 服务跑久了会和游戏服断连(心跳报 WinError 10053), 之后所有
        join/refresh 都在这条死连接上重试, 永远失败 —— 只能重启进程。
        这里改成自动重建 client + 重新登录, 并清空已经失效的观战会话。
        """
        now = time.time()
        if now - self._last_reconnect < RECONNECT_COOLDOWN:
            log("重连冷却中(%.0fs 后允许), 忽略: %s"
                % (RECONNECT_COOLDOWN - (now - self._last_reconnect), why))
            return False
        self._last_reconnect = now
        self._beat_fails = 0
        log("== 重新登录: %s ==" % why)
        try:
            if self.client is not None:
                try:
                    self.client.close()
                except Exception:
                    pass
            self.client = None
            with self.lock:
                self.sessions.clear()      # 旧会话已随连接一起失效
            self.login_state = "relogin"
            self.login()
            log("== 重新登录成功 ==")
            return True
        except SystemExit:
            self.login_state = "relogin-failed"
            log("重新登录失败(SystemExit)")
            return False
        except Exception as exc:
            self.login_state = "relogin-failed"
            log("重新登录失败: %s: %s" % (type(exc).__name__, exc))
            return False

    def worker(self):
        last_health = 0.0
        while not self.stop:
            try:
                now = time.time()
                # 关键: 游戏客户端每 5 秒发一次心跳(CMDID 5003), 服务器靠它判断会话存活。
                # 不发心跳 -> 服务器判定会话已死 -> 之后所有业务请求都被静默丢弃
                # (症状: 登录成功、能收到服务器推送, 但自己的请求一个回应都没有)。
                if now - self._last_beat >= 5.0:
                    self._last_beat = now
                    try:
                        self.client.heartbeat()
                        self._beat_fails = 0
                    except Exception as exc:
                        self._beat_fails += 1
                        log("心跳发送失败(%d 次): %s" % (self._beat_fails, exc))
                        # v1.0: 连接断了就必须重连 —— 之前只 log 不处理, 于是 client 永久失效,
                        # 后面所有 join/refresh 都是在死连接上重试, 只能靠重启服务恢复。
                        if self._beat_fails >= 2:
                            self.reconnect("心跳连续失败")
                with self.lock:
                    codes = set(self.wanted)
                # 新请求的观战码：先加入
                for code in list(codes):
                    with self.lock:
                        sess = self.sessions.get(code)
                    # v42: 关键修复 —— 原来看 sess is None 才 join。
                    # 但一次 join 失败(room_id=0, err=11079)会把"失败记录"留在 sessions 里,
                    # 于是既不重试 join(不是 None), 也不 refresh(room_id=0 被 continue 跳过),
                    # 永久卡死在失败状态, 只能重启服务 —— 这正是"每次重启才好"的根因。
                    # 现在: 只要没有有效 room_id 就按节流重试加入。
                    if sess is not None and not sess.get("room_id"):
                        last_try = sess.get("updated", 0) or 0
                        if now - last_try < JOIN_RETRY_SEC:
                            continue
                    if sess is None or not sess.get("room_id"):
                        # v41: 加入新房间前先退掉其它会话(服务器一个账号只允许一个观战会话)
                        self.exit_other_sessions(code)
                        try:
                            sess, err = self.join(code)
                        except Exception as exc:
                            sess, err = None, "join-exception %s" % exc
                            # v1.0: 连接层面的异常(10053/10054/超时)说明 client 已死,
                            # 在死连接上重试一百次也没用 —— 直接触发重连。
                            txt = str(exc)
                            if ("10053" in txt or "10054" in txt or "10060" in txt
                                    or "ConnectionReset" in txt or "timed out" in txt
                                    or "Timeout" in txt):
                                self.reconnect("join 连接异常: %s" % txt[:80])
                        if sess:
                            with self.lock:
                                self.sessions[code] = sess
                            log("已加入观战 code=%s room=%s players=%s"
                                % (code, sess["room_id"], len(sess["players"])))
                        else:
                            log("加入观战失败 code=%s: %s (将在 %ds 后重试)"
                                % (code, err, JOIN_RETRY_SEC))
                            with self.lock:
                                self.sessions[code] = {
                                    "code": code, "room_id": 0, "room_server_id": 0,
                                    "players": [], "updated": now, "err": err, "joins": 0,
                                }
                # 刷新已有会话
                for code, sess in list(self.sessions.items()):
                    if not sess.get("room_id"):
                        continue
                    try:
                        err = self.refresh(sess)
                        if err:
                            sess["err"] = err
                    except Exception as exc:
                        sess["err"] = "refresh-exception %s" % exc
                if now - last_health > 30:
                    last_health = now
                    log("health: sessions=%d wanted=%d last=%s"
                        % (len(self.sessions), len(self.wanted), self.last_error or "-"))
            except Exception as exc:
                self.last_error = "%s: %s" % (type(exc).__name__, exc)
                log("worker 异常: %s" % self.last_error)
            time.sleep(max(0.5, self.args.interval))

    # ── 给 HTTP 用 ──
    def hands(self, code: str) -> dict:
        with self.lock:
            # v41: 只保留"最新请求的房间"。原来 wanted 只加不清, worker 会同时观战
            # 多个房间, 而服务器一个账号只允许一个会话 -> 换局后新房间必被 err=11079 拒。
            if code not in self.wanted:
                self.wanted.clear()
            self.wanted.add(code)
            sess = self.sessions.get(code)
            if sess is None:
                return {"ok": False, "pending": True, "code": code,
                        "hint": "service is joining the room; retry in ~2s"}
            return {
                "ok": not sess.get("err") and bool(sess.get("players")),
                "code": code,
                "room_id": sess.get("room_id", 0),
                "room_server_id": sess.get("room_server_id", 0),
                "updated": sess.get("updated", 0.0),
                "age": round(time.time() - sess.get("updated", 0.0), 1),
                "err": sess.get("err", ""),
                "players": sess.get("players", []),
            }

    def health(self) -> dict:
        with self.lock:
            return {
                "ok": self.login_state == "ok",
                "login": self.login_state,
                "sessions": {k: {"room_id": v.get("room_id"), "err": v.get("err"),
                                 "age": round(time.time() - v.get("updated", 0.0), 1)}
                             for k, v in self.sessions.items()},
                "wanted": sorted(self.wanted),
                "last_error": self.last_error,
                "login_data": (self.login_data or "")[:400],
            }


def make_handler(svc: WatchService, token: str):
    class Handler(BaseHTTPRequestHandler):
        def _send(self, obj, status=200):
            body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, fmt, *a):
            pass

        def do_GET(self):
            u = urlparse(self.path)
            q = parse_qs(u.query)
            if token and (q.get("token", [""])[0] != token):
                return self._send({"ok": False, "err": "bad token"}, 403)
            if u.path in ("/", "/help"):
                return self._send({"ok": True, "usage": [
                    "/health", "/hands?code=<watchCode>&token=<token>"]})
            if u.path == "/health":
                return self._send(svc.health())
            if u.path == "/probe":
                return self._send(svc.probe((q.get("xor", [""])[0] or "").strip() or None))
            if u.path == "/hands":
                code = (q.get("code", [""])[0] or "").strip()
                if not code:
                    return self._send({"ok": False, "err": "missing code"}, 400)
                return self._send(svc.hands(code))
            if u.path == "/show":
                uid = (q.get("uid", [""])[0] or "").strip()
                if not uid:
                    return self._send({"ok": False, "err": "missing uid"}, 400)
                return self._send(svc.show(uid))
            if u.path == "/replay":
                rid = (q.get("rid", [""])[0] or "").strip()
                if not rid:
                    return self._send({"ok": False, "err": "missing rid"}, 400)
                return self._send(svc.replay(rid))
            return self._send({"ok": False, "err": "not found"}, 404)

    return Handler


def main():
    ap = argparse.ArgumentParser(description="旁观者服务：用第二账号观战并对外提供四人手牌")
    ap.add_argument("--tel", required=True, help="观战账号手机号")
    ap.add_argument("--sms", default="", help="短信验证码（与 --password 二选一）")
    ap.add_argument("--password", default="", help="手机号+密码登录（推荐：免验证码，可自动重登）")
    ap.add_argument("--pw-file", default="", help="从文件读密码（避免出现在命令行里）")
    ap.add_argument("--channel", default="test_junhai")
    ap.add_argument("--device-id", default="", help="固定设备号（默认按本机机器名推导；换机器时建议固定）")
    ap.add_argument("--client-ver", default="", help="客户端版本（默认自动检测 catalog 版本）")
    ap.add_argument("--host", default=DEFAULT_HOST, help="游戏服地址")
    ap.add_argument("--port", type=int, default=DEFAULT_PORT, help="游戏服端口")
    ap.add_argument("--listen", default="0.0.0.0", help="HTTP 监听地址")
    ap.add_argument("--http-port", type=int, default=8765)
    ap.add_argument("--token", default="", help="访问令牌（默认随机生成）")
    ap.add_argument("--interval", type=float, default=2.0, help="刷新间隔秒")
    ap.add_argument("--xor", default="none", choices=["none", "body", "full"],
                    help="协议帧混淆模式（默认 none）")
    ap.add_argument("--probe-uid", type=int, default=1368484,
                    help="/probe 用的玩家 id（对照查询）")
    ap.add_argument("--probe-watch", default="", help="/probe 顺便测的观战码")
    ap.add_argument("--sid-ttl", type=float, default=3600.0,
                    help="缓存 sid 的最长复用秒数（默认 3600）")
    ap.add_argument("--no-sid-cache", action="store_true", help="强制重新握手")
    ap.add_argument("--dex-root", default=str(DEX_ROOT_DEFAULT))
    args = ap.parse_args()

    if args.pw_file and not args.password:
        try:
            args.password = Path(args.pw_file).read_text(encoding="utf-8").strip()
            log("密码已从 %s 读入 (len=%d, 不打印内容)" % (args.pw_file, len(args.password)))
        except Exception as exc:
            raise SystemExit("读密码文件失败: %s" % exc)
    if not args.password and not args.sms:
        raise SystemExit("需要 --password（推荐：免验证码）或 --sms")

    sys.path.insert(0, args.dex_root)
    os.chdir(args.dex_root)      # proto_loader 按相对路径找 .proto.pb

    token = args.token or secrets.token_urlsafe(12)
    svc = WatchService(args)
    svc.login()

    th = threading.Thread(target=svc.worker, daemon=True)
    th.start()

    httpd = ThreadingHTTPServer((args.listen, args.http_port), make_handler(svc, token))
    log("HTTP 服务已启动: http://%s:%d   token=%s" % (args.listen, args.http_port, token))
    log("测试: curl 'http://127.0.0.1:%d/hands?code=<观战码>&token=%s'"
        % (args.http_port, token))
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        svc.stop = True
        try:
            if svc.client:
                svc.client.close()
        except Exception:
            pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
