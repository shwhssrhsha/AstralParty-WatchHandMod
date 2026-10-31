using System;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;
using Core;
using Core.Net;
using Cysharp.Threading.Tasks;
using FairyGUI;
using GameLogic;
using party.model;
using party.protocol;
using Tools;
using UI;
using UnityEngine;

namespace WatchHandMod
{
    /// <summary>
    /// v13 —— 队友手牌卡图常驻显示（游戏原生 UICom_Card）+ 自绘悬停提示。
    ///
    /// 数据: GameLogicManager.inst.room.curRoomInfo.Players[i].cardContainer._HandCards
    ///       (实测: PVE 下四人手牌全是明文, maskedIds=0 plainIds=16)
    /// 挂载: GRoot.inst -> UIBattleInfoPanel -> com_BattlePlayer.com_Container.com_Player_1..4
    /// 画法: UICom_Card.CreateInstance() + CommonUIManager.RendererCard(...)
    ///
    /// v13 相对 v12 的改动:
    ///   1. 卡名标签不再 new TextFormat()(那会把 font 清空导致文字不渲染), 改为改现有 textFormat
    ///   2. 悬停提示改为"自绘": 自己追踪鼠标命中卡图矩形, 自己画提示框挂在 GRoot 顶层
    ///      (不再依赖 FairyGUI 的 tooltips 机制, 那需要 GRoot 注册 tooltip 窗口)
    ///   3. 每个 tick 都检查手牌变化
    ///
    /// 只读: 不修改任何游戏状态, 不发网络请求。
    /// </summary>
    public static class ModEntry
    {
        private const float CardH = 84f;    // 卡图高度
        private const float LabelH = 16f;   // 卡图下方名字高度
        private const float Gap = 3f;       // 卡间距
        private const float TipW = 340f;    // 提示框宽度
        private const float TipLineH = 18f; // 提示框行高

        private static readonly GComponent[] Strips = new GComponent[4];
        private static readonly string[] LastSig = new string[4];
        private static int _ticks;
        private static bool _mountLogged;
        private static bool _fontLogged;
        private static string _lastError;

        // ---- 自绘提示 ----
        private sealed class Hotspot
        {
            public float X;
            public float Y;
            public float W;
            public float H;
            public string Text;
        }

        private static readonly List<Hotspot> Spots = new List<Hotspot>();
        private static GComponent _hud;
        /// <summary>v34: HUD 面板缓存 —— 之前每帧递归遍历整棵 UI 树(FindPanel), 实测把帧率压到 ~2 FPS。</summary>
        private static UIBattleInfoPanel _panelCache;
        /// <summary>v64: tooltip 置顶已移到 OnTick, 不再需要每帧计数。</summary>
        /// <summary>v39: Q 键开关 —— 默认关(零渲染开销), 开启后才画队友手牌。</summary>
        private static bool _handsVisible;
        private static bool _keyQHeld;
        /// <summary>v65: 0=未知 1=原生 Input.GetKeyDown 2=反射 fallback。只用于日志判别。</summary>
        private static int _keyPath;
        /// <summary>v66: 屏幕开关按钮(取代 Q 键, 走 FairyGUI onClick, 零轮询)。</summary>
        private static GComponent _toggle;
        private static GTextField _toggleText;
        private const float ToggleW = 62f;
        private const float ToggleH = 24f;

        // ===== v68 诊断开关: 屏幕上逐项开关, 用一次实机测试定位是哪一项导致"跳" =====
        private static bool _swHook = true;     // HookSyncProbes(替换游戏 RPC 回调)
        private static bool _swWrite = true;    // WriteWatchTarget(读房间状态)
        private static bool _swPoll = true;     // HTTP 轮询 + 收结果
        private static bool _swRender = true;   // Update(手牌渲染/挂载)
        private static bool _swDiag = true;     // DumpSources/DumpServiceDiag/prof/指纹采样
        /// <summary>v69: 后台请求 —— 手牌不显示时也发请求, 但一张卡都不画。
        /// 目的是把"发请求"和"渲染手牌"这两个原本绑死的变量拆开:
        /// 冷启动 + 只开 B(手牌不点) 若仍跳 => 元凶是请求; 若不跳 => 元凶是渲染。</summary>
        private static bool _swBgReq;
        private static readonly string[] SwNames =
            { "H hook", "W 写码", "P 轮询", "R 渲染", "D 诊断", "B 后台请求" };
        private static GComponent _swBox;
        private static readonly GTextField[] _swTexts = new GTextField[6];

        private static bool SwGet(int i)
        {
            switch (i)
            {
                case 0: return _swHook;
                case 1: return _swWrite;
                case 2: return _swPoll;
                case 3: return _swRender;
                case 4: return _swDiag;
                default: return _swBgReq;
            }
        }

        private static void SwToggle(int i)
        {
            switch (i)
            {
                case 0: _swHook = !_swHook; break;
                case 1: _swWrite = !_swWrite; break;
                case 2: _swPoll = !_swPoll; break;
                case 3: _swRender = !_swRender; break;
                case 4: _swDiag = !_swDiag; break;
                default: _swBgReq = !_swBgReq; break;
            }
            SdkLog.Info("WatchHand", "诊断开关 " + SwNames[i] + " -> " + (SwGet(i) ? "开" : "关"));
        }
        private static GComponent _tip;
        private static GGraph _tipBg;
        private static GRichTextField _tipText;
        private static string _tipShown;
        private static int _tipDumps;
        private static int _descDumps;
        private static int _handDumps;   // 只做计数统计, 不再限制条数
        private static bool _chainLogged;
        /// <summary>已打印过 render 诊断的卡牌 ID(每张只打一次, 避免刷屏)。</summary>
        private static readonly HashSet<int> RenderLogged = new HashSet<int>();
        /// <summary>
        /// 诊断(v46, 2026-10-26): 统计 "strip 还在原容器里" 与 "需要重建" 的次数。
        /// 目的是量化游戏是否在反复重置玩家按钮 —— 若 rebuilds 快速上涨而 hits 不动,
        /// 说明游戏持续把我们 AddChild 进 Button 的子节点移除/销毁, 那就是"界面周期性重置"的机制。
        /// </summary>
        private static int _stripRebuilds;
        /// <summary>诊断(v56): PollService 真正被调用(即将发请求)的累计次数。</summary>
        private static int _pollCount;
        private static int _stripHits;
        private static int _stripRebuildLogs;
        /// <summary>
        /// v20: 服务器实时下发的手牌快照(按玩家)。
        /// 来源 = SDK 的 GameEvents.HandChanged(包装 RoomHeroCardChangeS2C 得到)。
        /// 为什么需要它: 游戏自己的 OnCardChanged 有一道 "attrCards[0].CardId >= 0" 的掩码判断,
        /// 且外层回调还要求 battle.clientFinishReady; 而游戏容器 cardContainer._HandCards
        /// 对队友实测只有开局快照(3 分钟零更新)。SDK 的事件转发没有这两道限制, 所以队友手牌
        /// 要靠这里刷新。若服务器对某人下发掩码(负数 CardId), 我们丢弃该次更新(与游戏一致)。
        /// </summary>
        private static readonly Dictionary<long, List<HandCardData>> LiveHands =
            new Dictionary<long, List<HandCardData>>();
        private static bool _hookedEvents;
        private static int _handEventCount;

        // =====================================================================
        // v59 探针(2026-10-27): **只记录, 不改游戏行为**。
        //   目的: 验证"视角跳回自己 + 选目标被清空"是否由这条链引起:
        //     OnlineSyncRoomIdS2C(1052) → RequestSyncRoomC2S → SyncRoomS2C
        //     → roomController.Dispose() + CreateRoom() → 相机回自机 / selectPlayer 归 0
        //   做法: 把游戏自己的 RPC 回调包一层(原回调一律转发), 记录到达时间;
        //         并每 tick 采样 GRoot 子节点数 / UIBattleInfoPanel 实例变化。
        //   **不改任何现有行为**, 只是旁路观测。
        // =====================================================================
        private static OnlineSyncRoomIdS2CRPC.OnOnlineSyncRoomIdS2CServerDelegate _probeOrigOnlineSync;
        private static OnlineSyncRoomIdS2CRPC.OnOnlineSyncRoomIdS2CServerDelegate _probeMineOnlineSync;
        private static SyncRoomS2CRPC.OnSyncRoomS2CServerDelegate _probeOrigSyncRoom;
        private static SyncRoomS2CRPC.OnSyncRoomS2CServerDelegate _probeMineSyncRoom;
        private static int _probeOnlineSyncCount;
        private static int _probeSyncRoomCount;
        private static int _probeHookLogs;
        private static int _probeUiLogs;
        private static int _probeLastNumChildren = -1;
        private static UIBattleInfoPanel _probeLastPanel;

        /// <summary>
        /// v24: HeroAttrUpdated(UpdateHeroAttrS2C) 里手牌字段的原始记录。
        /// 这条消息没有 IsSelf 检查 —— 服务器对**所有人**都发, 客户端在 OnCardChanged 里
        /// 用 "attrCards[0].CardId >= 0" 当掩码闸门。打印原始 CardId 的正负, 就能定案
        /// 队友手牌到底是"服务器掩码"还是"另有原因"。
        /// 注意: SDK 的这个事件在**网络线程**触发, 所以回调里只收集, 到 tick(主线程)再打印。
        /// </summary>
        private sealed class AttrRec
        {
            public long FxPlayer;
            public string Line;
        }

        private static readonly List<AttrRec> AttrLog = new List<AttrRec>();
        private static readonly object AttrLogLock = new object();

