using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AFKManager
{
    /// <summary>
    /// Optional module: new vanilla random raids do not start in an area where every player is AFK.
    /// One Harmony Postfix on the server-side <c>RandEventSystem.GetValidEventPoints(RandomEvent, ...)</c>. Vanilla builds the
    /// possible raid centres there — one point per eligible player, at that player's position — and only its two random
    /// selection paths call it (the periodic roll in StartRandomEvent and the per-event "standalone" roll). Forced, boss and
    /// admin events (SetForcedEvent / SetRandomEventByName) never pass through it, so they are untouched by construction.
    /// The Postfix removes a candidate point when players are inside that event's own area (RandomEvent.m_eventRange around
    /// the point) and every one of them is AFK. One active player in the area keeps the point, so an AFK player can never
    /// shield active ones. Running events are never looked at, let alone cancelled.
    /// </summary>
    internal static class RaidIntegration
    {
        const string HarmonyId = "torokal.afkmanager.raids";
        static AfkService _service;
        static AFKConfig _config;
        static ManualLogSource _log;
        static bool _errorLogged;

        internal static void TryEnable(AfkService service, AFKConfig config, ManualLogSource log)
        {
            _service = service; _config = config; _log = log;
            if (!config.ExcludeAfkFromRandomRaids) { log.LogInfo("Raid integration disabled by config."); return; }
            try
            {
                MethodInfo target = AccessTools.Method(typeof(RandEventSystem), "GetValidEventPoints");
                ParameterInfo[] parameters = target != null ? target.GetParameters() : null;
                if (target == null || target.ReturnType != typeof(List<Vector3>) || parameters.Length < 1 || parameters[0].ParameterType != typeof(RandomEvent))
                {
                    log.LogWarning("Raid integration unavailable: RandEventSystem.GetValidEventPoints(RandomEvent, ...) not found in this game version. Everything else is unaffected.");
                    return;
                }
                new Harmony(HarmonyId).Patch(target, postfix: new HarmonyMethod(typeof(RaidIntegration), "Postfix"));
                log.LogInfo("Raid integration enabled (Postfix on RandEventSystem.GetValidEventPoints).");
            }
            catch (Exception e)
            {
                log.LogWarning("Raid integration could not be enabled (" + e.GetType().Name + ": " + e.Message + "). Everything else is unaffected.");
            }
        }

        // ReSharper disable once UnusedMember.Local — invoked by Harmony. __0 = the RandomEvent being evaluated.
        static void Postfix(RandomEvent __0, List<Vector3> __result)
        {
            if (__result == null || __result.Count == 0 || __0 == null) return;
            if (_config == null || !_config.Enabled || !_config.ExcludeAfkFromRandomRaids) return;
            try
            {
                int removed = RemoveAfkOnlyAreas(__0, __result);
                if (removed > 0 && _config.DebugLogging)
                    _log.LogInfo("Random raid '" + __0.m_name + "': " + removed + " AFK-only target area(s) not eligible, " + __result.Count + " left.");
            }
            catch (Exception e)
            {
                if (!_errorLogged) { _errorLogged = true; _log.LogError("Raid integration error (vanilla result kept): " + e); }
            }
        }

        static readonly List<Vector3> _playerPositions = new List<Vector3>();
        static readonly List<bool> _playerAfk = new List<bool>();

        /// <summary>
        /// O(candidate points x connected players), only when vanilla evaluates a random event; no allocations (reused lists).
        /// Player positions are ZNetPeer.m_refPos — the same value vanilla builds the candidate points from.
        /// Sessions AFKManager has not initialized yet are not AFK, so they keep a point eligible.
        /// </summary>
        static int RemoveAfkOnlyAreas(RandomEvent randomEvent, List<Vector3> points)
        {
            ZNet znet = ZNet.instance;
            if (znet == null || _service == null || !znet.IsServer()) return 0;
            _playerPositions.Clear(); _playerAfk.Clear();
            List<ZNetPeer> peers = znet.GetPeers();
            for (int k = 0; k < peers.Count; k++)
            {
                ZNetPeer peer = peers[k];
                if (peer == null || !peer.IsReady()) continue;
                _playerPositions.Add(peer.m_refPos);
                _playerAfk.Add(_service.IsAfk(peer.m_uid));
            }
            return RemoveAfkOnlyAreas(points, randomEvent.m_eventRange, _playerPositions, _playerAfk);
        }

        /// <summary>
        /// The decision itself (pure): a candidate point is removed when at least one player is inside the event area around it
        /// (horizontal distance below the event's range, as in vanilla's IsInsideRandomEventArea) and all of them are AFK.
        /// A point with no tracked player in its area (e.g. the host of a non-dedicated game) is left to vanilla.
        /// </summary>
        internal static int RemoveAfkOnlyAreas(List<Vector3> points, float eventRange, List<Vector3> playerPositions, List<bool> playerAfk)
        {
            float range = Mathf.Max(eventRange, 1f);
            float rangeSqr = range * range;
            int removed = 0;
            for (int i = points.Count - 1; i >= 0; i--)
            {
                Vector3 point = points[i];
                bool anyone = false, active = false;
                for (int k = 0; k < playerPositions.Count; k++)
                {
                    float dx = playerPositions[k].x - point.x, dz = playerPositions[k].z - point.z;
                    if (dx * dx + dz * dz >= rangeSqr) continue;
                    anyone = true;
                    if (!playerAfk[k]) { active = true; break; }
                }
                if (anyone && !active) { points.RemoveAt(i); removed++; }
            }
            return removed;
        }
    }
}
