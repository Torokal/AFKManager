using BepInEx;
using UnityEngine;

namespace AFKManager
{
    /// <summary>
    /// AFKManager — server-side only AFK detection for Valheim dedicated servers. Vanilla clients install nothing.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class AFKManagerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "torokal.afkmanager";
        public const string PluginName = "AFKManager";
        public const string PluginVersion = "0.3.0";

        static AfkService _service;
        AFKConfig _config;
        float _nextCheck;
        bool _wasServer;

        /// <summary>For other server-side mods: is this connected session (ZNetPeer.m_uid) currently AFK?</summary>
        public static bool IsAfk(long peerUid) { return _service != null && _service.IsAfk(peerUid); }

        void Awake()
        {
            _config = new AFKConfig(Config, Logger);
            AnnouncementService announcer = new AnnouncementService(_config, Logger);
            _service = new AfkService(_config, Logger, announcer);
            SleepIntegration.TryEnable(_service, _config, Logger);
            RaidIntegration.TryEnable(_service, _config, Logger);
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded (server-side only; does nothing on clients).");
        }

        // Per frame this is one float comparison; the real work runs every CheckIntervalSeconds on the main thread.
        void Update()
        {
            float now = Time.realtimeSinceStartup;       // monotonic; never world time (it jumps when the night is skipped)
            if (now < _nextCheck) return;
            _nextCheck = now + _config.CheckIntervalSeconds;

            bool isServer = ZNet.instance != null && ZNet.instance.IsServer();
            if (!isServer || !_config.Enabled)
            {
                if (_wasServer) { _service.Clear(); _wasServer = false; }
                return;
            }
            _wasServer = true;
            _service.Check(now);
        }
    }
}
