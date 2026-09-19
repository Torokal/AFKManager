using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using UnityEngine;

namespace AFKManager
{
    /// <summary>
    /// AFK core. Polls the connected peers and compares a few fields of each player's own ZDO — data that vanilla clients
    /// already send to a dedicated server. No Harmony patches, no custom RPCs, no world/scene scans: O(connected players).
    /// </summary>
    internal sealed class AfkService
    {
        // ZSyncAnimation stores animator parameters in the ZDO under (salt + Animator.StringToHash(name)).
        const int AnimatorZdoSalt = 438569;

        readonly AFKConfig _config;
        readonly ManualLogSource _log;
        readonly AnnouncementService _announcer;
        readonly Dictionary<long, PlayerAfkState> _states = new Dictionary<long, PlayerAfkState>();
        readonly List<long> _stale = new List<long>();
        readonly int _craftingKey;
        int[] _equipmentKeys;
        int _generation;

        internal AfkService(AFKConfig config, ManualLogSource log, AnnouncementService announcer)
        {
            _config = config; _log = log; _announcer = announcer;
            _craftingKey = AnimatorZdoSalt + Animator.StringToHash("crafting");
        }

        /// <summary>True when the connected session is currently flagged AFK. Unknown / not yet initialized sessions are never AFK.</summary>
        internal bool IsAfk(long peerUid)
        {
            PlayerAfkState s;
            return _states.TryGetValue(peerUid, out s) && s.Initialized && s.IsAfk;
        }

        internal void Clear() { _states.Clear(); }

        /// <summary>One detection pass. Must run on the main thread (ZDO / ZNet APIs are not thread-safe).</summary>
        internal void Check(float now)
        {
            ZNet znet = ZNet.instance;
            ZDOMan zdoMan = ZDOMan.instance;
            if (znet == null || zdoMan == null) return;

            bool debug = _config.DebugLogging;
            long startTicks = debug ? Stopwatch.GetTimestamp() : 0L;
            if (_equipmentKeys == null) _equipmentKeys = new[] {
                ZDOVars.s_rightItem, ZDOVars.s_leftItem, ZDOVars.s_rightBackItem, ZDOVars.s_leftBackItem, ZDOVars.s_chestItem,
                ZDOVars.s_legItem, ZDOVars.s_helmetItem, ZDOVars.s_shoulderItem, ZDOVars.s_utilityItem };

            float afkAfter = _config.AfkAfterSeconds;
            float moveThreshold = _config.MovementThresholdMeters;
            float moveThresholdSqr = moveThreshold * moveThreshold;
            float lookThreshold = _config.LookThresholdDegrees;

            _generation++;
            int players = 0, afk = 0;
            List<ZNetPeer> peers = znet.GetPeers();          // live list, no allocation
            for (int i = 0; i < peers.Count; i++)
            {
                ZNetPeer peer = peers[i];
                if (peer == null || !peer.IsReady()) continue;

                PlayerAfkState state;
                if (!_states.TryGetValue(peer.m_uid, out state))
                {
                    state = new PlayerAfkState();
                    state.PeerUid = peer.m_uid;
                    _states[peer.m_uid] = state;
                }
                state.SeenGeneration = _generation;
                state.PlayerName = peer.m_playerName;

                UpdatePlayer(state, peer, zdoMan, now, moveThresholdSqr, lookThreshold);

                // Timeout: only while the character is observable; paused through dead / loading / missing windows (neutral).
                if (state.Initialized && !state.NeedsBaseline && !state.IsAfk && now - state.LastActivityTime >= afkAfter)
                {
                    state.IsAfk = true;
                    if (debug) _log.LogInfo(state.PlayerName + " ACTIVE -> AFK");
                    _announcer.AnnounceAfk(state.PlayerName, state.LastWorldPosition);
                }
                if (state.Initialized) { players++; if (state.IsAfk) afk++; }
            }

            RemoveDisconnected();

            if (debug)
            {
                double ms = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
                _log.LogInfo("check: players=" + players + " active=" + (players - afk) + " afk=" + afk + " elapsed=" + ms.ToString("F3") + "ms");
            }
        }

