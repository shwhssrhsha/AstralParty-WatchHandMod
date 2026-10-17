using System;
using System.Collections.Generic;
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
        /// <summary>v37: OnFrame frame counter (throttles the tooltip raise-to-top).</summary>
        private static int _frameTicks;
        /// <summary>v39: Q 键开关 —— 默认关(零渲染开销), 开启后才画队友手牌。</summary>
        private static bool _handsVisible;
        private static bool _keyQHeld;
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
        /// v1.0: 取某玩家要显示的手牌 —— **观战服务优先**(它才是实时源)。
        ///
        /// 为什么不能用本地数据: rp.serverPlayer.Hero.Cards 与 rp.cardContainer._HandCards
        /// 对四人都有数据、也无掩码, 但**是快照、不随出牌更新**(实测相隔 32 秒完全一致)。
        /// 所以本地数据只在服务拿不到时兜底都不合适 —— 会显示"看起来像真的但是旧的"手牌。
        /// 顺序: SvcHands(需 5 秒内更新过) → LiveHands → 失败(界面显示"手牌加载失败")。
        /// </summary>
        private static IReadOnlyList<HandCardData> CardsOf(RoomPlayer rp, CardContainer cc)
        {
            if (rp == null)
            {
                return null;
            }
            // ① 观战服务(真·实时) —— 必须检查时效, 服务停了就不能继续摆旧数据
            // v1.0: 时效用**帧计数**判断, 不用 Environment.TickCount ——
            // 实测 TickCount 在本机 IL2CPP 下不增长(诊断里 age 永远同一个数),
            // 导致"永远判定过期"; 而 _ticks 稳定增长(约 1.9 次/秒)。
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

        /// <summary>v45: 把 rp.serverPlayer.Hero.Cards 转成 HandCardData 列表(拿不到返回 null)。</summary>
        private static List<HandCardData> LocalHeroCards(RoomPlayer rp)
        {
            try
            {
                Player sp = rp.serverPlayer;
                Hero hero = sp == null ? null : sp.Hero;
                if (hero == null)
                {
                    return null;
                }
                var hc = hero.Cards;
                if (hc == null || hc.Count == 0)
                {
                    return null;
                }
                var outList = new List<HandCardData>(hc.Count);
                for (int k = 0; k < hc.Count; k++)
                {
                    CardInfo ci = hc[k];
                    int cid = ci == null ? 0 : ci.CardId;
                    if (cid <= 0)
                    {
                        continue;
                    }
                    // HandCardData(Id, Guid, PurifyNum, IsTemp, battleCost=-1) → 费用自动取 cfg.Cost
                    outList.Add(new HandCardData(cid, 0, 0, false, -1));
                }
                return outList.Count > 0 ? outList : null;
            }
            catch (Exception)
            {
                return null;
            }
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
        ///   { "url": "http://&lt;你自己的服务器&gt;:8765", "token": "&lt;你自己设的 token&gt;" }
        ///
        /// 文件不存在 = 不启用(界面显示"手牌加载失败: 服务未配置"), 不影响游戏本身。
        /// 这样分发的 dll 里不含任何服务端地址/令牌。
        /// </summary>
        private static string _svcUrl = "";
        private static string _svcToken = "";
        private static string _svcStatus = "未配置";
        private static volatile string _svcLastError = "";   // v43: 最近一次服务侧失败原因(界面显示用)
        /// <summary>v1.0: 上次成功拿到服务数据时的**帧计数**(_ticks)。见 SvcStaleTicks 的说明。</summary>
        private static long _svcHandsAtTicks;
        /// <summary>
        /// v1.0: 服务数据超过这么多**帧**(约 1.9 次/秒 → 10 帧≈5 秒)没更新就算失效。
        /// 不用秒数: Environment.TickCount 在本机 IL2CPP 下不增长, 按它算会永远判定过期。
        /// </summary>
        private const int SvcStaleTicks = 10;
        /// <summary>v1.0: 配置检查节流(帧, ≈10 帧/5 秒)。</summary>
        private static long _svcCfgCheckedTick;
        /// <summary>v44: 服务数据超过这个秒数没更新就算失效 —— 否则会一直显示"最后一次成功的旧手牌"。</summary>
        private const int SvcStaleSec = 5;
        /// <summary>v1.0: 单次请求硬超时(毫秒)。超过就强制认为失败, 避免 _svcBusy 永久卡死。</summary>
        private const int PollHardTimeoutTicks = 19;   // ≈10 秒(帧≈1.9/秒)
        private static long _svcPollStarted;
        /// <summary>v1.0: 配置文件检查节流(秒)。</summary>
        private static long _svcLastPoll;
        private static volatile string _svcJsonResult;
        private static volatile bool _svcBusy;
        private static readonly Dictionary<long, List<HandCardData>> SvcHands =
            new Dictionary<long, List<HandCardData>>();
        private static readonly object SvcHandsLock = new object();

        /// <summary>
        /// v1.0(发布版): 从用户自己的 watch_service.json 读服务地址与令牌。
        /// 每 5 秒看一次(支持运行中改配置)。文件不存在 = 不启用。
        /// </summary>
        private static void RefreshServiceConfig()
        {
            // v1.0: 节流用帧计数(≈10 帧/5 秒), 不用 TickCount
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
        /// v1.0: 找 watch_service.json —— 优先 mod 自己的文件夹, 其次加载器根目录。
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
        /// 发一次查询 —— 走后台线程 + WebClient（同步下载在子线程里做, 不阻塞游戏主线程）。
        /// 不用 UnityWebRequest: 它在 netstandard2.0 下强制依赖框架版 System.Uri, 编译不过。
        /// </summary>
        /// <summary>
        /// v1.0: 到期无条件中断请求 —— HttpWebRequest.Timeout 在 IL2CPP 下实测不生效,
        /// 只能靠 Abort() 强制打断阻塞中的调用。看门狗线程设为后台线程, 不阻止进程退出。
        /// </summary>
        private static void StartAbortWatchdog(System.Net.HttpWebRequest req, int ms)
        {
            try
            {
                var t = new System.Threading.Thread(delegate()
                {
                    try
                    {
                        System.Threading.Thread.Sleep(ms);
                        req.Abort();
                    }
                    catch (Exception)
                    {
                    }
                });
                t.IsBackground = true;
                t.Start();
            }
            catch (Exception)
            {
            }
        }

        private static void PollService(string code)
        {
            // v1.0: 全部用帧计数当"时钟"(TickCount 在本机不增长)。帧≈1.9/秒。
            long now = _ticks;
            // v1.0: 请求线程卡死的兜底 —— HttpWebRequest 的 Timeout 在 IL2CPP 下实测可能不生效,
            // 线程挂死在读响应里, _svcBusy 永远为 true, 之后再也不发新请求
            // (症状: status 长期停在"请求中...", 而 SvcHands 里还是旧数据, 超过 5 秒后
            //  界面全变成"手牌加载失败: 服务已停止更新(Ns)")。
            // 这里: 超过 PollHardTimeoutMs 还没回来就强制放行, 允许下一次请求。
            if (_svcBusy)
            {
                if (_svcPollStarted > 0 && (now - _svcPollStarted) > PollHardTimeoutTicks)
                {
                    _svcBusy = false;
                    _svcStatus = "上次请求超时已重置";
                }
                else
                {
                    return;
                }
            }
            if (string.IsNullOrEmpty(_svcUrl) || string.IsNullOrEmpty(code))
            {
                return;
            }
            if (now - _svcLastPoll < 4)
            {
                return;
            }
            _svcLastPoll = now;
            _svcBusy = true;
            _svcPollStarted = now;
            // 观战码/token 都是字母数字, 直接拼
            string url = _svcUrl.TrimEnd('/') + "/hands?code=" + code
                + (string.IsNullOrEmpty(_svcToken) ? "" : "&token=" + _svcToken);
            _svcStatus = "请求中...";
            try
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate(object _)
                {
                    System.Net.HttpWebRequest req = null;
                    try
                    {
                        // v33: 改用 HttpWebRequest —— WebClient 没有超时, 卡住就永久卡住
                        // (实测: status 长期停在"配置已加载", 说明线程进去后再没出来)
                        req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                        try { req.Proxy = null; } catch (Exception) { }   // 绕过 clash 代理
                        req.Timeout = 6000;
                        req.ReadWriteTimeout = 6000;
                        req.UserAgent = "WatchHandMod";
                        // v1.0: 关键修复 —— 实测 req.Timeout 在 IL2CPP 下**不生效**
                        // (日志证据: age=6s busy=1 err=- 一直不恢复, 6 秒超时从未触发)。
                        // 所以另起一个"看门狗"线程, 到点无条件 Abort(), 它会强制中断阻塞中的
                        // GetResponse()/ReadToEnd() 并抛异常, 保证 finally 一定被走到、_svcBusy 复位。
                        StartAbortWatchdog(req, 8000);
                        using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                        using (var sr = new System.IO.StreamReader(resp.GetResponseStream(),
                                                                   System.Text.Encoding.UTF8))
                        {
                            _svcJsonResult = sr.ReadToEnd();
                        }
                    }
                    catch (Exception ex)
                    {
                        _svcJsonResult = "!ERR!" + ex.GetType().Name + ": " + ex.Message;
                    }
                    finally
                    {
                        if (req != null)
                        {
                            try { req.Abort(); } catch (Exception) { }
                        }
                        _svcBusy = false;
                    }
                });
            }
            catch (Exception ex)
            {
                _svcBusy = false;
                _svcStatus = "线程异常 " + ex.GetType().Name;
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
            _svcHandsAtTicks = _ticks;   // v1.0: 记录时效起点(帧计数)
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
                    if (room.info != null)
                    {
                        code = room.info.WatchCode ?? "";
                        mapType = room.MapType;
                        roomId = room.Id;
                    }
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
                    // v1.0 诊断: status 每 2 秒会被 PollService 重置成"请求中...", 不能反映真实结果。
                    // 这两个才是判据: 数据多久没更新过 / 最近一次失败原因。
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

        public static void Main()
        {
            ModBase.Run(OnInit, OnTick, tag: "WatchHand");
        }

        private static void OnInit()
        {
            SdkLog.Info("WatchHand", "=== WatchHand v1.0 (release, user config) ===");
            SdkLog.Info("WatchHand", "探针说明: F9=用当前观战码请求整房快照(含四人手牌); F10=直接发刷新请求");
            // v20: 订阅 SDK 的手牌变化事件(包装 RoomHeroCardChangeS2C), 用于刷新队友手牌。
            try
            {
                GameEvents.HandChanged += OnHandChanged;
                GameEvents.HeroAttrUpdated += OnHeroAttrUpdated;   // v24: 掩码定案用
                _hookedEvents = true;
                SdkLog.Info("WatchHand", "已订阅 GameEvents.HandChanged + HeroAttrUpdated");
            }
            catch (Exception exh)
            {
                SdkLog.Warn("WatchHand", "订阅 HandChanged 失败: " + exh.GetType().Name + ": " + exh.Message);
            }
            // 每帧把提示框顶到最上层。只在 mod tick(=2.7 秒级) 里置顶是不够的:
            // 游戏自己也在调整 UI 层级, 中间那几十帧会把提示框挤下去。
            try
            {
                Timers.inst.Add(0f, 0, OnFrame);
            }
            catch (Exception ex)
            {
                SdkLog.Warn("WatchHand", "Timers.Add failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void OnFrame(object param)
        {
            try
            {
                // v25: F9/F10 观战探针已废弃 —— 它会向服务器发观战请求, 实测会让游戏客户端
                // 走进异常分支(用户报告"卡住/别的错误")。观战改由外部服务用第二个账号做,
                // mod 只负责把观战码写出来(见 WriteWatchTarget)。
                // v26: 收观战服务的 HTTP 响应(必须在下面提前 return 之前)
                PumpService();
                // v39: Q 键切换"显示队友手牌" —— 默认关闭(零渲染开销), 需要时按一下看。
                // 只切换本地显示开关, **不发任何网络请求**(与已废弃的 F9 完全不同)。
                // 本机 Win32 输入后端不可用(走 Unity 反射, 按住期间持续为 true), 必须自己做边沿检测。
                bool keyQ = false;
                try
                {
                    keyQ = InputService.IsKeyDown(KeyCode.Q);
                }
                catch (Exception)
                {
                }
                if (keyQ && !_keyQHeld)
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
                    SdkLog.Info("WatchHand", "KeyQ: 队友手牌显示 = " + _handsVisible);
                }
                _keyQHeld = keyQ;
                if (_tip == null || _tip.isDisposed || !_tip.visible)
                {
                    return;
                }
                // 只在"当前父容器内"提到最后。
                // 不能在这里改父容器 —— 否则会和 Update() 里"挂到 HUD 根"的逻辑互相打架
                // (每帧被搬回 GRoot, 于是永远被 HUD 盖住)。
                // v37: 关键修复 —— 原来**每帧**在 GRoot(根容器)上 SetChildIndex 重排序子节点,
                // 实测持续掉帧。降低到每 30 帧(约 30 秒? 不, OnFrame 是每帧)…… 这里用帧计数节流:
                // 提示框只需要"大致在最上层", 每 60 帧(约 1 秒)置顶一次完全够用。
                _frameTicks++;
                if (_frameTicks % 60 != 0)
                {
                    return;
                }
                GComponent parent = _tip.parent as GComponent;
                if (parent == null)
                {
                    return;
                }
                int last = parent.numChildren - 1;
                if (last > 0 && parent.GetChildIndex(_tip) != last)
                {
                    parent.SetChildIndex(_tip, last);
                }
            }
            catch (Exception)
            {
            }
        }

        private static void OnTick()
        {
            _ticks++;
            if (_ticks < 3)
            {
                return;
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
            if (_ticks % 30 == 0)
            {
                try
                {
                    WriteWatchTarget();
                }
                catch (Exception ex)
                {
                    SdkLog.Warn("WatchHand", "tick30 WriteWatchTarget 抛出: " + ex.GetType().Name
                        + ": " + ex.Message);
                }
            }
            // v26: 观战服务轮询(只有写了 watch_service.json 才启用; 没配置时零开销)
            // v35: 每 6 帧(约 0.1 秒)检查一次, 内部仍有 2 秒节流
            if (_ticks % 6 == 0)
            {
                try
                {
                    RefreshServiceConfig();
                    if (!string.IsNullOrEmpty(_svcUrl) && !string.IsNullOrEmpty(_lastWatchCode))
                    {
                        PollService(_lastWatchCode);
                    }
                }
                catch (Exception ex)
                {
                    SdkLog.Warn("WatchHand", "tick6 RefreshServiceConfig 抛出: " + ex.GetType().Name
                        + ": " + ex.Message);
                }
            }
            // v21: 把三个手牌数据源打出来对比(只读诊断) —— v35: 每 60 帧(约 1 秒)一次
            if (_ticks % 60 == 0)
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
            if (_ticks % 6 == 0)
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
            if (_ticks % 5 == 0)
            {
                SdkLog.Info("WatchHand", "prof tick=" + _ticks + " hooked=" + (p1 - pStart) + "ms attrLog=" + (p2 - p1) + "ms rest=" + (t0 - p2) + "ms update=" + (tA - t0) + "ms tooltip=" + (tB - tA) + "ms");
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

        private static void Update()
        {
            // v39: 默认不画队友手牌(渲染开销为 0); 按 Q 打开后才走后面的绘制。
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
                GComponent strip = EnsureStrip(i, owner);
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
                        // v1.0: 没有明确错误, 但有"过期"的时间戳 -> 说明服务停止更新了
                        // (帧计数判定: age>10 帧 ≈ 5 秒)
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
                }
                LastSig[i] = null;
            }
            Spots.Clear();
        }

        private static GComponent EnsureStrip(int index, GComponent owner)
        {
            GComponent s = Strips[index];
            if (s != null && !s.isDisposed && s.parent == owner)
            {
                s.visible = true;
                return s;
            }
            s = new GComponent();
            s.touchable = false;
            s.SetSize(CardH * 3f, CardH + LabelH);
            // 贴在"整个玩家条目"右侧之外, 不遮挡原有的名字/星币/血量
            s.x = owner.width + 4f;
            s.y = (owner.height - (CardH + LabelH)) * 0.5f;
            owner.AddChild(s);
            Strips[index] = s;
            return s;
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
                if (_tip.parent != GRoot.inst)
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
            try
            {
                GRoot.inst.HidePopup(_tip);
            }
            catch (Exception)
            {
            }
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