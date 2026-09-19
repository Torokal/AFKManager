using BepInEx.Configuration;
using BepInEx.Logging;

namespace AFKManager
{
    /// <summary>User-facing settings. Values are re-read on every check, so edits to the .cfg apply after a config reload.</summary>
    internal sealed class AFKConfig
    {
        const float DefaultAfkMinutes = 10f, DefaultInterval = 5f, DefaultMove = 0.5f, DefaultLook = 10f;

        readonly ConfigEntry<bool> _enabled, _announceAfk, _announceReturn, _announceInChat, _announceNotification, _excludeFromSleep, _debug;
        readonly ConfigEntry<float> _afkAfterMinutes, _checkInterval, _moveThreshold, _lookThreshold;
        readonly ConfigEntry<string> _afkMessage, _returnMessage;
        readonly ManualLogSource _log;
        bool _warnedInvalid;

        internal AFKConfig(ConfigFile cfg, ManualLogSource log)
        {
            _log = log;
            _enabled = cfg.Bind("General", "Enabled", true,
                "Master switch. When false AFKManager does nothing (no detection, no announcements, no sleep changes).");
            _afkAfterMinutes = cfg.Bind("General", "AfkAfterMinutes", DefaultAfkMinutes, new ConfigDescription(
                "Minutes without any detected activity before a player is marked AFK. Small values (e.g. 1) are useful for testing.",
                new AcceptableValueRange<float>(0.1f, 1440f)));
            _checkInterval = cfg.Bind("General", "CheckIntervalSeconds", DefaultInterval, new ConfigDescription(
                "How often (seconds) connected players are checked. The check is extremely cheap; 5 is recommended. " +
                "The movement threshold below was validated for a 5 second interval.",
                new AcceptableValueRange<float>(1f, 60f)));

            _moveThreshold = cfg.Bind("Detection", "MovementThresholdMeters", DefaultMove, new ConfigDescription(
                "Horizontal distance (meters) a player must move between two checks to count as active. " +
                "0.5 safely ignores physics drift (e.g. slowly sliding on a slope). On a ship, movement relative to the ship is used, " +
                "so passengers standing still on a sailing ship do not count as moving.",
                new AcceptableValueRange<float>(0.05f, 10f)));
            _lookThreshold = cfg.Bind("Detection", "LookThresholdDegrees", DefaultLook, new ConfigDescription(
                "How far (degrees) a player must turn their view to count as active.",
                new AcceptableValueRange<float>(1f, 180f)));

            _announceAfk = cfg.Bind("Announcements", "AnnounceAfk", true, "Tell all players when someone becomes AFK.");
            _announceReturn = cfg.Bind("Announcements", "AnnounceReturn", true, "Tell all players when someone is no longer AFK.");
            _announceInChat = cfg.Bind("Announcements", "AnnounceInChat", true,
                "Show announcements in the normal chat window (sender name: AFKManager). Works with vanilla clients.");
            _announceNotification = cfg.Bind("Announcements", "AnnounceNotification", true,
                "Show announcements as a small notification in the top-left message feed. Works with vanilla clients.");
            _afkMessage = cfg.Bind("Announcements", "AfkMessage", "{player} is now AFK.",
                "Text shown when a player becomes AFK. {player} is replaced with the player's name.");
            _returnMessage = cfg.Bind("Announcements", "ReturnMessage", "{player} is no longer AFK.",
                "Text shown when a player returns. {player} is replaced with the player's name.");

            _excludeFromSleep = cfg.Bind("Gameplay", "ExcludeAfkFromSleep", true,
                "When true, AFK players who are not in bed no longer prevent the night from being skipped. " +
                "At least one player must still be in bed. Requires a server restart to fully enable/disable the hook.");

            _debug = cfg.Bind("Logging", "DebugLogging", false,
                "Log activity reasons, AFK transitions and a short summary of every check. Leave off for normal play.");
        }

        internal bool Enabled { get { return _enabled.Value; } }
        internal bool AnnounceAfk { get { return _announceAfk.Value; } }
        internal bool AnnounceReturn { get { return _announceReturn.Value; } }
        internal bool AnnounceInChat { get { return _announceInChat.Value; } }
        internal bool AnnounceNotification { get { return _announceNotification.Value; } }
        internal bool ExcludeAfkFromSleep { get { return _excludeFromSleep.Value; } }
        internal bool DebugLogging { get { return _debug.Value; } }
        internal string AfkMessage { get { return _afkMessage.Value; } }
        internal string ReturnMessage { get { return _returnMessage.Value; } }

        internal float AfkAfterSeconds { get { return Safe(_afkAfterMinutes, DefaultAfkMinutes, 0.1f, 1440f) * 60f; } }
        internal float CheckIntervalSeconds { get { return Safe(_checkInterval, DefaultInterval, 1f, 60f); } }
        internal float MovementThresholdMeters { get { return Safe(_moveThreshold, DefaultMove, 0.05f, 10f); } }
        internal float LookThresholdDegrees { get { return Safe(_lookThreshold, DefaultLook, 1f, 180f); } }

        // BepInEx clamps to the acceptable range, but NaN/Infinity can still come out of a hand-edited file.
        float Safe(ConfigEntry<float> entry, float fallback, float min, float max)
        {
            float v = entry.Value;
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                if (!_warnedInvalid)
                {
                    _warnedInvalid = true;
                    _log.LogWarning("Config value '" + entry.Definition.Key + "' is not a valid number; using default " + fallback + ".");
                }
                return fallback;
            }
            return v < min ? min : (v > max ? max : v);
        }
    }
}