        void UpdatePlayer(PlayerAfkState state, ZNetPeer peer, ZDOMan zdoMan, float now, float moveThresholdSqr, float lookThreshold)
        {
            ZDOID characterId = peer.m_characterID;
            ZDO zdo = characterId.IsNone() ? null : zdoMan.GetZDO(characterId);
            if (zdo == null || zdo.GetBool(ZDOVars.s_dead, false))
            {
                state.NeedsBaseline = true;               // loading, dead or between characters: neutral
                return;
            }

            // --- read the server-visible values once ---
            ZDOID parent = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.SyncTransform);
            Vector3 worldPos = zdo.GetPosition();
            Vector3 position = parent.IsNone() ? worldPos : zdo.GetVec3(ZDOVars.s_relPosHash, Vector3.zero);
            Vector3 lookTarget = zdo.GetVec3(ZDOVars.s_lookTarget, Vector3.zero);
            bool hasLook = lookTarget != Vector3.zero;
            Vector3 lookDir = hasLook ? (lookTarget - worldPos).normalized : Vector3.zero;
            int emoteId = zdo.GetInt(ZDOVars.s_emoteID, 0);
            int crafting = zdo.GetInt(_craftingKey, 0);
            bool inBed = zdo.GetBool(ZDOVars.s_inBed, false);
            int equipment = 17;
            for (int k = 0; k < _equipmentKeys.Length; k++) equipment = unchecked(equipment * 31 + zdo.GetInt(_equipmentKeys[k], 0));

            ActivityReason reasons = ActivityReason.None;
            bool sameCharacter = state.Initialized && !state.NeedsBaseline && characterId == state.CharacterId;

            if (!state.Initialized)
            {
                // Session start: the character just became available. Neutral, but the AFK timer starts here (not at socket connect).
                state.Initialized = true;
                state.LastActivityTime = now;
            }
            else if (sameCharacter)
            {
                if (inBed && !state.WasInBed) reasons |= ActivityReason.EnterBed;
                if (emoteId != state.LastEmoteId) reasons |= ActivityReason.Emote;
                if (equipment != state.LastEquipmentHash) reasons |= ActivityReason.Equipment;
                if (crafting != state.LastCrafting) reasons |= ActivityReason.CraftingStation;

                // Boarding/leaving a ship, entering/leaving a bed change the coordinate frame or move the player automatically
                // (e.g. wake-up): neutral for movement/look — this poll only re-baselines them.
                bool frameChanged = parent != state.Parent || inBed != state.WasInBed;
                if (!frameChanged)
                {
                    float dx = position.x - state.LastPosition.x, dz = position.z - state.LastPosition.z;
                    if (dx * dx + dz * dz > moveThresholdSqr) reasons |= ActivityReason.Movement;

                    if (hasLook && state.HasLookReference && Vector3.Angle(lookDir, state.LookReference) > lookThreshold)
                        reasons |= ActivityReason.Look;
                }
                else state.HasLookReference = false;
            }
            else
            {
                state.HasLookReference = false;           // respawn / back from dead or loading: neutral, re-baseline everything
                // The unobservable window neither counts as idle time nor as activity (respawn is automatic, not input).
                // Also when the whole dead/loading window fell between two polls and only the CharacterID change is seen.
                state.LastActivityTime += now - state.LastObservedTime;
            }

            // --- store baselines ---
            state.NeedsBaseline = false;
            state.LastObservedTime = now;
            state.CharacterId = characterId;
            state.Parent = parent;
            state.LastWorldPosition = worldPos;
            state.LastPosition = position;                 // per-poll displacement (a cumulative anchor would trip on slope drift)
            state.LastEmoteId = emoteId;
            state.LastEquipmentHash = equipment;
            state.LastCrafting = crafting;
            state.WasInBed = inBed;
            if (hasLook && (!state.HasLookReference || (reasons & ActivityReason.Look) != 0))
            {
                state.LookReference = lookDir;             // anchor: moves only on look activity, so slow deliberate turns still add up
                state.HasLookReference = true;
            }

            if (reasons != ActivityReason.None) MarkActivity(state, reasons, now);
        }

        /// <summary>Single place where activity is recorded and AFK -> ACTIVE happens (at most once per poll per player).</summary>
        void MarkActivity(PlayerAfkState state, ActivityReason reasons, float now)
        {
            state.LastActivityTime = now;
            if (state.IsAfk)
            {
                state.IsAfk = false;
                if (_config.DebugLogging) _log.LogInfo(state.PlayerName + " AFK -> ACTIVE (" + reasons + ")");
                _announcer.AnnounceReturn(state.PlayerName, state.LastWorldPosition);
            }
            else if (_config.DebugLogging) _log.LogInfo(state.PlayerName + " activity: " + reasons);
        }

        void RemoveDisconnected()
        {
            if (_states.Count == 0) return;
            _stale.Clear();
            foreach (KeyValuePair<long, PlayerAfkState> kv in _states)
                if (kv.Value.SeenGeneration != _generation) _stale.Add(kv.Key);
            for (int i = 0; i < _stale.Count; i++)
            {
                if (_config.DebugLogging) _log.LogInfo("session ended: " + _states[_stale[i]].PlayerName);
                _states.Remove(_stale[i]);                 // silent: no announcement on disconnect
            }
        }
    }
}
