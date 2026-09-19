using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace AFKManager
{
    /// <summary>
    /// Optional module: AFK players who are not in bed stop blocking the night skip.
    /// The ONLY Harmony patch in AFKManager: a relax-only Postfix on the server-side
    /// <c>private bool Game.EverybodyIsTryingToSleep()</c>. It never turns true into false, so it composes with other sleep mods
    /// (a Postfix still runs when another mod's Prefix skips the original). If anything goes wrong the vanilla result is kept.
    /// </summary>
    internal static class SleepIntegration
    {
        const string HarmonyId = "torokal.afkmanager.sleep";
        static AfkService _service;
        static AFKConfig _config;
        static ManualLogSource _log;
        static bool _errorLogged;

        internal static void TryEnable(AfkService service, AFKConfig config, ManualLogSource log)
        {
            _service = service; _config = config; _log = log;
            if (!config.ExcludeAfkFromSleep) { log.LogInfo("Sleep integration disabled by config."); return; }
            try
            {
                MethodInfo target = AccessTools.Method(typeof(Game), "EverybodyIsTryingToSleep");
                if (target == null || target.ReturnType != typeof(bool))
                {
                    log.LogWarning("Sleep integration unavailable: Game.EverybodyIsTryingToSleep() not found in this game version. AFK detection is unaffected.");
                    return;
                }
                new Harmony(HarmonyId).Patch(target, postfix: new HarmonyMethod(typeof(SleepIntegration), "Postfix"));
                log.LogInfo("Sleep integration enabled (Postfix on Game.EverybodyIsTryingToSleep).");

                MethodInfo loop = AccessTools.Method(typeof(Game), "UpdateSleeping");
                Patches other = loop != null ? Harmony.GetPatchInfo(loop) : null;
                if (other != null && other.Prefixes.Count > 0)
                    log.LogWarning("Another mod patches Game.UpdateSleeping; if it replaces the vanilla sleep loop, ExcludeAfkFromSleep may have no effect.");
            }
            catch (Exception e)
            {
                log.LogWarning("Sleep integration could not be enabled (" + e.GetType().Name + ": " + e.Message + "). AFK detection is unaffected.");
            }
        }

        // ReSharper disable once UnusedMember.Local — invoked by Harmony
        static void Postfix(ref bool __result)
        {
            if (__result || _config == null || !_config.Enabled || !_config.ExcludeAfkFromSleep) return;
            try
            {
                if (Evaluate()) __result = true;
            }
            catch (Exception e)
            {
                if (!_errorLogged) { _errorLogged = true; _log.LogError("Sleep integration error (vanilla result kept): " + e); }
            }
        }

        /// <summary>
        /// Mirrors vanilla (all characters of ready peers must be in bed) with one relaxation: a player flagged AFK who is not in
        /// bed is ignored. Fail-safe rules: at least one player must actually be in bed; players that are not flagged AFK
        /// (including sessions AFKManager has not initialized yet) still block; loading/dead players are skipped exactly like
        /// vanilla skips them (they have no character ZDO).
        /// </summary>
        static bool Evaluate()
        {
            ZNet znet = ZNet.instance; ZDOMan zdoMan = ZDOMan.instance;
            if (znet == null || zdoMan == null || _service == null || !znet.IsServer()) return false;

            bool anyoneInBed = false;
            List<ZNetPeer> peers = znet.GetPeers();
            for (int i = 0; i < peers.Count; i++)
            {
                ZNetPeer peer = peers[i];
                if (peer == null || !peer.IsReady() || peer.m_characterID.IsNone()) continue;
                ZDO zdo = zdoMan.GetZDO(peer.m_characterID);
                if (zdo == null) continue;
                if (zdo.GetBool(ZDOVars.s_inBed, false)) { anyoneInBed = true; continue; }
                if (!_service.IsAfk(peer.m_uid)) return false;   // an active (or unknown) player is still up
            }
            return anyoneInBed;
        }
    }
}