        private static void OnHeroAttrUpdated(UpdateHeroAttrS2C model)
        {
            try
            {
                if (model == null)
                {
                    return;
                }
                var attrs = model.EffectDatas;
                if (attrs == null)
                {
                    return;
                }
                for (int i = 0; i < attrs.Count; i++)
                {
                    HeroAttrEffect e = attrs[i];
                    if (e == null || e.Card == null)
                    {
                        continue;   // 只看带"手牌变更"的那部分
                    }
                    var cards = e.Card.Cards;
                    var sb = new System.Text.StringBuilder();
                    int neg = 0;
                    if (cards != null)
                    {
                        for (int k = 0; k < cards.Count; k++)
                        {
                            int id = cards[k] == null ? 0 : cards[k].CardId;
                            if (k > 0) { sb.Append(','); }
                            sb.Append(id);
                            if (id < 0) { neg++; }
                        }
                    }
                    var rec = new AttrRec
                    {
                        FxPlayer = e.PlayerId,
                        Line = "attrEvent msgPlayer=" + model.PlayerId + " fxPlayer=" + e.PlayerId
                             + " n=" + (cards == null ? -1 : cards.Count) + " neg=" + neg
                             + " ids=[" + sb.ToString() + "]"
                    };
                    lock (AttrLogLock)
                    {
                        if (AttrLog.Count < 300)
                        {
                            AttrLog.Add(rec);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>把网络线程收集到的 attrEvent 记录在主线程打印出来(顺带标注 SELF/TEAM)。</summary>
        private static void FlushAttrLog()
        {
            AttrRec[] recs;
            lock (AttrLogLock)
            {
                if (AttrLog.Count == 0)
                {
                    return;
                }
                recs = AttrLog.ToArray();
                AttrLog.Clear();
            }
            long self = -1;
            try
            {
                RoomInfo room = GameLogicManager.inst.room == null ? null : GameLogicManager.inst.room.curRoomInfo;
                if (room != null)
                {
                    self = room.GetSelfInfo().Id;
                }
            }
            catch (Exception)
            {
            }
            for (int i = 0; i < recs.Length; i++)
            {
                SdkLog.Info("WatchHand", recs[i].Line
                    + (self >= 0 && recs[i].FxPlayer == self ? "   <= SELF" : "   <= TEAM"));
            }
        }

        /// <summary>
        /// v22/v23 (F9/F10 观战探针): 拦截期间保存的原 RPC 回调。非 null = 正在等响应。
        /// 原回调会 JoinLobbyAsync + BattleStart(会打断当前对局), 所以只取数据、不调用它。
        /// </summary>
        private static WatchRefreshRoomStateS2CRPC.OnWatchRefreshRoomStateS2CServerDelegate _watchOriginal;

        /// <summary>
        /// v23 (F9): WatchJoinRoomS2C 的原回调。这条响应里**直接带四人 Players(含 Hero.Cards)**,
        /// 一步就能拿到整房手牌; 原回调会 CreateRoom + 把客户端切进观战状态, 所以不调用它。
        /// </summary>
        private static WatchJoinRoomS2CRPC.OnWatchJoinRoomS2CServerDelegate _joinOriginal;
        private static int _probeSeq;

        private static void RestoreProbeCallbacks()
        {
            try
            {
                NetManager net = MonoSingletonProvider<NetManager>.inst;
                if (net == null || net.RPC == null)
                {
                    return;
                }
                if (_joinOriginal != null && net.RPC.WatchJoinRoomS2C != null)
                {
                    net.RPC.WatchJoinRoomS2C.OnWatchJoinRoomS2CServerCallBackAsync = _joinOriginal;
                    SdkLog.Info("WatchHand", "探针: Join 回调已还原");
                }
                if (_watchOriginal != null && net.RPC.WatchRefreshRoomStateS2C != null)
                {
                    net.RPC.WatchRefreshRoomStateS2C.OnWatchRefreshRoomStateS2CServerCallBackAsync = _watchOriginal;
                    SdkLog.Info("WatchHand", "探针: Refresh 回调已还原");
                }
            }
            catch (Exception ex)
            {
                SdkLog.Warn("WatchHand", "还原探针回调失败: " + ex.Message);
            }
            _joinOriginal = null;
            _watchOriginal = null;
        }

        /// <summary>
        /// v23 (F9): 用当前房间的观战码请求 WatchJoinRoomC2S。
        /// 服务器回的 WatchJoinRoomS2C 直接带四人 Players(含手牌), 取完就完事, 不需要再发 Refresh。
        /// </summary>
        private static void WatchProbeJoin()
        {
            if (_joinOriginal != null)
            {
                SdkLog.Warn("WatchHand", "F9: 上一次 Join 拦截还在等响应(超时会自动还原)");
                return;
            }
            try
            {
                NetManager net = MonoSingletonProvider<NetManager>.inst;
                if (net == null || net.RPC == null || net.RPC.WatchJoinRoomS2C == null)
                {
                    SdkLog.Warn("WatchHand", "F9: NetManager / RPC 不可用");
                    return;
                }
                RoomInfo room = GameLogicManager.inst.room == null ? null : GameLogicManager.inst.room.curRoomInfo;
                string code = room == null ? null : room.watchCode;
                if (string.IsNullOrEmpty(code))
                {
                    SdkLog.Warn("WatchHand", "F9: 取不到观战码(curRoomInfo.watchCode 为空), 放弃");
                    return;
                }
                _probeSeq++;
                _joinOriginal = net.RPC.WatchJoinRoomS2C.OnWatchJoinRoomS2CServerCallBackAsync;
                net.RPC.WatchJoinRoomS2C.OnWatchJoinRoomS2CServerCallBackAsync = OnJoinProbeResponse;
                SdkLog.Info("WatchHand", "F9#" + _probeSeq + ": 拦截已武装(原回调="
                    + (_joinOriginal == null ? "null" : "有") + ") watchCode=" + code
                    + " roomId=" + (room == null ? 0L : room.Id));
                GameLogicManager.inst.watch.RequestWatchJoinRoomC2S(code);
                SdkLog.Info("WatchHand", "F9#" + _probeSeq + ": WatchJoinRoomC2S 已发出, 等响应");
            }
            catch (Exception ex)
            {
                _joinOriginal = null;
                SdkLog.Error("WatchHand", "F9 探针失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static UniTask OnJoinProbeResponse(WatchJoinRoomS2C model, int errId, bool isDispatch)
        {
            RestoreProbeCallbacks();
            try
            {
                if (errId != 0)
                {
                    SdkLog.Warn("WatchHand", "F9#" + _probeSeq + ": 服务器 errId=" + errId + " (观战加入被拒)");
                    return UniTask.CompletedTask;
                }
                if (model == null)
                {
                    SdkLog.Warn("WatchHand", "F9#" + _probeSeq + ": 响应为空");
                    return UniTask.CompletedTask;
                }
                var players = model.Players;
                SdkLog.Info("WatchHand", "F9#" + _probeSeq + ": 收到观战房间 RoomId=" + model.RoomId
                    + " mapId=" + model.MapId
                    + " players=" + (players == null ? -1 : players.Count));
                if (players == null)
                {
                    return UniTask.CompletedTask;
                }
                int got = 0;
                for (int i = 0; i < players.Count; i++)
                {
                    Player sp = players[i];
                    if (sp == null)
                    {
                        continue;
                    }
                    var hero = sp.Hero;
                    var cards = hero == null ? null : hero.Cards;
                    var sb = new System.Text.StringBuilder();
                    int neg = 0;
                    var list = new List<HandCardData>();
                    if (cards != null)
                    {
                        for (int k = 0; k < cards.Count; k++)
                        {
                            CardInfo ci = cards[k];
                            int id = ci == null ? 0 : ci.CardId;
                            if (k > 0) { sb.Append(','); }
                            sb.Append(id);
                            if (id < 0) { neg++; }
                            if (ci != null && id >= 0)
                            {
                                try { list.Add(new HandCardData(ci)); }
                                catch (Exception) { }
                            }
                        }
                    }
                    SdkLog.Info("WatchHand", "F9#" + _probeSeq + ": " + (sp.IsBot ? "BOT" : "HUMAN") + "#" + sp.Id
                        + " hero=" + (hero == null ? "null" : "ok")
                        + " n=" + (cards == null ? -1 : cards.Count) + " neg=" + neg
                        + " ids=[" + sb.ToString() + "]");
                    if (list.Count > 0 && neg == 0)
                    {
                        LiveHands[sp.Id] = list;
                        got++;
                    }
                }
                SdkLog.Info("WatchHand", "F9#" + _probeSeq + ": 写入 " + got + " 人手牌到 LiveHands(共 "
                    + LiveHands.Count + " 人), 界面应刷新");
            }
            catch (Exception ex)
            {
                SdkLog.Error("WatchHand", "F9 处理响应失败: " + ex.GetType().Name + ": " + ex.Message);
            }
            return UniTask.CompletedTask;   // ← 不调用原回调: 避免 CreateRoom + 切进观战状态
        }

        /// <summary>
        /// v23 (F10): 直接发 WatchRefreshRoomStateC2S(绕过 RequestWatchRefreshRoomStateC2S 里
        /// 那个 await PreLoadBattleAsset —— 上一版请求很可能卡在那里根本没发出去)。
        /// </summary>
        private static void WatchProbeRefresh()
        {
            if (_watchOriginal != null)
            {
                SdkLog.Warn("WatchHand", "F10: 上一次 Refresh 拦截还在等响应(超时会自动还原)");
                return;
            }
            try
            {
                NetManager net = MonoSingletonProvider<NetManager>.inst;
                RoomInfo room = GameLogicManager.inst.room == null ? null : GameLogicManager.inst.room.curRoomInfo;
                if (net == null || net.RPC == null || net.RPC.WatchRefreshRoomStateS2C == null
                    || room == null || room.info == null)
                {
                    SdkLog.Warn("WatchHand", "F10: NetManager/RPC/房间信息不全, 放弃");
                    return;
                }
                _probeSeq++;
                _watchOriginal = net.RPC.WatchRefreshRoomStateS2C.OnWatchRefreshRoomStateS2CServerCallBackAsync;
                net.RPC.WatchRefreshRoomStateS2C.OnWatchRefreshRoomStateS2CServerCallBackAsync = OnWatchProbeResponse;
                SdkLog.Info("WatchHand", "F10#" + _probeSeq + ": 拦截已武装, 直接发请求 roomId=" + room.Id
                    + " serverId=" + room.info.RoomServerId);
                net.RPC.WatchRefreshRoomStateC2S.WatchRefreshRoomStateC2SCall(new WatchRefreshRoomStateC2S
                {
                    RoomId = room.Id,
                    RoomServerId = room.info.RoomServerId
                });
                SdkLog.Info("WatchHand", "F10#" + _probeSeq + ": WatchRefreshRoomStateC2S 已发出");
            }
            catch (Exception ex)
            {
                _watchOriginal = null;
                SdkLog.Error("WatchHand", "F10 探针失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// v22/v23 (F10 的响应处理): 抢下 WatchRefreshRoomStateS2C 里的整房 Room(含四人手牌)。
        /// v22 的 F9 入口已废弃 —— 那条请求走 RequestWatchRefreshRoomStateC2S, 内部先 await
        /// PreLoadBattleAsset, 实测 35 秒都没响应(请求很可能压根没发出去)。现在 F9 改走
        /// WatchJoinRoom(见 WatchProbeJoin), F10 才是这条直接发请求的版本。
        /// </summary>
        private static UniTask OnWatchProbeResponse(WatchRefreshRoomStateS2C model, int errId, bool isDispatch)
        {
            // 先还原回调 —— 只拦一次, 不影响以后的正常观战
            RestoreProbeCallbacks();

            try
            {
                if (errId != 0)
                {
                    SdkLog.Warn("WatchHand", "F9: 服务器 errId=" + errId + " (观战刷新被拒)");
                    return UniTask.CompletedTask;
                }
                Room room = model == null ? null : model.Room;
                if (room == null)
                {
                    SdkLog.Warn("WatchHand", "F9: 响应里没有 Room");
                    return UniTask.CompletedTask;
                }
                var players = room.Players;
                SdkLog.Info("WatchHand", "F9: 收到快照 Room.Id=" + room.Id
                    + " players=" + (players == null ? -1 : players.Count));
                if (players == null)
                {
                    return UniTask.CompletedTask;
                }
                for (int i = 0; i < players.Count; i++)
                {
                    Player sp = players[i];
                    if (sp == null)
                    {
                        continue;
                    }
                    var hero = sp.Hero;
                    var cards = hero == null ? null : hero.Cards;
                    var sb = new System.Text.StringBuilder();
                    int neg = 0;
                    var list = new List<HandCardData>();
                    if (cards != null)
                    {
                        for (int k = 0; k < cards.Count; k++)
                        {
                            CardInfo ci = cards[k];
                            int id = ci == null ? 0 : ci.CardId;
                            if (k > 0) { sb.Append(','); }
                            sb.Append(id);
                            if (id < 0) { neg++; }
                            if (ci != null && id >= 0)
                            {
                                try { list.Add(new HandCardData(ci)); }
                                catch (Exception) { }
                            }
                        }
                    }
                    SdkLog.Info("WatchHand", "F9: " + (sp.IsBot ? "BOT" : "HUMAN") + "#" + sp.Id
                        + " hero=" + (hero == null ? "null" : "ok")
                        + " n=" + (cards == null ? -1 : cards.Count) + " neg=" + neg
                        + " ids=[" + sb.ToString() + "]");
                    if (list.Count > 0 && neg == 0)
                    {
                        LiveHands[sp.Id] = list;
                    }
                }
                SdkLog.Info("WatchHand", "F9: 快照已记入 LiveHands(" + LiveHands.Count + " 人), 界面应刷新");
            }
            catch (Exception ex)
            {
                SdkLog.Error("WatchHand", "F9 处理响应失败: " + ex.GetType().Name + ": " + ex.Message);
            }
            return UniTask.CompletedTask;   // ← 关键: 不调用原回调, 避免 JoinLobby + BattleStart
        }

        private static void OnHandChanged(long playerId, IReadOnlyList<CardInfo> cards)
        {
            try
            {
                if (cards == null)
                {
                    return;
                }
                _handEventCount++;
                int neg = 0;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < cards.Count; i++)
                {
                    int id = cards[i] == null ? 0 : cards[i].CardId;
                    if (i > 0) { sb.Append(','); }
                    sb.Append(id);
                    if (id < 0) { neg++; }
                }
                bool self = false;
                try
                {
                    GameLogicManager gm = GameLogicManager.inst;
                    RoomInfo room = gm == null || gm.room == null ? null : gm.room.curRoomInfo;
                    self = room != null && room.GetSelfInfo().Id == playerId;
                }
                catch (Exception)
                {
                }
                SdkLog.Info("WatchHand", "handEvent #" + _handEventCount
                    + (self ? " SELF" : " TEAM") + " id=" + playerId
                    + " n=" + cards.Count + " neg=" + neg + " ids=[" + sb.ToString() + "]");

                if (neg > 0)
                {
                    return;   // 掩码(负数) = 服务器不给内容, 不覆盖已有快照
                }
                var list = new List<HandCardData>(cards.Count);
                for (int i = 0; i < cards.Count; i++)
                {
                    CardInfo ci = cards[i];
                    if (ci == null)
                    {
                        continue;
                    }
                    try
                    {
                        list.Add(new HandCardData(ci));
                    }
                    catch (Exception)
                    {
                    }
                }
                LiveHands[playerId] = list;
            }
            catch (Exception ex)
            {
                SdkLog.Warn("WatchHand", "OnHandChanged: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// v44: 取某玩家要显示的手牌 —— **观战服务优先**(它才是实时源)。
        ///
        /// 为什么不用本地数据: rp.serverPlayer.Hero.Cards 与 rp.cardContainer._HandCards
        /// 对四人都有数据、也无掩码, 但**是快照、不随出牌更新** —— 实测相隔 32 秒完全一致,
        /// 而同一时刻服务侧 count 从 5 变成 4。用本地数据会显示"看起来像真的、其实是旧的"手牌。
        /// 顺序: SvcHands(需 5 秒内更新过) → LiveHands → 失败(界面显示"手牌加载失败")。
        /// </summary>
        private static IReadOnlyList<HandCardData> CardsOf(RoomPlayer rp, CardContainer cc)
        {
            if (rp == null)
            {
                return null;
            }
            // ① 观战服务(真·实时) —— 必须检查时效, 服务停了就不能继续摆旧数据
            // v44: 时效用**帧计数**判断 —— Environment.TickCount 在本机 IL2CPP 下不增长
            // (诊断里 age 永远同一个数, 导致永远判定过期); _ticks 稳定增长(约 1.9/秒)。
            long svcAgeTicks = _ticks - _svcHandsAtTicks;
            bool svcFresh = _svcHandsAtTicks > 0 && svcAgeTicks <= SvcStaleTicks;
            if (svcFresh)
            {
                List<HandCardData> svc;
                lock (SvcHandsLock)
                {
                    SvcHands.TryGetValue(rp.Id, out svc);
                }
                if (svc != null && svc.Count > 0)
                {
                    return svc;
                }
            }
            // ② 事件快照(出牌时服务器推送的变化)
            List<HandCardData> live;
            if (LiveHands.TryGetValue(rp.Id, out live) && live != null && live.Count > 0)
            {
                return live;
            }
            // ③ 拿不到 → null, 由调用方显示"手牌加载失败"
            return null;
        }

        /// <summary>v43: 手牌取不到时, 在对应位置明确显示失败原因, 而不是摆一份陈旧的开局快照。</summary>
        private static void RenderUnavailable(GComponent strip, string reason)
        {
            try
            {
                strip.RemoveChildren(0, -1, true);
                GTextField txt = new GTextField();
                txt.text = reason;
                txt.singleLine = true;
                // 必须取现有 textFormat 改 size/color 再赋回(TextFormat 是结构体);
                // new TextFormat() 会把 font 清空, 导致文字根本不渲染。
                TextFormat fmt = txt.textFormat;
                fmt.size = 14;
                fmt.color = new Color(1f, 0.45f, 0.42f, 1f);
                txt.textFormat = fmt;
                strip.AddChild(txt);
                strip.SetSize(190f, CardH + LabelH);
                strip.visible = true;
            }
            catch (Exception ex)
            {
                SdkLog.Warn("WatchHand", "RenderUnavailable: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string _lastWatchCode = "";
        private static string _lastLoggedCode;   // v40: 只在观战码变化时打日志


        /// <summary>
        /// v1.0(发布版): 观战服务地址与令牌**不再硬编码**, 改由用户自己的配置文件提供:
        ///
        ///   AstralParty_ModLoader\mods\WatchHandMod\watch_service.json
        ///   { "url": "http://你自己的服务器:8765", "token": "你自己设的 token" }
        ///
        /// 文件不存在 = 不启用(界面显示"手牌加载失败: 服务未配置"), 不影响游戏本身。
        /// 这样分发的 dll 里不含任何服务端地址/令牌。
        /// 注: 自用版(WatchHandMod)保持硬编码, 与发布版分开维护。
        /// </summary>
        private static string _svcUrl = "";
        private static string _svcToken = "";
        private static string _svcStatus = "未配置";
        /// <summary>v1.0: 配置检查节流(帧, ≈10 帧/5 秒)。</summary>
        private static long _svcCfgCheckedTick;
        private static volatile string _svcLastError = "";   // v43: 最近一次服务侧失败原因(界面显示用)
        /// <summary>v44: 上次成功拿到服务数据时的帧计数(_ticks)。</summary>
        private static long _svcHandsAtTicks;
        /// <summary>v44: 超过这么多帧(≈10 帧 = 5 秒)没更新就算失效 —— 否则会一直显示旧手牌。</summary>
        private const int SvcStaleTicks = 10;
        /// <summary>v44: 单次请求硬超时(帧)。超过就强制认为失败, 避免 _svcBusy 永久卡死。</summary>
        private const int PollHardTimeoutTicks = 19;   // ≈10 秒(帧≈1.9/秒)
        private static long _svcPollStarted;
        private static long _svcLastPoll;
        private static volatile string _svcJsonResult;
        private static volatile bool _svcBusy;
        /// <summary>v61: 在途的 UnityWebRequest(反射对象); 非 null 表示正在下载。</summary>
        private static object _svcReq;
        private static readonly Dictionary<long, List<HandCardData>> SvcHands =
            new Dictionary<long, List<HandCardData>>();
        private static readonly object SvcHandsLock = new object();

        /// <summary>
        /// v1.0(发布版): 从用户自己的 watch_service.json 读服务地址与令牌。
        /// 每 5 秒(10 帧)看一次, 支持运行中改配置。文件不存在 = 不启用。
        /// </summary>
        private static void RefreshServiceConfig()
        {
            long cfgNowTick = _ticks;
            if (cfgNowTick - _svcCfgCheckedTick < 10)
            {
                return;
            }
            _svcCfgCheckedTick = cfgNowTick;
            try
            {
                string path = FindServiceConfigPath();
                if (path == null)
                {
                    _svcUrl = "";
                    _svcToken = "";
                    _svcStatus = "未配置";
                    return;
                }
                string txt = System.IO.File.ReadAllText(path);
                object node;
                if (!CesiumLoader.SDK.Configuration.CesiumJson.TryDeserialize(txt, out node))
                {
                    _svcStatus = "配置解析失败";
                    return;
                }
                var map = node as IDictionary<string, object>;
                if (map == null)
                {
                    _svcStatus = "配置格式错";
                    return;
                }
                string url = CesiumLoader.SDK.Configuration.CesiumJson.GetString(map, "url", "");
                string token = CesiumLoader.SDK.Configuration.CesiumJson.GetString(map, "token", "");
                if (string.IsNullOrEmpty(url))
                {
                    _svcUrl = "";
                    _svcToken = "";
                    _svcStatus = "未配置(url 为空)";
                    return;
                }
                if (url != _svcUrl)
                {
                    SdkLog.Info("WatchHand", "观战服务地址: " + url);
                }
                _svcUrl = url;
                _svcToken = token ?? "";
                _svcStatus = "已加载(" + path + ")";
            }
            catch (Exception ex)
            {
                _svcStatus = "配置异常 " + ex.GetType().Name;
            }
        }

        /// <summary>
        /// v1.0: 找 watch_service.json —— 优先 mod 自己的文件夹, 其次 mods 根, 再其次加载器根。
        /// 加载器会把 mods 目录通过环境变量 CESIUM_MODS_DIR 交给 mod。
        /// </summary>
        private static string FindServiceConfigPath()
        {
            var candidates = new List<string>();
            try
            {
                string mods = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
                if (!string.IsNullOrEmpty(mods))
                {
                    string m = mods.TrimEnd('\\', '/');
                    candidates.Add(m + "\\WatchHandMod\\watch_service.json");   // mod 自己的文件夹
                    candidates.Add(m + "\\watch_service.json");                  // mods 根
                    string root = System.IO.Path.GetDirectoryName(m);
                    if (!string.IsNullOrEmpty(root))
                    {
                        candidates.Add(root + "\\watch_service.json");            // 加载器根
                    }
                }
            }
            catch (Exception)
            {
            }
            for (int i = 0; i < candidates.Count; i++)
            {
                try
                {
                    if (System.IO.File.Exists(candidates[i]))
                    {
                        return candidates[i];
                    }
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

        /// <summary>
        /// v61: 发一次查询 —— 反射调用 Unity 的 UnityWebRequest（主线程发起 + 主线程轮询 isDone）。
        /// 为什么不再用线程池 / System.Net.Sockets / HttpWebRequest:
        ///   实测只要走过 .NET 线程池或 System.Net, 之后即使完全不再发包,
        ///   游戏界面仍会持续周期性重建(v56 HttpWebRequest、v58 原始 Socket 都这样),
        ///   而官方 HandViewerMod 全程用 UnityWebRequest, 没有这个现象。
        ///   反射调用可避开对 UnityWebRequestModule 的编译期依赖
        ///   (netstandard2.0 下直接引用会强制要求框架版 System.Uri, 编不过)。
        /// </summary>
        private static void PollService(string code)
        {
            long now = _ticks;   // v44: 用帧计数当"时钟"(TickCount 在本机不增长)

            // ---- 1) 先在途请求: 完成就收, 超时就丢 ----
            if (_svcReq != null)
            {
                bool done;
                try { done = (bool)SvProp(_svcReq, "isDone"); }
                catch (Exception) { done = true; }
                bool timeout = (now - _svcPollStarted) > PollHardTimeoutTicks;
                if (!done && !timeout)
                {
                    return;                       // 还在下载, 本 tick 什么都不做
                }
                FinishServiceRequest(done);
            }

            // ---- 2) 再决定要不要发新的 ----
            if (string.IsNullOrEmpty(_svcUrl) || string.IsNullOrEmpty(code))
            {
                return;
            }
            if (now - _svcLastPoll < 4)
            {
                return;
            }
            _svcLastPoll = now;
            _pollCount++;
            SdkLog.Info("WatchHand", "poll #" + _pollCount + " handsVisible=" + _handsVisible
                + " busy=0 code=" + code);
            StartServiceRequest(code);
        }

        private static object SvProp(object o, string name)
        {
            return o.GetType().GetProperty(name).GetValue(o, null);
        }

        /// <summary>v61: 反射创建 UnityWebRequest 并发起。成功返回 true。</summary>
        private static bool StartServiceRequest(string code)
        {
            try
            {
                Type t = CesiumLoader.SDK.Runtime.RuntimeAssemblyService.FindTypeBySimpleName("UnityWebRequest");
                if (t == null)
                {
                    _svcStatus = "找不到 UnityWebRequest";
                    _svcJsonResult = "!ERR!找不到 UnityWebRequest";
                    return false;
                }
                // 观战码/token 都是字母数字, 直接拼
                string url = _svcUrl.TrimEnd('/') + "/hands?code=" + code
                    + (string.IsNullOrEmpty(_svcToken) ? "" : "&token=" + _svcToken);
                MethodInfo get = t.GetMethod("Get", new Type[] { typeof(string) });
                if (get == null)
                {
                    _svcStatus = "UnityWebRequest.Get 不存在";
                    _svcJsonResult = "!ERR!UnityWebRequest.Get 不存在";
                    return false;
                }
                object req = get.Invoke(null, new object[] { url });
                if (req == null)
                {
                    _svcStatus = "请求对象为空";
                    _svcJsonResult = "!ERR!请求对象为空";
                    return false;
                }
                try
                {
                    t.GetMethod("SetRequestHeader", new Type[] { typeof(string), typeof(string) })
                        .Invoke(req, new object[] { "User-Agent", "WatchHandMod" });
                }
                catch (Exception) { }
                try
                {
                    PropertyInfo tp = t.GetProperty("timeout");
                    if (tp != null)
                    {
                        tp.SetValue(req, 6, null);
                    }
                }
                catch (Exception) { }
                MethodInfo send = t.GetMethod("SendWebRequest", Type.EmptyTypes);
                if (send == null)
                {
                    _svcStatus = "SendWebRequest 不存在";
                    _svcJsonResult = "!ERR!SendWebRequest 不存在";
                    try { req.GetType().GetMethod("Dispose", Type.EmptyTypes).Invoke(req, null); } catch (Exception) { }
                    return false;
                }
                send.Invoke(req, null);
                _svcReq = req;
                _svcPollStarted = _ticks;
                _svcBusy = true;
                _svcStatus = "请求中...";
                return true;
            }
            catch (Exception ex)
            {
                _svcStatus = "发起失败 " + ex.GetType().Name;
                _svcJsonResult = "!ERR!发起失败 " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>v61: 收掉在途请求, 结果写进 _svcJsonResult 交给 PumpService。</summary>
        private static void FinishServiceRequest(bool completed)
        {
            object req = _svcReq;
            _svcReq = null;
            _svcBusy = false;
            if (req == null)
            {
                return;
            }
            try
            {
                if (!completed)
                {
                    _svcJsonResult = "!ERR!请求超时";
                    return;
                }
                long code = 0;
                try { code = Convert.ToInt64(SvProp(req, "responseCode")); } catch (Exception) { }
                if (code != 200)
                {
                    _svcJsonResult = "!ERR!HTTP " + code;
                    return;
                }
                ulong bytes = 0;
                try { bytes = Convert.ToUInt64(SvProp(req, "downloadedBytes")); } catch (Exception) { }
                if (bytes > 1048576)
                {
                    _svcJsonResult = "!ERR!响应过大";
                    return;
                }
                object dh = SvProp(req, "downloadHandler");
                string text = dh == null ? null : (string)dh.GetType().GetProperty("text").GetValue(dh, null);
                _svcJsonResult = string.IsNullOrEmpty(text) ? "!ERR!空响应" : text;
            }
            catch (Exception ex)
            {
                _svcJsonResult = "!ERR!" + ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                // ★ v74: 先 Abort 再 Dispose。官方 UnityHandRequest.Dispose() 就是这么做的。
                //   只调 Dispose 不会中止底层仍在传输的请求, 于是"手牌关了、对象销毁了,
                //   但那个 UnityWebRequest 还在跑"——表现就是关掉之后照样跳。
                try
                {
                    MethodInfo abort = req.GetType().GetMethod("Abort", Type.EmptyTypes);
                    if (abort != null)
                    {
                        abort.Invoke(req, null);
                    }
                }
                catch (Exception)
                {
                }
                try { req.GetType().GetMethod("Dispose", Type.EmptyTypes).Invoke(req, null); } catch (Exception) { }
            }
        }


        /// <summary>在主线程收结果并解析(OnFrame 调用)。</summary>
        private static void PumpService()
        {
            string json = _svcJsonResult;
            if (json == null)
            {
                return;
            }
            _svcJsonResult = null;
            if (json.StartsWith("!ERR!", StringComparison.Ordinal))
            {
                _svcStatus = json.Substring(5);
                return;
            }
            try
            {
                ApplyServiceJson(json);
            }
            catch (Exception ex)
            {
                _svcStatus = "解析异常 " + ex.GetType().Name;
                _svcLastError = "响应解析失败";
            }
        }

        /// <summary>解析 {"ok":true,"players":[{"id":..,"cards":[id,...]}]} 并写进 SvcHands。</summary>
        private static void ApplyServiceJson(string json)
        {
            object node;
            if (!CesiumLoader.SDK.Configuration.CesiumJson.TryDeserialize(json, out node))
            {
                _svcStatus = "响应解析失败";
                return;
            }
            var map = node as IDictionary<string, object>;
            if (map == null)
            {
                _svcStatus = "响应非对象";
                return;
            }
            bool ok = false;
            object okv;
            if (map.TryGetValue("ok", out okv) && okv is bool)
            {
                ok = (bool)okv;
            }
            if (!ok)
            {
                string err = CesiumLoader.SDK.Configuration.CesiumJson.GetString(map, "err", "");
                object pend;
                bool pending = map.TryGetValue("pending", out pend) && pend is bool && (bool)pend;
                _svcStatus = pending ? "pending(服务正在加入观战)" : ("服务报错 " + err);
                _svcLastError = pending ? "服务正在加入观战" : (string.IsNullOrEmpty(err) ? "服务未就绪" : err);
                return;
            }
            object pv;
            if (!map.TryGetValue("players", out pv))
            {
                _svcStatus = "响应无 players";
                _svcLastError = "服务响应异常";
                return;
            }
            var plist = pv as IList<object>;
            if (plist == null)
            {
                _svcStatus = "players 非数组";
                return;
            }
            var tmp = new Dictionary<long, List<HandCardData>>();
            for (int i = 0; i < plist.Count; i++)
            {
                var pm = plist[i] as IDictionary<string, object>;
                if (pm == null)
                {
                    continue;
                }
                object idv;
                if (!pm.TryGetValue("id", out idv))
                {
                    continue;
                }
                long pid = (long)Num(idv);
                var cards = new List<HandCardData>();
                object cv;
                if (pm.TryGetValue("cards", out cv))
                {
                    var cl = cv as IList<object>;
                    if (cl != null)
                    {
                        for (int k = 0; k < cl.Count; k++)
                        {
                            int cid = (int)Num(cl[k]);
                            if (cid <= 0)
                            {
                                continue;
                            }
                            try
                            {
                                // HandCardData(Id, Guid, PurifyNum, IsTemp, battleCost=-1) → 费用自动取 cfg.Cost
                                cards.Add(new HandCardData(cid, 0, 0, false, -1));
                            }
                            catch (Exception)
                            {
                            }
                        }
                    }
                }
                tmp[pid] = cards;
            }
            lock (SvcHandsLock)
            {
                SvcHands.Clear();
                foreach (KeyValuePair<long, List<HandCardData>> kv in tmp)
                {
                    SvcHands[kv.Key] = kv.Value;
                }
            }
            _svcStatus = "ok(" + tmp.Count + " 人)";
            _svcLastError = "";      // v43: 这次拿到数据了, 清掉上一次的失败原因
            _svcHandsAtTicks = _ticks;   // v44: 记录时效起点(帧计数)
        }

        private static double Num(object v)
        {
            if (v == null)
            {
                return 0;
            }
            try
            {
                if (v is double) { return (double)v; }
                if (v is long) { return (long)v; }
                if (v is int) { return (int)v; }
                return Convert.ToDouble(v);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// v40(打包版): 只读取当前对局的观战码并记进 _lastWatchCode(供 HTTP 查询用),
        /// **不再写任何文件** —— 这样 mod 就是单个 dll, 拷到任何电脑都能跑,
        /// 不会因为硬编码的 F:\ 路径在别人机器上不存在而报错。
        /// 观战码是通过 /hands?code=... 直接传给服务的, 本来就不需要落盘。
        /// </summary>
        private static void WriteWatchTarget()
        {
            try
            {
                GameLogicManager gm = GameLogicManager.inst;
                if (gm == null)
                {
                    return;
                }
                RoomLogic logic = gm.room;
                if (logic == null)
                {
                    return;
                }
                RoomInfo room = logic.curRoomInfo;
                if (room == null)
                {
                    return;
                }
                string code = "";
                int mapType = -1;
                long roomId = 0L;
                try
                {
                    // ★ 2026-10-26 修复: 原来读的是 room.info.WatchCode —— 访问 room.info
                    //   这个懒加载 getter 会让游戏重新拉取/重建房间信息, 表现为
                    //   "界面每 15 秒整体刷新回默认状态"(已实测: 注释掉本函数调用后消失)。
                    //   改用 RoomInfo 自己的缓存字段 watchCode(第 264 / 965 行一直用它, 无副作用)。
                    code = room.watchCode ?? "";
                    // v60: 原来读 room.MapType —— 它是会写游戏状态的 getter
                    //   (RoomInfo.MapType: if (info.MapType == 0) info.MapType = 1;)。
                    //   本函数挂在 _ticks % 30(≈15.5 秒), 与 _handsVisible 无关, 所以
                    //   "关掉手牌窗口后界面仍每十几秒刷一次"就是它。改读 Room 的普通字段。
                    mapType = room.info != null ? room.info.MapType : -1;
                    roomId = room.Id;
                }
                catch (Exception ex)
                {
                    SdkLog.Warn("WatchHand", "读观战码失败: " + ex.GetType().Name);
                }
                _lastWatchCode = code ?? "";
                // 只在观战码变化时打一行日志(方便排查), 不写文件
                if (_lastWatchCode != _lastLoggedCode)
                {
                    _lastLoggedCode = _lastWatchCode;
                    SdkLog.Info("WatchHand", "观战码 = " + (string.IsNullOrEmpty(_lastWatchCode) ? "<空>" : _lastWatchCode)
                        + "  map_type=" + mapType + "  room_id=" + roomId);
                }
            }
            catch (Exception ex)
            {
                SdkLog.Warn("WatchHand", "ReadWatchCode: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>v27: 每 15 秒打一行服务侧状态, 用来定位"为什么没刷新"。</summary>
        private static void DumpServiceDiag()
        {
            try
            {
                GameLogicManager gm = GameLogicManager.inst;
                string gmTxt = gm == null ? "null" : "ok";
                string logicTxt = "-";
                string roomTxt = "-";
                string infoTxt = "-";
                string codeTxt = "-";
                if (gm != null && gm.room != null)
                {
                    logicTxt = "ok";
                    RoomInfo ri = gm.room.curRoomInfo;
                    if (ri != null)
                    {
                        roomTxt = "ok";
                        try
                        {
                            infoTxt = ri.info == null ? "null" : "ok";
                            codeTxt = string.IsNullOrEmpty(ri.watchCode) ? "<空>" : ri.watchCode;
                        }
                        catch (Exception)
                        {
                            infoTxt = "err";
                        }
                    }
                    else
                    {
                        roomTxt = "null";
                    }
                }
                else
                {
                    logicTxt = "null";
                }
                int hands = 0;
                lock (SvcHandsLock)
                {
                    hands = SvcHands.Count;
                }
                SdkLog.Info("WatchHand", "svcDiag ticks=" + _ticks + " gm=" + gmTxt + " logic=" + logicTxt + " room=" + roomTxt
                    + " info=" + infoTxt + " watchCode=" + codeTxt
                    + " | url=" + (string.IsNullOrEmpty(_svcUrl) ? "<未配置>" : _svcUrl)
                    + " lastCode=" + (string.IsNullOrEmpty(_lastWatchCode) ? "<空>" : _lastWatchCode)
                    + " status=" + _svcStatus + " hands=" + hands
                    // v44 诊断: status 每 2 秒会被 PollService 重置成"请求中...", 不能反映真实结果。
                    // 这两个才是判据: 数据多少帧没更新过 / 最近一次失败原因。
                    + " age=" + (_svcHandsAtTicks > 0
                        ? (_ticks - _svcHandsAtTicks).ToString() + "t" : "never")
                    + " busy=" + (_svcBusy ? "1" : "0")
                    + " err=" + (string.IsNullOrEmpty(_svcLastError) ? "-" : _svcLastError));
            }
            catch (Exception ex)
            {
                SdkLog.Warn("WatchHand", "svcDiag 失败: " + ex.GetType().Name);
            }
        }

        /// <summary>
        /// v21 诊断(只读): 把三个可能的手牌来源摆在一起, 判定队友手牌到底有没有下发渠道。
        ///   serverPlayer.Hero.Cards  = 服务器房间数据里的原始手牌(UpdateFromServer 用它重建容器)
        ///   cardContainer._HandCards = 游戏容器(mod 一直在用的, 实测对队友停在开局)
        ///   cardContainer.CardCount  = 独立维护的手牌"数量"(可能单独更新)
        /// 每 15 秒一次, 不看签名。
        /// </summary>
        private static void DumpSources()
        {
            try
            {
                GameLogicManager gm = GameLogicManager.inst;
                RoomInfo room = gm == null || gm.room == null ? null : gm.room.curRoomInfo;
                if (room == null)
                {
                    return;
                }
                List<RoomPlayer> players = room.Players;
                if (players == null || players.Count == 0)
                {
                    return;
                }
                long self = -1;
                try { self = room.GetSelfInfo().Id; } catch (Exception) { }

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < players.Count; i++)
                {
                    RoomPlayer rp = players[i];
                    if (rp == null) { continue; }
                    string heroTxt;
                    try
                    {
                        Player sp = rp.serverPlayer;
                        Hero hero = sp == null ? null : sp.Hero;
                        if (sp == null) { heroTxt = "sp=null"; }
                        else if (hero == null) { heroTxt = "hero=null"; }
                        else
                        {
                            var hc = hero.Cards;
                            if (hc == null) { heroTxt = "heroCards=null"; }
                            else
                            {
                                var hb = new System.Text.StringBuilder();
                                for (int k = 0; k < hc.Count; k++)
                                {
                                    if (k > 0) { hb.Append(','); }
                                    hb.Append(hc[k] == null ? 0 : hc[k].CardId);
                                }
                                heroTxt = "heroCards=" + hc.Count + "[" + hb.ToString() + "]";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        heroTxt = "hero=err(" + ex.GetType().Name + ")";
                    }

                    CardContainer cc = null;
                    try { cc = rp.cardContainer; } catch (Exception) { }
                    int cnt = -9;
                    try { cnt = cc == null ? -1 : cc.CardCount; } catch (Exception) { }
                    int n = -1;
                    var cb = new System.Text.StringBuilder();
                    try
                    {
                        if (cc != null && cc._HandCards != null)
                        {
                            n = cc._HandCards.Count;
                            for (int k = 0; k < n; k++)
                            {
                                if (k > 0) { cb.Append(','); }
                                cb.Append(cc._HandCards[k] == null ? 0 : cc._HandCards[k].CardId);
                            }
                        }
                    }
                    catch (Exception) { }

                    sb.Append(rp.Id == self ? "SELF" : "TEAM").Append('#').Append(rp.Id)
                      .Append(" isBot=").Append(rp.IsBot)
                      .Append(" {").Append(heroTxt).Append("} container=").Append(n)
                      .Append('[').Append(cb.ToString()).Append("] count=").Append(cnt)
                      .Append("  ");
                }
                SdkLog.Info("WatchHand", "sources " + sb.ToString());
            }
            catch (Exception ex)
            {
                SdkLog.Warn("WatchHand", "DumpSources: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // =====================================================================
        // v59 探针实现(2026-10-27): 旁路观测, 不改游戏行为
        // =====================================================================

        /// <summary>
        /// 把 OnlineSyncRoomIdS2C / SyncRoomS2C 的游戏回调包一层, 原回调一律原样转发。
        /// 游戏每场战斗会重置 RPC 回调, 所以每秒重挂一次(幂等: 已是自己的就不重复挂)。
        /// </summary>
        private static void HookSyncProbes()
        {
            try
            {
                NetManager net = MonoSingletonProvider<NetManager>.inst;
                if (net == null || net.RPC == null)
                {
                    return;
                }
                OnlineSyncRoomIdS2CRPC a = net.RPC.OnlineSyncRoomIdS2C;
                if (a != null && a.OnOnlineSyncRoomIdS2CServerCallBackAsync != _probeMineOnlineSync)
                {
                    _probeOrigOnlineSync = a.OnOnlineSyncRoomIdS2CServerCallBackAsync;
                    _probeMineOnlineSync = ProbeOnlineSync;
                    a.OnOnlineSyncRoomIdS2CServerCallBackAsync = _probeMineOnlineSync;
                    if (_probeHookLogs < 8)
                    {
                        _probeHookLogs++;
                        SdkLog.Info("WatchHand", "[probe] 已挂 OnlineSyncRoomIdS2C, 原回调="
                            + (_probeOrigOnlineSync == null ? "<null>" : "有"));
                    }
                }
                SyncRoomS2CRPC b = net.RPC.SyncRoomS2C;
                if (b != null && b.OnSyncRoomS2CServerCallBackAsync != _probeMineSyncRoom)
                {
                    _probeOrigSyncRoom = b.OnSyncRoomS2CServerCallBackAsync;
                    _probeMineSyncRoom = ProbeSyncRoom;
                    b.OnSyncRoomS2CServerCallBackAsync = _probeMineSyncRoom;
                    if (_probeHookLogs < 8)
                    {
                        _probeHookLogs++;
                        SdkLog.Info("WatchHand", "[probe] 已挂 SyncRoomS2C, 原回调="
                            + (_probeOrigSyncRoom == null ? "<null>" : "有"));
                    }
                }
            }
            catch (Exception ex)
            {
                LogOnce("probeHook: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static UniTask ProbeOnlineSync(OnlineSyncRoomIdS2C model, int errId, bool isDispatch)
        {
            try
            {
                _probeOnlineSyncCount++;
                SdkLog.Info("WatchHand", "★★[probe] OnlineSyncRoomIdS2C #" + _probeOnlineSyncCount
                    + " tick=" + _ticks
                    + " roomId=" + (model == null ? 0L : model.RoomId)
                    + " err=" + errId);
            }
            catch (Exception)
            {
            }
            OnlineSyncRoomIdS2CRPC.OnOnlineSyncRoomIdS2CServerDelegate orig = _probeOrigOnlineSync;
            if (orig == null)
            {
                return UniTask.CompletedTask;
            }
            try
            {
                return orig(model, errId, isDispatch);
            }
            catch (Exception ex)
            {
                try
                {
                    SdkLog.Warn("WatchHand", "[probe] 原 OnlineSync 回调抛出: "
                        + ex.GetType().Name + ": " + ex.Message);
                }
                catch (Exception)
                {
                }
                return UniTask.CompletedTask;
            }
        }

        private static UniTask ProbeSyncRoom(SyncRoomS2C model, int errId, bool isDispatch)
        {
            try
            {
                _probeSyncRoomCount++;
                SdkLog.Info("WatchHand", "★★[probe] SyncRoomS2C #" + _probeSyncRoomCount
                    + " tick=" + _ticks
                    + " roomId=" + (model == null || model.Room == null ? 0L : model.Room.Id)
                    + " err=" + errId);
            }
            catch (Exception)
            {
            }
            SyncRoomS2CRPC.OnSyncRoomS2CServerDelegate orig = _probeOrigSyncRoom;
            if (orig == null)
            {
                return UniTask.CompletedTask;
            }
            try
            {
                return orig(model, errId, isDispatch);
            }
            catch (Exception ex)
            {
                try
                {
                    SdkLog.Warn("WatchHand", "[probe] 原 SyncRoom 回调抛出: "
                        + ex.GetType().Name + ": " + ex.Message);
                }
                catch (Exception)
                {
                }
                return UniTask.CompletedTask;
            }
        }

        /// <summary>每 tick 采样: GRoot 子节点数变化 + UIBattleInfoPanel 实例变化(有上限, 防刷屏)。</summary>
        private static void SampleProbeFingerprint()
        {
            try
            {
                GComponent root = GRoot.inst;
                if (root != null)
                {
                    int nc = root.numChildren;
                    if (nc != _probeLastNumChildren)
                    {
                        if (_probeUiLogs < 120)
                        {
                            _probeUiLogs++;
                            SdkLog.Info("WatchHand", "[probe] GRoot.numChildren " + _probeLastNumChildren
                                + " -> " + nc + " tick=" + _ticks
                                + " syncRoom=" + _probeSyncRoomCount
                                + " onlineSync=" + _probeOnlineSyncCount);
                        }
                        _probeLastNumChildren = nc;
                    }
                }
                UIBattleInfoPanel p = _panelCache;
                if (!object.ReferenceEquals(p, _probeLastPanel))
                {
                    if (_probeUiLogs < 120)
                    {
                        _probeUiLogs++;
                        SdkLog.Info("WatchHand", "[probe] UIBattleInfoPanel 实例变化 -> "
                            + (p == null ? "<null>" : "新实例") + " tick=" + _ticks);
                    }
                    _probeLastPanel = p;
                }
            }
            catch (Exception)
            {
            }
        }

        public static void Main()
        {
            // v68: 恢复 tick 注册。v67 空壳实验作废 —— 源码当时被改成空壳, v68 加开关时
            // 忘了改回来, 导致 v68 编出来也是空壳(游戏里没按钮、什么也不做)。现修正。
            ModBase.Run(OnInit, OnTick, tag: "WatchHand");
        }

        private static void OnInit()
        {
            SdkLog.Info("WatchHand", "=== WatchHand v1.1 (发布版: json 配置; Q 键开关; 手牌可见时 2.1 秒轮询) ===");
            SdkLog.Info("WatchHand", "探针说明: F9=用当前观战码请求整房快照(含四人手牌); F10=直接发刷新请求");
            // ★ 2026-10-26 诊断实验 #1: 不订阅 GameEvents, 并置 _hookedEvents = false
            //    → OnTick 里的 GameEvents.EnsureHooked() 也不会再被调用。
            //    目的: 完全切断本 mod 与游戏 RPC 回调(RoomHeroCardChangeS2C / UpdateHeroAttrS2C)
            //    的接触, 验证"界面周期性重置 / 点 A 落到 B"是否由 SDK 包装游戏回调引起。
            //    影响: LiveHands 不再填充, handEvent / attrEvent 日志消失;
            //          手牌主源 SvcHands(服务端快照轮询)不受影响, 服务端正常时显示不变。
            try
            {
                // GameEvents.HandChanged += OnHandChanged;
                // GameEvents.HeroAttrUpdated += OnHeroAttrUpdated;
                _hookedEvents = false;
                SdkLog.Info("WatchHand", "诊断实验#1: 跳过 GameEvents 订阅, EnsureHooked 将不再调用");
            }
            catch (Exception exh)
            {
                SdkLog.Warn("WatchHand", "订阅 HandChanged 失败: " + exh.GetType().Name + ": " + exh.Message);
            }
            // ★ 2026-10-27 v79: 恢复每帧回调注册 —— 这是 Q 键能不能用的关键。
            //   历史: v70 注释掉这行、把开关改成屏幕按钮; v71 按用户要求"恢复 Q 键"
            //   时只恢复了 OnFrame 里面的读键代码, 却漏了这里的注册。结果 v71~v78
            //   的 Q 键代码一直存在, 但 OnFrame 从未被调用(实测日志里一条
            //   "Q 键路径 = ..." / "KeyQ: ..." 都没有), Q 键完全不通。
            //   v78 又把屏幕按钮全部删掉, 于是"没有任何开关入口"。
            //   现在把注册补回来: OnFrame 每帧只做一件事 —— 读一次 Q 键的边沿,
            //   原生 Input.GetKeyDown 无分配, 开销可忽略。
            try
            {
                Timers.inst.Add(0f, 0, OnFrame);
                SdkLog.Info("WatchHand", "已注册每帧回调(OnFrame): Q 键开关生效");
            }
            catch (Exception ex)
            {
                SdkLog.Warn("WatchHand", "Timers.Add failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            // v59: 挂 OnlineSyncRoomIdS2C / SyncRoomS2C 探针(只记录, 原回调照常转发)
            HookSyncProbes();
        }

        /// <summary>
        /// 每帧回调(Timers.inst.Add(0f, 0, ...))。v64 起这里**只做 Q 键边沿检测** ——
        /// 键必须每帧采样, 否则会漏按。其余两件事(PumpService 收 HTTP / tooltip 置顶)
        /// 已移到 OnTick。
        /// </summary>
        private static void OnFrame(object param)
        {
            try
            {
                // ★ 2026-10-27 v65 (二分): 不再用 SDK 反射读键。
                //   每帧回调里现在只剩"读一次 Q 键"这一个动作, 而它原来是
                //   InputService.IsKeyDown —— 走 SDK 反射调 Input.GetKey, 每帧 60 次
                //   MethodInfo.Invoke(有分配, 持续制造 GC)。改为直接调
                //   UnityEngine.Input.GetKeyDown: 原生调用、不分配, 而且它本身就是
                //   "按下那一帧"的边沿, 不需要 _keyQHeld 自己判。
                //   若 GetKeyDown 在本机不可用(不抛异常但恒为 false), 回退到原反射路径。
                bool down = false;
                bool nativeOk = false;
                try
                {
                    down = UnityEngine.Input.GetKeyDown(KeyCode.Q);
                    nativeOk = true;
                }
                catch (Exception)
                {
                }
                // 第一帧就判定走的是哪条路, 只打一次 —— 否则"还跳"时分不清
                // 是原生路径没生效, 还是根因不在读键。
                if (nativeOk)
                {
                    if (_keyPath != 1)
                    {
                        _keyPath = 1;
                        SdkLog.Info("WatchHand", "Q 键路径 = 原生 UnityEngine.Input.GetKeyDown");
                    }
                }
                else
                {
                    if (_keyPath != 2)
                    {
                        _keyPath = 2;
                        SdkLog.Info("WatchHand", "Q 键路径 = InputService 反射 fallback (GetKeyDown 不可用)");
                    }
                    bool held = false;
                    try
                    {
                        held = InputService.IsKeyDown(KeyCode.Q);
                    }
                    catch (Exception)
                    {
                    }
                    down = held && !_keyQHeld;
                    _keyQHeld = held;
                }
                if (down)
                {
                    _handsVisible = !_handsVisible;
                    if (!_handsVisible)
                    {
                        try
                        {
                            HideAll();
                        }
                        catch (Exception)
                        {
                        }
                    }
                    else
                    {
                        // v77: Q 键现在是唯一的开关入口(屏幕正中的按钮已删除)。
                        //   打开时立即刷新一次, 与原来按钮点击的行为对齐; 顺便让
                        //   _lastWatchCode 立刻有值, 后面的 2.1 秒轮询才能马上开始
                        //   (WriteWatchTarget 本身只有每 30 tick 一次)。
                        try
                        {
                            RequestHandsOnce();
                        }
                        catch (Exception)
                        {
                        }
                    }
                    SdkLog.Info("WatchHand", "KeyQ: 队友手牌显示 = " + _handsVisible);
                }
            }
            catch (Exception)
            {
            }
        }

        private static void OnTick()
        {
            _ticks++;
            // ★ 2026-10-27 v70: 在途请求的清理必须**无条件**执行。
            //   原来只有在 PollService 里才会调 FinishServiceRequest, 而 PollService 被
            //   开关门控挡住。于是"请求还在途时把 P 关掉"(或关掉 B 且手牌不显示) 会让
            //   _svcReq 永远不为 null、那个 UnityWebRequest 对象永远不被 Dispose ——
            //   日志里就是 poll # 之后再无任何收尾。官方的写法是 SetViewing(false) 里
            //   无条件 DisposeRequest(), 不看别的开关。这里补上同样的语义。
            try
            {
                if (_svcReq != null)
                {
                    // ★ v75: 恢复"完成即收"。
                    //   v72 把自动轮询挪到 _swBgReq 开关后面时, 顺手把唯一会检查 isDone 的
                    //   PollService 也一起关掉了, 而 _swBgReq 默认 false。结果 v74 里每个请求
                    //   都没人检查完成, 一律挂满 PollHardTimeoutTicks(≈9.8 秒) 才被当成
                    //   "请求超时"丢掉, completed 恒为 false, 响应一次都读不到。
                    //   实测症状与之一致: 打开手牌后"跳"约 10 秒后自己停 —— 那 10 秒就是这条
                    //   硬超时。修好后在途窗口应等于真实响应时间。
                    bool done = false;
                    bool probeOk = true;
                    try
                    {
                        done = (bool)SvProp(_svcReq, "isDone");
                    }
                    catch (Exception)
                    {
                        probeOk = false;   // 反射失败不误判为完成, 退回原来的超时/禁用路径
                    }
                    bool stale = _svcPollStarted > 0
                        && (_ticks - _svcPollStarted) > PollHardTimeoutTicks;
                    bool notAllowed = !(_swPoll && (_handsVisible || _swBgReq));
                    if (probeOk && done)
                    {
                        FinishServiceRequest(true);
                    }
                    else if (stale || notAllowed)
                    {
                        // v73: 实测 UnityWebRequest.timeout 在本机不生效(请求能挂 20+ 秒
                        //      仍在途), 所以必须用自己的帧计数做硬超时, 否则这个请求会
                        //      一直占着 Unity 的 WebRequest 不释放。
                        SdkLog.Info("WatchHand", (stale ? "请求超时(帧计数), 丢弃" : "已不允许发请求, 丢弃在途请求")
                            + "(Dispose)");
                        FinishServiceRequest(false);
                    }
                }
            }
            catch (Exception)
            {
            }
            // ★ 2026-10-27 v63: 原先这三件事在每帧的 OnFrame 里(每秒 60 次), 现统一搬到
            //   这里(0.5 秒一次)。目的: 彻底去掉每帧回调, 二分验证"界面周期性重置"的
            //   来源。PumpService 晚 0.5 秒收结果、tooltip 晚 0.5 秒置顶, 都无感。
            try
            {
                if (_swPoll)
                {
                    PumpService();
                }
            }
            catch (Exception)
            {
            }
            try
            {
                if (_tip != null && !_tip.isDisposed && _tip.visible)
                {
                    GComponent tipParent = _tip.parent as GComponent;
                    if (tipParent != null)
                    {
                        int tipLast = tipParent.numChildren - 1;
                        if (tipLast > 0 && tipParent.GetChildIndex(_tip) != tipLast)
                        {
                            tipParent.SetChildIndex(_tip, tipLast);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            if (_ticks < 3)
            {
                return;
            }
            // v68: 探针各有开关, 用来逐项定位"跳"的来源。
            if (_swHook && _ticks % 2 == 0)
            {
                HookSyncProbes();
            }
            if (_swDiag)
            {
                SampleProbeFingerprint();
            }
            // v68: 手牌按钮 + 诊断开关的维护。不受"渲染"开关影响, 否则关掉渲染就点不到了。
            if (_ticks % 6 == 0)
            {
                EnsureUiButtons();
            }
            // v36: 从 OnTick 开头开始计时(诊断卡顿)
            int pStart = Environment.TickCount;
            // v20: SDK 的 RPC 挂钩需要续挂(游戏每场战斗会重置回调)。
            // v35: 原来是**每帧**调用, 实测持续掉帧 —— 改成每 30 帧(约 0.5 秒)一次。
            if (_hookedEvents && _ticks % 30 == 0)
            {
                try
                {
                    GameEvents.EnsureHooked();
                }
                catch (Exception)
                {
                }
            }
            int p1 = Environment.TickCount;
            // v24: 把网络线程收集的 attrEvent 记录在主线程打出来
            // v35: 同样降频(原来每帧)
            if (_hookedEvents && _ticks % 5 == 0)
            {
                try
                {
                    FlushAttrLog();
                }
                catch (Exception)
                {
                }
            }
            int p2 = Environment.TickCount;
            // v25: 把当前对局的观战码写出来, 给外部观战服务用(它用第二个账号进观战拿四人手牌)。
            // 取代了 v22~v24 那个"按 F9 发观战请求"的做法 —— 那个会让游戏客户端异常。
            // v35: 观战码变化很慢, 每 30 帧(0.5 秒)写一次足够, 减少文件 IO
            if (_swWrite && _ticks % 30 == 0)
            {
                try
                {
                    // 2026-10-26: 诊断实验 #2 已确认 —— 注释掉它之后界面不再周期刷新,
                    // 元凶是函数里的 room.info 访问(现已改为 room.watchCode), 故恢复调用。
                    WriteWatchTarget();
                }
                catch (Exception ex)
                {
                    SdkLog.Warn("WatchHand", "tick30 WriteWatchTarget 抛出: " + ex.GetType().Name
                        + ": " + ex.Message);
                }
            }
            // v26: 观战服务轮询
            // v76 (2026-10-27): 恢复"手牌可见时轮询", 间隔约 2.1 秒(4 tick)。
            //   ① 必要性(日志实测 05:50~05:52 那次): 只发一次请求时, 服务端还在 join
            //      就返回 pending(界面显示"服务正在加入观战"), 之后没人重试
            //      -> 手牌永远出不来(svcDiag: hands=0 age=never busy=0
            //      err=服务正在加入观战)。
            //   ② 安全性: 服务端 v1.3 已加空闲退出(IDLE_EXIT_SEC=5s) —— 客户端一停请求,
            //      5 秒内就 WatchExitRoomC2S 退出观战并清空 wanted, 所以"关掉手牌还跳"
            //      已经由服务端解决; 客户端可以放心在"手牌可见"期间持续请求。
            //   间隔必须明显小于 IDLE_EXIT_SEC, 否则会反复"退出-重进"。
            if (_ticks % 4 == 0)
            {
                try
                {
                    RefreshServiceConfig();
                    if (_swPoll
                        && (_handsVisible || _swBgReq)
                        && !string.IsNullOrEmpty(_svcUrl)
                        && !string.IsNullOrEmpty(_lastWatchCode))
                    {
                        PollService(_lastWatchCode);
                    }
                }
                catch (Exception ex)
                {
                    SdkLog.Warn("WatchHand", "tick4 RefreshServiceConfig 抛出: " + ex.GetType().Name
                        + ": " + ex.Message);
                }
            }
            // v21: 把三个手牌数据源打出来对比(只读诊断) —— v35: 每 60 帧(约 1 秒)一次
            if (_swDiag && _ticks % 60 == 0)
            {
                try
                {
                    DumpSources();
                }
                catch (Exception)
                {
                }
                try
                {
                    DumpServiceDiag();
                }
                catch (Exception)
                {
                }
            }
            // v35: 渲染每 6 帧(约 0.1 秒)一次 —— 原来每帧跑, 是掉帧的第二大来源。
            // 0.1 秒的显示延迟肉眼无感。
            // v36: 计时诊断 —— 连续两版降频都没解决, 不再猜, 直接量每部分耗时(毫秒)。
            int t0 = Environment.TickCount;
            if (_swRender && _ticks % 6 == 0)
            {
                try
                {
                    Update();
                }
                catch (Exception ex)
                {
                    LogOnce("update: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            int tA = Environment.TickCount;
            try
            {
                UpdateTooltip();
            }
            catch (Exception ex)
            {
                LogOnce("tooltip: " + ex.GetType().Name + ": " + ex.Message);
            }
            int tB = Environment.TickCount;
            if (_swDiag && _ticks % 5 == 0)
            {
                SdkLog.Info("WatchHand", "prof tick=" + _ticks + " hooked=" + (p1 - pStart) + "ms attrLog=" + (p2 - p1) + "ms rest=" + (t0 - p2) + "ms update=" + (tA - t0) + "ms tooltip=" + (tB - tA) + "ms stripHit=" + _stripHits + " stripRebuild=" + _stripRebuilds);
            }
        }

        private static void LogOnce(string msg)
        {
            if (msg == _lastError)
            {
                return;
            }
            _lastError = msg;
            SdkLog.Warn("WatchHand", msg);
        }

        // =====================================================================
        // 手牌卡图
        // =====================================================================

        /// <summary>
        /// v66: 屏幕上的"手牌"开关按钮, 取代原来的 Q 键。
        /// 原因: Q 键要求每帧轮询来做边沿检测, 而每帧回调正是本 mod 与官方 HandViewerMod
        /// 最大的结构差异 —— 官方全程 onClick 事件驱动, 从不注册每帧回调。
        /// 按钮走 FairyGUI 的 onClick, 零轮询。
        /// </summary>
        private static void EnsureToggle(UIBattleInfoPanel panel)
        {
            GComponent host = (GComponent)panel;
            if (_toggle != null && !_toggle.isDisposed && _toggle.parent == host)
            {
                if (_toggleText != null && !_toggleText.isDisposed)
                {
                    string want = _handsVisible ? "手牌 ▲" : "手牌 ▼";
                    if (_toggleText.text != want)
                    {
                        _toggleText.text = want;
                    }
                }
                return;
            }
            if (_toggle != null)
            {
                try
                {
                    if (_toggle.parent != null)
                    {
                        _toggle.parent.RemoveChild(_toggle);
                    }
                    _toggle.Dispose();
                }
                catch (Exception)
                {
                }
                _toggle = null;
                _toggleText = null;
            }
            try
            {
                GComponent box = new GComponent();
                box.opaque = true;
                box.touchable = true;
                box.SetSize(ToggleW, ToggleH);
                GGraph bg = new GGraph();
                bg.SetSize(ToggleW, ToggleH);
                bg.DrawRect(ToggleW, ToggleH, 1, Color.white, new Color(0.06f, 0.09f, 0.16f, 0.88f));
                bg.touchable = false;
                box.AddChild(bg);
                GTextField txt = new GTextField();
                txt.text = _handsVisible ? "手牌 ▲" : "手牌 ▼";
                txt.singleLine = true;
                // 必须取现有 textFormat 改再赋回(结构体); new TextFormat() 会清空 font 导致不渲染
                TextFormat fmt = txt.textFormat;
                fmt.size = 13;
                fmt.color = Color.white;
                txt.textFormat = fmt;
                txt.touchable = false;
                box.AddChild(txt);
                box.onClick.Add(delegate(EventContext ctx)
                {
                    ctx.StopPropagation();
                    _handsVisible = !_handsVisible;
                    if (!_handsVisible)
                    {
                        try
                        {
                            HideAll();
                        }
                        catch (Exception)
                        {
                        }
                    }
                    else
                    {
                        // v72: 每次打开手牌只发一次请求, 不再定时轮询。
                        //      "发请求"这个动作本身会让游戏画面周期性重置, 官方靠
                        //      "只在展开时发一次 + 5 秒收回"避开, 这里采用同样策略。
                        //      想刷新就关掉再打开。
                        RequestHandsOnce();
                    }
                    SdkLog.Info("WatchHand", "手牌按钮: 显示 = " + _handsVisible);
                });
                host.AddChild(box);
                // 定位到屏幕右上角: 先用 GRoot 坐标算, 再转成面板局部坐标,
                // 这样不依赖面板自身尺寸/原点。
                // v68: 挪到屏幕正中(用户指定)。
                Vector2 gp = GRoot.inst.LocalToGlobal(
                    new Vector2(GRoot.inst.width * 0.5f - ToggleW * 0.5f,
                                GRoot.inst.height * 0.5f - 83f));
                Vector2 lp = host.GlobalToLocal(gp);
                box.SetXY(lp.x, lp.y);
                _toggle = box;
                _toggleText = txt;
            }
            catch (Exception ex)
            {
                SdkLog.Warn("WatchHand", "EnsureToggle: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>v68: 右上角一排诊断开关, 点一下切换对应行为。</summary>
        private static void EnsureSwitches(UIBattleInfoPanel panel)
        {
            GComponent host = (GComponent)panel;
            if (_swBox != null && !_swBox.isDisposed && _swBox.parent == host)
            {
                for (int i = 0; i < 6; i++)
                {
                    if (_swTexts[i] != null && !_swTexts[i].isDisposed)
                    {
                        string want = SwNames[i] + (SwGet(i) ? " 开" : " 关");
                        if (_swTexts[i].text != want)
                        {
                            _swTexts[i].text = want;
                        }
                    }
                }
                return;
            }
            if (_swBox != null)
            {
                try
                {
                    if (_swBox.parent != null)
                    {
                        _swBox.parent.RemoveChild(_swBox);
                    }
                    _swBox.Dispose();
                }
                catch (Exception)
                {
                }
                _swBox = null;
            }
            try
            {
                GComponent box = new GComponent();
                box.opaque = false;
                // v68: 父容器本身也要可触摸 —— 设成 false 时子按钮的点击收不到,
                //      表现就是"开关点不动/关不掉"。
                box.touchable = true;
                box.SetSize(ToggleW, ToggleH * 6f + 20f);
                for (int i = 0; i < 6; i++)
                {
                    int idx = i;
                    GComponent btn = new GComponent();
                    btn.opaque = true;
                    btn.touchable = true;
                    btn.SetSize(ToggleW, ToggleH);
                    GGraph bg = new GGraph();
                    bg.SetSize(ToggleW, ToggleH);
                    bg.DrawRect(ToggleW, ToggleH, 1, Color.white, new Color(0.06f, 0.09f, 0.16f, 0.88f));
                    bg.touchable = false;
                    btn.AddChild(bg);
                    GTextField t = new GTextField();
                    t.text = SwNames[idx] + (SwGet(idx) ? " 开" : " 关");
                    t.singleLine = true;
                    TextFormat fmt = t.textFormat;
                    fmt.size = 12;
                    fmt.color = Color.white;
                    t.textFormat = fmt;
                    t.touchable = false;
                    btn.AddChild(t);
                    btn.SetXY(0f, (float)idx * (ToggleH + 4f));
                    btn.onClick.Add(delegate(EventContext ctx)
                    {
                        ctx.StopPropagation();
                        SwToggle(idx);
                    });
                    box.AddChild(btn);
                    _swTexts[idx] = t;
                }
                host.AddChild(box);
                Vector2 gp = GRoot.inst.LocalToGlobal(
                    new Vector2(GRoot.inst.width * 0.5f - ToggleW * 0.5f,
                                GRoot.inst.height * 0.5f - 83f + ToggleH + 6f));
                Vector2 lp = host.GlobalToLocal(gp);
                box.SetXY(lp.x, lp.y);
                _swBox = box;
            }
            catch (Exception ex)
            {
                SdkLog.Warn("WatchHand", "EnsureSwitches: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>v68: 维护"手牌"按钮与诊断开关。由 OnTick 每 6 tick 无条件调用,
        /// 不依赖 _swRender —— 否则关掉渲染后按钮就不再更新了。</summary>
        private static void EnsureUiButtons()
        {
            try
            {
                // v78 (2026-10-27): 用户要求把屏幕上的鼠标按钮组全部去掉 ——
                //   "手牌 ▲/▼"按钮(v77 已删) 和 6 个诊断开关(H/W/P/R/D/B) 都不再创建。
                //   手牌开关改由 Q 键独占(OnFrame 里的 UnityEngine.Input.GetKeyDown(KeyCode.Q));
                //   诊断开关对应的行为仍按字段默认值生效, 只是界面上不能再点。
                //   这里只负责清理旧版可能残留的对象, 不创建任何 UI。
                RemoveIfAlive(ref _toggle);
                RemoveIfAlive(ref _swBox);
                _toggleText = null;
                for (int i = 0; i < _swTexts.Length; i++)
                {
                    _swTexts[i] = null;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>v78: 把一个 FairyGUI 组件从父节点摘掉并销毁, 然后把引用置空。</summary>
        private static void RemoveIfAlive(ref GComponent go)
        {
            try
            {
                if (go == null)
                {
                    return;
                }
                if (!go.isDisposed && go.parent != null)
                {
                    go.parent.RemoveChild(go);
                }
                if (!go.isDisposed)
                {
                    go.Dispose();
                }
            }
            catch (Exception)
            {
            }
            go = null;
        }

        /// <summary>
        /// v72: 手动刷新一次手牌 —— 只在"打开手牌"这一刻调用, 不做任何定时轮询。
        /// 原因: 实测"发起 HTTP 请求"这个动作本身会让游戏画面周期性重置(选中框消失/
        /// 视角回自己), 与请求成功还是失败无关; 官方 HandViewerMod 靠"只在展开时发一次"
        /// 把这个影响压到最低。这里照同样的策略: 想刷新就关掉手牌再打开。
        /// </summary>
        private static void RequestHandsOnce()
        {
            try
            {
                WriteWatchTarget();      // 先确保 _lastWatchCode 是最新的
                if (string.IsNullOrEmpty(_svcUrl) || string.IsNullOrEmpty(_lastWatchCode))
                {
                    SdkLog.Info("WatchHand", "手动刷新: 还没有观战码, 跳过");
                    return;
                }
                if (_svcReq != null)
                {
                    FinishServiceRequest(false);   // 丢掉旧的(可能还挂着的)请求
                }
                _svcLastPoll = 0;                  // 绕开 4 tick 节流
                SdkLog.Info("WatchHand", "手动刷新: 发一次请求 code=" + _lastWatchCode);
                StartServiceRequest(_lastWatchCode);
            }
            catch (Exception ex)
            {
                SdkLog.Warn("WatchHand", "RequestHandsOnce: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Update()
        {
            // v68: 按钮/开关维护已移到 OnTick 的 EnsureUiButtons()(每 6 tick 无条件执行),
            //      这样即使关掉"渲染"开关, 开关按钮本身仍可点、文字仍会刷新。
            // v39: 默认不画队友手牌(渲染开销为 0); 按钮点开后才走后面的绘制。
            if (!_handsVisible)
            {
                HideAll();
                return;
            }
            GameLogicManager gm = GameLogicManager.inst;
            if (gm == null)
            {
                HideAll();
                return;
            }
            RoomLogic logic = gm.room;
            RoomInfo room = logic == null ? null : logic.curRoomInfo;
            if (room == null)
            {
                HideAll();
                return;
            }
            List<RoomPlayer> players = room.Players;
            if (players == null || players.Count == 0)
            {
                HideAll();
                return;
            }

            UIBattleInfoPanel panel = PanelCached();
            if (panel == null)
            {
                HideAll();
                return;
            }
            UIBattleInfo_Com_PlayerContainer cont = panel.com_BattlePlayer == null
                ? null : panel.com_BattlePlayer.com_Container;
            if (cont == null)
            {
                HideAll();
                return;
            }

            GComponent[] owners =
            {
                cont.com_Player_1, cont.com_Player_2, cont.com_Player_3, cont.com_Player_4
            };

            if (!_mountLogged)
            {
                _mountLogged = true;
                SdkLog.Info("WatchHand", string.Format(
                    "HUD 就绪: owners={0}/{1}/{2}/{3}  players={4}",
                    owners[0] != null, owners[1] != null, owners[2] != null, owners[3] != null,
                    players.Count));
            }

            // 提示框挂 GRoot 根容器 —— 关键原因:
            // 卡图分散在 com_Player_1..4 四个"并列"容器里(各自 idx=3/4 之类),
            // 提示框挂其中任何一个, 都只在那个容器内靠上, 会被并列的其它玩家容器压住。
            // 只有挂到根容器 + 每帧 SetChildIndex 到最后, 才是真正的全局最上层。
            GComponent host = GRoot.inst;
            _hud = host;
            if (_tip != null && !_tip.isDisposed && _tip.parent != host)
            {
                host.AddChild(_tip);
                _tipShown = null;
            }

            int n = players.Count < 4 ? players.Count : 4;
            for (int i = 0; i < n; i++)
            {
                GComponent owner = owners[i];
                RoomPlayer rp = players[i];
                if (owner == null || rp == null)
                {
                    continue;
                }
                CardContainer cc = rp.cardContainer;
                // v20: 队友手牌取"服务器实时快照"优先(GameEvents.HandChanged),
                // 没有才回退游戏容器(实测对队友只有开局快照)
                IReadOnlyList<HandCardData> cards = CardsOf(rp, cc);
                GComponent strip = EnsureStrip(i, owner, panel);
                if (strip == null)
                {
                    continue;
                }
                if (cards == null)
                {
                    // v43: 取不到手牌 —— 明确显示失败, 不回退开局快照(那会误导成实时数据)
                    string why = "手牌加载失败";
                    if (!string.IsNullOrEmpty(_svcLastError))
                    {
                        why = "手牌加载失败: " + _svcLastError;
                    }
                    else
                    {
                        // v44: 没有明确错误, 但有"过期"的时间戳 -> 说明服务停止更新了(帧计数判定)
                        long ageTicks2 = _ticks - _svcHandsAtTicks;
                        if (_svcHandsAtTicks > 0 && ageTicks2 > SvcStaleTicks)
                        {
                            why = "手牌加载失败: 服务已停止更新("
                                + (ageTicks2 / 2) + "s)";
                        }
                    }
                    if (LastSig[i] != why)
                    {
                        LastSig[i] = why;
                        RenderUnavailable(strip, why);
                    }
                    continue;
                }

                string sig = Signature(cards);
                if (sig == LastSig[i])
                {
                    continue;
                }
                LastSig[i] = sig;
                DumpHands(rp, cards);   // 诊断: 打印该玩家每张卡的 CardId(负数=服务器掩码, 拿不到)
                RenderStrip(strip, rp, cards);
            }

            RebuildSpotsIfDue();

            // 父链诊断放在这里: 此时 strip 一定已经创建(之前放在前面, 拿到的是空值)
            if (!_chainLogged && Strips[0] != null && !Strips[0].isDisposed)
            {
                _chainLogged = true;
                try
                {
                    var chain = new System.Text.StringBuilder();
                    GObject node = Strips[0];
                    int depth = 0;
                    while (node != null && depth < 14)
                    {
                        if (depth > 0)
                        {
                            chain.Append("  <  ");
                        }
                        chain.Append(node.GetType().Name).Append("(name='").Append(node.name ?? "").Append('\'');
                        GComponent pc = node.parent as GComponent;
                        if (pc != null)
                        {
                            chain.Append(" idx=").Append(pc.GetChildIndex(node)).Append('/').Append(pc.numChildren);
                        }
                        chain.Append(')');
                        node = node.parent;
                        depth++;
                    }
                    SdkLog.Info("WatchHand", "strip 父链(自身→根): " + chain.ToString());
                    SdkLog.Info("WatchHand", "hud=" + panel.GetType().Name
                        + " hudIdx=" + (panel.parent is GComponent
                            ? ((GComponent)panel.parent).GetChildIndex(panel).ToString() : "?")
                        + " tipParent=" + (_tip == null || _tip.parent == null
                            ? "<null>" : _tip.parent.GetType().Name)
                        + " tipIdx=" + (_tip == null || !(_tip.parent is GComponent) ? "?"
                            : ((GComponent)_tip.parent).GetChildIndex(_tip).ToString()));
                }
                catch (Exception ex6)
                {
                    SdkLog.Warn("WatchHand", "chain probe: " + ex6.Message);
                }
            }
        }

        private static void HideAll()
        {
            for (int i = 0; i < Strips.Length; i++)
            {
                GComponent s = Strips[i];
                if (s != null && !s.isDisposed)
                {
                    s.visible = false;
                    // ★ 2026-10-27 v62: 彻底销毁, 不是只摘掉。
                    //   之前只 RemoveChild 不 Dispose: 对象连同里面 24 张卡一直泄漏,
                    //   日志里 "strip 重建 #N why=父容器不是 GRoot: 现 parent='<null>'" 就是它,
                    //   而且下一轮 EnsureStrip 又会 new 一个, 反复累积。
                    try
                    {
                        if (s.parent != null)
                        {
                            s.parent.RemoveChild(s);
                        }
                        s.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }
                Strips[i] = null;
                LastSig[i] = null;
            }
            Spots.Clear();
        }

        private static GComponent EnsureStrip(int index, GComponent owner, UIBattleInfoPanel panel)
        {
            GComponent s = Strips[index];
            if (s != null && !s.isDisposed && s.parent == (GComponent)panel)
            {
                _stripHits++;
                s.visible = true;
                PositionStrip(s, owner, panel);   // 每 tick 跟随按钮位置
                return s;
            }
            // ---- 诊断(v46): 走到这里 = 需要重建 ----
            _stripRebuilds++;
            if (_stripRebuildLogs < 8 || _stripRebuilds % 40 == 0)
            {
                _stripRebuildLogs++;
                string why;
                if (s == null)
                {
                    why = "首次创建";
                }
                else if (s.isDisposed)
                {
                    why = "已被释放(isDisposed)";
                }
                else
                {
                    string pn = "<null>";
                    string pt = "?";
                    try
                    {
                        GObject par = s.parent;
                        if (par != null)
                        {
                            pn = par.name ?? "";
                            pt = par.GetType().Name;
                        }
                    }
                    catch (Exception)
                    {
                    }
                    why = "父容器不是 HUD 面板: 现 parent='" + pn + "'(" + pt + ")";
                }
                SdkLog.Info("WatchHand", "strip 重建 #" + _stripRebuilds + " idx=" + index
                    + " hits=" + _stripHits + " why=" + why);
            }
            s = new GComponent();
            s.touchable = false;
            s.SetSize(CardH * 3f, CardH + LabelH);
            // ★ 2026-10-27 v62: 改挂游戏自己的 HUD 面板(UIBattleInfoPanel), 与官方 HandViewerMod 一致。
            //   前两个挂载点都太"全局", 实测都会把界面带回默认状态(选中框消失 / 视角回自己):
            //     com_Player_N —— 破坏 Button 的子节点结构(3 -> 7), 游戏每次刷新按钮都要处理外来节点;
            //     GRoot        —— 改根容器的子节点与全局排序(sortingOrder=9000), 问题依旧。
            //   面板是普通 HUD 层, 官方就挂这里且不设 sortingOrder。
            if (panel != null)
            {
                ((GComponent)panel).AddChild(s);
            }
            else
            {
                GRoot.inst.AddChild(s);
            }
            Strips[index] = s;
            PositionStrip(s, owner, panel);
            return s;
        }

        /// <summary>
        /// 把 strip 摆到玩家按钮右侧。v62 起 strip 挂在 UIBattleInfoPanel 上,
        /// 所以要把按钮的全局坐标转成该面板的局部坐标(面板为 null 时退回 GRoot)。
        /// 每次 EnsureStrip 都会调用一次, 按钮位置变化时能跟上。
        /// </summary>
        private static void PositionStrip(GComponent s, GComponent owner, UIBattleInfoPanel panel)
        {
            try
            {
                GComponent baseC = panel != null ? (GComponent)panel : GRoot.inst;
                Vector2 gp = owner.LocalToGlobal(Vector2.zero);
                Vector2 lp = baseC.GlobalToLocal(gp);
                s.SetXY(lp.x + owner.width + 4f,
                        lp.y + (owner.height - (CardH + LabelH)) * 0.5f);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 诊断: 打印某玩家手牌的 CardId 列表, 并统计负数(= 服务器掩码, 客户端拿不到真卡)。
        /// 用来确认"队友的某些卡看不到"是服务器掩码, 还是本 mod 的问题。
        ///
        /// 2026-10-15: 去掉了原来 16 条的硬上限 —— 上限正好被打满, 导致队友后期
        /// (包括"新卡")的手牌完全没有记录, 无法回答"队友看到的牌是否完整"。
        /// 现在每次**手牌签名变化**都会打印一行(签名没变不打印), 所以量是可控的。
        /// </summary>
        private static void DumpHands(RoomPlayer rp, IReadOnlyList<HandCardData> cards)
        {
            _handDumps++;
            try
            {
                var sb = new System.Text.StringBuilder();
                int neg = 0;
                for (int k = 0; k < cards.Count; k++)
                {
                    int id = cards[k] == null ? 0 : cards[k].CardId;
                    if (k > 0)
                    {
                        sb.Append(',');
                    }
                    sb.Append(id);
                    if (id < 0)
                    {
                        neg++;
                    }
                }
                string who;
                try
                {
                    GameLogicManager gm = GameLogicManager.inst;
                    RoomLogic logic = gm == null ? null : gm.room;
                    RoomInfo room = logic == null ? null : logic.curRoomInfo;
                    long self = room == null ? -1L : room.GetSelfInfo().Id;
                    who = rp.Id == self ? "SELF" : "TEAM";
                }
                catch (Exception)
                {
                    who = "?";
                }
                SdkLog.Info("WatchHand", "hand #" + _handDumps + " " + who + " id=" + rp.Id
                    + " n=" + cards.Count + " neg=" + neg + " ids=[" + sb.ToString() + "]");
            }
            catch (Exception ex)
            {
                SdkLog.Warn("WatchHand", "DumpHands: " + ex.Message);
            }
        }

        private static string Signature(IReadOnlyList<HandCardData> cards)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < cards.Count; i++)
            {
                HandCardData c = cards[i];
                sb.Append(c == null ? -1 : c.CardId).Append(',');
            }
            return sb.ToString();
        }

        private static void RenderStrip(GComponent strip, RoomPlayer rp, IReadOnlyList<HandCardData> cards)
        {
            strip.RemoveChildren(0, -1, true);
            long playerId = rp.Id;
            float x = 0f;
            int drawn = 0;
            for (int i = 0; i < cards.Count; i++)
            {
                HandCardData data = cards[i];
                if (data == null || data.CardId <= 0)
                {
                    continue;
                }
                GComponent holder = BuildCard(playerId, data);
                if (holder == null)
                {
                    continue;
                }
                holder.x = x;
                strip.AddChild(holder);
                x += holder.width + Gap;
                drawn++;
            }
            strip.SetSize(x < 1f ? 1f : x, CardH + LabelH);
            strip.visible = drawn > 0;
        }

        private static GComponent BuildCard(long playerId, HandCardData data)
        {
            GComponent holder = new GComponent();
            try
            {
                var cfg = StaticConfigure.Card.InfoDict[data.CardId];
                if (cfg == null)
                {
                    return Fallback(holder, data);
                }
                var view = cfg.GetBattlePlayerCardView(playerId);
                // 2026-10-15 v19: 排查"新出的建筑卡(21026 起)在手牌条里不显示卡面"。
                // 两条兜底:
                //   (1) 拿不到 CardView 就自建一个(CardView 是普通类, 字段 public 可写)
                //   (2) 图名为空就按实测命名规则补 —— 卡图资源恒为 UT_HandCard_<卡牌ID>
                //       (安装目录 69 张卡图 + catalog 地址表两处独立验证过)
                if (view == null)
                {
                    view = new CardView();
                    view.CardId = data.CardId;
                    SdkLog.Warn("WatchHand", "cardView=null, 已自建: id=" + data.CardId);
                }
                if (string.IsNullOrEmpty(view.Key))
                {
                    view.Key = "UT_HandCard_" + data.CardId;
                    SdkLog.Warn("WatchHand", "view.Key 为空 -> 兜底 " + view.Key + " (id=" + data.CardId + ")");
                }
                UICom_Card card = UICom_Card.CreateInstance();
                holder.AddChild(card);
                // ★ 2026-10-26: 立刻关掉触摸。下面 "card 尺寸 <= 0" 分支和 catch 分支
                // 都会提前 return Fallback, 跳过原来放在 1814 的 card.touchable = false,
                // 会留下一个可触摸的 UICom_Card 留在 strip 里吞点击。
                card.touchable = false;

                // 卡名: 客户端 STRCard 表里存的是 CARD-0XX 占位符, 优先用内嵌的社区卡表
                string cardName = CardTable.Name(data.CardId);
                if (string.IsNullOrEmpty(cardName))
                {
                    cardName = UIHelper.GetLocal(cfg.NameID, (UIStringType)9);
                }
                if (!_fontLogged)
                {
                    _fontLogged = true;
                    int dictCount = -1;
                    int kmin = int.MaxValue;
                    int kmax = int.MinValue;
                    var sample = new System.Text.StringBuilder();
                    try
                    {
                        var d = StaticConfigure.STRCard.LocalDict;
                        dictCount = d.Count;
                        int shown = 0;
                        foreach (var kv in d)
                        {
                            int key = kv.Key;
                            if (key < kmin) { kmin = key; }
                            if (key > kmax) { kmax = key; }
                            if (shown < 6)
                            {
                                string v = null;
                                try { v = kv.Value == null ? "<null>" : kv.Value.GetLocal(); } catch (Exception) { v = "<err>"; }
                                sample.Append(key).Append('=').Append(v).Append("; ");
                                shown++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SdkLog.Warn("WatchHand", "dic probe: " + ex.Message);
                    }
                    SdkLog.Info("WatchHand", "name probe: cardId=" + data.CardId
                        + " cfg.Id=" + cfg.Id + " cfg.NameID=" + cfg.NameID
                        + " name='" + (cardName ?? "<null>") + "'"
                        + " STRCard.Count=" + dictCount
                        + " keyRange=[" + kmin + ".." + kmax + "]"
                        + " sample=" + sample.ToString());
                    // 卡面图片名 / 卡牌编号 / 备注 id 也可能藏着名字线索
                    try
                    {
                        SdkLog.Info("WatchHand", "name probe2: cfg.Image='" + (cfg.Image ?? "<null>")
                            + "' CardNumb=" + cfg.CardNumb + " CommentId=" + cfg.CommentId);
                    }
                    catch (Exception ex2)
                    {
                        SdkLog.Warn("WatchHand", "probe2: " + ex2.Message);
                    }
                    // GetLocal() 只挑一种语言, 直接读各语言字段看真名是否藏在别列
                    try
                    {
                        const int probeKey = 200010;
                        STRCardLocalConfigure lc;
                        if (StaticConfigure.STRCard.LocalDict.TryGetValue(probeKey, out lc) && lc != null)
                        {
                            SdkLog.Info("WatchHand", "local probe key=" + probeKey
                                + " cn='" + (lc.Simplified ?? "") + "'"
                                + " en='" + (lc.English ?? "") + "'"
                                + " jp='" + (lc.Japanese ?? "") + "'"
                                + " tw='" + (lc.Traditional ?? "") + "'"
                                + " kr='" + (lc.Korean ?? "") + "'");
                        }
                        else
                        {
                            SdkLog.Info("WatchHand", "local probe: key " + probeKey + " not in table");
                        }
                    }
                    catch (Exception ex5)
                    {
                        SdkLog.Warn("WatchHand", "local probe: " + ex5.Message);
                    }
                    // 列出 200000 以上的键(卡牌名很可能在这个区间, 键 = 卡牌ID*10)
                    try
                    {
                        var big = new System.Text.StringBuilder();
                        int shown2 = 0;
                        foreach (var kv in StaticConfigure.STRCard.LocalDict)
                        {
                            int key = kv.Key;
                            if (key < 200000 || shown2 >= 14)
                            {
                                continue;
                            }
                            string v2 = null;
                            try { v2 = kv.Value == null ? "<null>" : kv.Value.GetLocal(); } catch (Exception) { v2 = "<err>"; }
                            big.Append(key).Append('=').Append(v2).Append("; ");
                            shown2++;
                        }
                        SdkLog.Info("WatchHand", "name probe3 (key>=200000): " + big.ToString());
                    }
                    catch (Exception ex3)
                    {
                        SdkLog.Warn("WatchHand", "probe3: " + ex3.Message);
                    }
                    // 直接用 CardNumb 查一次(实测表键 = 卡牌ID*10)
                    try
                    {
                        string byNumb = UIHelper.GetLocal(cfg.CardNumb, (UIStringType)9);
                        SdkLog.Info("WatchHand", "name probe4: CardNumb=" + cfg.CardNumb
                            + " GetLocal(CardNumb)='" + (byNumb ?? "<null>") + "'");
                    }
                    catch (Exception ex4)
                    {
                        SdkLog.Warn("WatchHand", "probe4: " + ex4.Message);
                    }
                }
                if (string.IsNullOrEmpty(cardName))
                {
                    cardName = "#" + data.CardId;
                }
                // 描述: 同样优先用社区卡表(文本干净, 没有 {range=…} 之类模板残留), 否则回退游戏文本
                string desc = CardTable.Desc(data.CardId);
                if (string.IsNullOrEmpty(desc))
                {
                    desc = UIHelper.GetLocal(cfg.DescId, (UIStringType)9);
                    string dyn = DynamicDesc(playerId, data);
                    if (!string.IsNullOrEmpty(dyn))
                    {
                        desc = dyn;
                    }
                }
                // 游戏把 {range=…}/{damage=…} 这类模板留给 UI 层渲染时解析,
                // CardDescription 只处理了 {damage=}, 其余要自己去壳。
                string rawDesc = desc;
                desc = CleanTemplate(desc);
                if (_descDumps < 2)
                {
                    _descDumps++;
                    SdkLog.Info("WatchHand", "desc#" + _descDumps
                        + " raw=[" + rawDesc.Replace("\n", " ").Substring(0, Math.Min(90, rawDesc.Length))
                        + "] clean=[" + desc.Replace("\n", " ").Substring(0, Math.Min(90, desc.Length)) + "]");
                }
                CommonUIManager.RendererCard(card, view, cardName, desc, string.Empty, string.Empty,
                    data.BattleCost, cfg.CardType, cfg.CardTargetType, true, false);

                // 每张卡只打一次: 关键取值 + 渲染结果, 用来定位"卡面空白"
                // (普通卡与建筑卡各留一条, 直接对比就知道差在哪)
                if (RenderLogged.Add(data.CardId))
                {
                    try
                    {
                        string url;
                        try
                        {
                            url = card.loader_FrontCard == null
                                ? "<noloader>" : (card.loader_FrontCard.url ?? "<null>");
                        }
                        catch (Exception)
                        {
                            url = "<err>";
                        }
                        SdkLog.Info("WatchHand", "render id=" + data.CardId
                            + " key='" + (view.Key ?? "<null>") + "'"
                            + " loader='" + url + "'"
                            + " image='" + (cfg.Image ?? "<null>") + "'"
                            + " types=" + (int)cfg.CardType + "/" + (int)cfg.CardTargetType
                            + "/" + (int)cfg.EffectType
                            + " cost=" + data.BattleCost
                            + " video=" + view.IsVideo + " alt=" + view.IsAltArtCard
                            + " vtype=" + view.CardType
                            + " size=" + card.width + "x" + card.height);
                    }
                    catch (Exception exr)
                    {
                        SdkLog.Warn("WatchHand", "render log: " + exr.Message);
                    }
                }

                if (card.height <= 0f || card.width <= 0f)
                {
                    return Fallback(holder, data);
                }
                float k = CardH / card.height;
                card.SetScale(k, k);
                card.SetXY(0f, 0f);
                card.touchable = false;

                float cw = card.width * k;
                holder.SetSize(cw, CardH + LabelH);
                AddLabel(holder, cardName, cw);
                holder.touchable = false;

                string tip = cardName + "\n费用 " + data.BattleCost
                    + (data.PurifyNum > 0 ? " · 净化 " + data.PurifyNum : string.Empty)
                    + (data.IsTemp ? " · 临时牌" : string.Empty);
                if (!string.IsNullOrEmpty(desc))
                {
                    tip = tip + "\n" + desc;
                }
                holder.tooltips = tip;
                return holder;
            }
            catch (Exception ex)
            {
                LogOnce("card id=" + data.CardId + " " + ex.GetType().Name + ": " + ex.Message);
                return Fallback(holder, data);
            }
        }

        private static string DynamicDesc(long playerId, HandCardData data)
        {
            try
            {
                GameLogicManager gm = GameLogicManager.inst;
                CardLogic logic = gm == null ? null : gm.card;
                if (logic == null || logic.cardActions == null)
                {
                    return null;
                }
                if (!logic.cardActions.ContainsKey(data.CardId))
                {
                    return null;
                }
                var act = logic.cardActions[data.CardId];
                if (act == null)
                {
                    return null;
                }
                return act.CardDescription(playerId, data);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 去掉本地化模板外壳 {name=value} -> value, 并把 UBB 标记转成 HTML。
        /// FairyGUI 的 GRichTextField 走 HTML 解析(&lt;font color=…&gt;),
        /// UBB 形式([color=…])在本作里没被渲染, 所以要转一道。
        /// </summary>
        private static string CleanTemplate(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return s;
            }
            // 必须分两遍: 第一遍剥 {}, 第二遍转 UBB。
            // 一遍扫描时 {name=value} 里的内容是原样拷贝出去的,
            // 会绕过 UBB 转换 —— 这正是 [color=…] 漏网的原因。
            return UbbToHtml(StripBraces(s));
        }

        /// <summary>{name=value} -> value</summary>
        private static string StripBraces(string s)
        {
            if (s.IndexOf('{') < 0)
            {
                return s;
            }
            var sb = new System.Text.StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '{')
                {
                    int eq = s.IndexOf('=', i + 1);
                    int close = s.IndexOf('}', i + 1);
                    if (eq > i && close > eq)
                    {
                        sb.Append(s, eq + 1, close - eq - 1);
                        i = close + 1;
                        continue;
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>[color=#RRGGBB]…[/color] -> &lt;font color="#RRGGBB"&gt;…&lt;/font&gt;</summary>
        private static string UbbToHtml(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('[') < 0)
            {
                return s;
            }
            var sb = new System.Text.StringBuilder(s.Length + 16);
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '[' && StartsAt(s, i, "[color="))
                {
                    int end = s.IndexOf(']', i + 7);
                    if (end > i + 7)
                    {
                        sb.Append("<font color=\"");
                        sb.Append(s, i + 7, end - i - 7);
                        sb.Append("\">");
                        i = end + 1;
                        continue;
                    }
                }
                if (c == '[' && StartsAt(s, i, "[/color]"))
                {
                    sb.Append("</font>");
                    i += 8;
                    continue;
                }
                if (c == '[' && StartsAt(s, i, "[b]"))
                {
                    sb.Append("<b>");
                    i += 3;
                    continue;
                }
                if (c == '[' && StartsAt(s, i, "[/b]"))
                {
                    sb.Append("</b>");
                    i += 4;
                    continue;
                }
                if (c == '[' && StartsAt(s, i, "[i]"))
                {
                    sb.Append("<i>");
                    i += 3;
                    continue;
                }
                if (c == '[' && StartsAt(s, i, "[/i]"))
                {
                    sb.Append("</i>");
                    i += 4;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static bool StartsAt(string s, int index, string pattern)
        {
            return index + pattern.Length <= s.Length
                && string.CompareOrdinal(s, index, pattern, 0, pattern.Length) == 0;
        }

        /// <summary>
        /// 卡图下方的卡名。
        /// 注意: 不能 new TextFormat() —— 那会把 font 清空, 文字就渲染不出来。
        /// 必须取现有 textFormat 改 size/color 再赋回(结构体)。
        /// </summary>
        private static void AddLabel(GComponent holder, string text, float width)
        {
            // 先诊断: 卡名取不到时也要留下证据(上一版因为提前 return 什么都没打印)
            if (!_fontLogged)
            {
                _fontLogged = true;
                try
                {
                    SdkLog.Info("WatchHand", "label probe: text='" + (text ?? "<null>")
                        + "' len=" + (text == null ? -1 : text.Length)
                        + " width=" + width
                        + " defFont='" + (UIConfig.defaultFont ?? "<null>") + "'");
                }
                catch (Exception ex)
                {
                    SdkLog.Warn("WatchHand", "label probe threw: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            GTextField label = new GTextField();
            label.SetSize(width < 1f ? 1f : width, LabelH);
            label.text = text;
            label.SetXY(0f, CardH + 1f);
            label.touchable = false;
            // 字体诊断: 放在 try 外, 保证一定能打出来
            if (!_fontLogged)
            {
                _fontLogged = true;
                try
                {
                    SdkLog.Info("WatchHand", "font probe: textFormat.font='"
                        + (label.textFormat.font ?? "<null>")
                        + "' size=" + label.textFormat.size
                        + " UIConfig.defaultFont='" + (UIConfig.defaultFont ?? "<null>") + "'");
                }
                catch (Exception ex)
                {
                    SdkLog.Warn("WatchHand", "font probe threw: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            try
            {
                TextFormat fmt = label.textFormat;
                fmt.size = 12;
                fmt.color = Color.white;
                label.textFormat = fmt;
            }
            catch (Exception ex)
            {
                LogOnce("label format: " + ex.GetType().Name + ": " + ex.Message);
            }
            holder.AddChild(label);
        }

        private static GComponent Fallback(GComponent holder, HandCardData data)
        {
            float w = CardH * 0.62f;
            GGraph g = new GGraph();
            g.SetSize(w, CardH);
            g.DrawRect(w, CardH, 1, Color.white, new Color(0.12f, 0.14f, 0.2f, 0.92f));
            g.touchable = false;
            holder.AddChild(g);
            GTextField t = new GTextField();
            t.SetSize(w, CardH);
            t.text = "#" + data.CardId;
            t.SetXY(2f, CardH * 0.5f - 8f);
            t.touchable = false;
            holder.AddChild(t);
            holder.SetSize(w, CardH + LabelH);
            AddLabel(holder, "#" + data.CardId, w);
            holder.touchable = false;
            holder.tooltips = "卡牌 " + data.CardId + " (原始卡面不可用)";
            return holder;
        }

        // =====================================================================
        // 自绘悬停提示
        // =====================================================================

        /// <summary>把当前所有卡图的屏幕矩形收集起来(每 tick 重建, 简单可靠)。</summary>
        /// <summary>
        /// v34: HUD 面板缓存版查找 —— FindPanel 会递归遍历整棵 UI 树, 每帧调用会把帧率压到 ~2 FPS。
        /// 面板在整局里是同一个对象, 所以只在"缓存失效"时重查。
        /// </summary>
        private static UIBattleInfoPanel PanelCached()
        {
            if (_panelCache != null && !_panelCache.isDisposed && _panelCache.parent != null)
            {
                return _panelCache;
            }
            _panelCache = FindPanel(GRoot.inst, 0);
            return _panelCache;
        }

        /// <summary>v34: 热点区域(鼠标提示用)每 12 帧(约 0.2 秒)重建一次, 不必每帧做矩阵变换。</summary>
        private static void RebuildSpotsIfDue()
        {
            if (_ticks % 12 != 0)
            {
                return;
            }
            RebuildSpots();
        }

        private static void RebuildSpots()
        {
            Spots.Clear();
            for (int i = 0; i < Strips.Length; i++)
            {
                GComponent strip = Strips[i];
                if (strip == null || strip.isDisposed || !strip.visible)
                {
                    continue;
                }
                int n = strip.numChildren;
                for (int k = 0; k < n; k++)
                {
                    GObject child = strip.GetChildAt(k);
                    if (child == null || child.isDisposed || string.IsNullOrEmpty(child.tooltips))
                    {
                        continue;
                    }
                    Vector2 gp = child.LocalToGlobal(Vector2.zero);
                    Spots.Add(new Hotspot
                    {
                        X = gp.x,
                        Y = gp.y,
                        W = child.width,
                        H = child.height,
                        Text = child.tooltips
                    });
                }
            }
        }

        private static void UpdateTooltip()
        {
            Vector2 mouse;
            if (!InputService.TryGetMousePosition(out mouse))
            {
                HideTip();
                return;
            }
            // Unity 屏幕坐标原点在左下, FairyGUI 在左上
            float mx = mouse.x;
            float my = Screen.height - mouse.y;

            string hit = null;
            for (int i = 0; i < Spots.Count; i++)
            {
                Hotspot s = Spots[i];
                if (mx >= s.X && mx <= s.X + s.W && my >= s.Y && my <= s.Y + s.H)
                {
                    hit = s.Text;
                    break;
                }
            }
            if (string.IsNullOrEmpty(hit))
            {
                HideTip();
                return;
            }
            ShowTip(hit, mx, my);
        }

        private static void ShowTip(string text, float mx, float my)
        {
            // 改用游戏自带的 tooltip 机制。
            // 自绘框试过挂 HUD 根 / 卡图容器 / GRoot 三种位置, 始终被 HUD 压住 ——
            // 游戏那条链上每层都有自己的排序(com_BattlePlayer idx=27/32、UIBattleInfoPanel idx=0/2),
            // 外来的 GObject 抢不过它。而游戏自己的 tooltip 由它插到托管的最高层, 层级必然正确。
            // ShowTooltips 调用不抛异常但什么都不显示(实测: 游戏没配 UIConfig.tooltipWin), 所以还是自绘。
            // 关键修正: 光靠 childIndex 抢不过游戏的 HUD —— 那些容器有自己的排序(很可能用了 sortingOrder),
            // 所以必须给提示框一个极大的 sortingOrder。
            if (_tip == null || _tip.isDisposed)
            {
                _tip = new GComponent();
                _tipBg = new GGraph();
                _tipText = new GRichTextField();
                _tip.AddChild(_tipBg);
                _tip.AddChild(_tipText);
                _tip.touchable = false;
                _tipText.touchable = false;
                // ★ 2026-10-26 修复"点 A 落到 B": GGraph 的 touchable 默认是 true,
                // _tip.touchable=false 不会传递给子元素, 于是这块 340×h 的背景
                // (sortingOrder=10000, 压在全部 HUD 之上) 一直吞掉它范围内的点击。
                _tipBg.touchable = false;
                // ★ 关键修正: 给提示框一个极高的 sortingOrder, 压过游戏所有 HUD 容器。
                // 只靠 childIndex 是不够的 —— 游戏容器自己的排序会赢(实测三种挂载位置全被压住)。
                try
                {
                    _tip.sortingOrder = 10000;
                }
                catch (Exception)
                {
                }
                if (_hud != null && !_hud.isDisposed)
                {
                    _hud.AddChild(_tip);
                }
                else
                {
                    GRoot.inst.AddChild(_tip);
                }
                _tipShown = null;
            }

            if (_tipShown != text)
            {
                _tipShown = text;

                try
                {
                    TextFormat f = _tipText.textFormat;
                    f.size = 14;
                    f.color = Color.white;
                    _tipText.textFormat = f;
                }
                catch (Exception)
                {
                }
                // 关键: 让文字控件"宽度固定、高度自适应", 长描述才会自动换行。
                // GTextField 默认 autoSize=Both, 宽度会跟着内容一起涨, 文字就横着冲出提示框。
                try
                {
                    _tipText.autoSize = AutoSizeType.Height;
                }
                catch (Exception)
                {
                }
                _tipText.SetSize(TipW - 12f, 0f);
                _tipText.SetXY(6f, 4f);
                _tipText.text = text;

                // 用文字控件换行后的实测高度来定框高
                float h = 0f;
                try
                {
                    h = _tipText.height + 10f;
                }
                catch (Exception)
                {
                }
                if (h < TipLineH + 10f)
                {
                    h = TipLineH + 10f;
                }

                _tipBg.SetSize(TipW, h);
                _tipBg.DrawRect(TipW, h, 1, new Color(0.65f, 0.65f, 0.7f, 1f),
                    new Color(0.05f, 0.06f, 0.1f, 0.96f));
                _tip.SetSize(TipW, h);
                if (_tipDumps < 2)
                {
                    _tipDumps++;
                    string pname = "?";
                    int idx = -1;
                    int cnt = -1;
                    try
                    {
                        GObject par = _tip.parent;
                        pname = par == null ? "<null>" : par.name;
                        GComponent pc = par as GComponent;
                        if (pc != null)
                        {
                            idx = pc.GetChildIndex(_tip);
                            cnt = pc.numChildren;
                        }
                    }
                    catch (Exception)
                    {
                    }
                    string flat = text.Replace("\n", " | ");
                    SdkLog.Info("WatchHand", "tip#" + _tipDumps
                        + " parent=" + pname + " index=" + idx + "/" + cnt
                        + " hud=" + (_hud == null ? "<null>" : _hud.name)
                        + " text=" + flat.Substring(0, Math.Min(120, flat.Length)));
                }
            }

            _tip.visible = true;
            // 每 tick 把它挪到根容器的最后一个子级(= 最上层), 避免被游戏 HUD 盖住
            try
            {
                // ★ 2026-10-26: 原来只要 _tip.parent 不是 GRoot.inst 就重挂一遍,
                // 每 tick 都可能对全局根容器做 AddChild, 会触发显示列表重建。
                // 只在确实没有父节点时才挂一次。
                if (_tip.parent == null)
                {
                    GRoot.inst.AddChild(_tip);
                }
                int last = GRoot.inst.numChildren - 1;
                if (last >= 0 && GRoot.inst.GetChildIndex(_tip) != last)
                {
                    GRoot.inst.SetChildIndex(_tip, last);
                }
            }
            catch (Exception)
            {
            }
            float px = mx + 18f;
            float py = my + 18f;
            if (px + TipW > GRoot.inst.width)
            {
                px = mx - TipW - 8f;
            }
            if (py + _tip.height > GRoot.inst.height)
            {
                py = my - _tip.height - 8f;
            }
            if (px < 0f)
            {
                px = 0f;
            }
            if (py < 0f)
            {
                py = 0f;
            }
            // 目标是全局坐标; 提示框可能挂在 HUD 根上, 转成其父容器的局部坐标
            Vector2 pos = new Vector2(px, py);
            try
            {
                if (_tip.parent != null && _tip.parent != GRoot.inst)
                {
                    pos = _tip.parent.GlobalToLocal(pos);
                }
            }
            catch (Exception)
            {
            }
            _tip.SetXY(pos.x, pos.y);
        }

        private static void HideTip()
        {
            _tipShown = null;
            if (_tip == null || _tip.isDisposed)
            {
                return;
            }
            _tip.visible = false;
            // v45: 这里原本还调了 GRoot.inst.HidePopup(_tip) —— 用错了 API。
            // _tip 是用 AddChild 加到 GRoot 的普通子节点, 从没进过 FairyGUI 的 popup 栈;
            // HidePopup 会去动 GRoot 的弹窗栈 / modal 状态, 对不在栈里的对象调用结果不确定,
            // 可能扰动全局点击判定(症状: 点 A 落到 B)。上一行 visible=false 已足够。
        }

        // =====================================================================

        private static UIBattleInfoPanel FindPanel(GObject node, int depth)
        {
            if (node == null || depth > 12)
            {
                return null;
            }
            UIBattleInfoPanel hit = node as UIBattleInfoPanel;
            if (hit != null)
            {
                return hit;
            }
            GComponent comp = node as GComponent;
            if (comp == null)
            {
                return null;
            }
            int count = comp.numChildren;
            for (int i = 0; i < count; i++)
            {
                UIBattleInfoPanel found = FindPanel(comp.GetChildAt(i), depth + 1);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }
    }
}