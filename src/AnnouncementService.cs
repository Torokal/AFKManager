using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using Splatform;
using UnityEngine;

namespace AFKManager
{
    /// <summary>
    /// Announces AFK state transitions to vanilla clients. Two independent channels, both built only from network messages
    /// every vanilla client already understands (no custom RPC, no client mod, no Harmony patch):
    ///  - Notification: the vanilla "ShowMessage" routed RPC (the path vanilla uses for "World saved").
    ///  - Chat: the vanilla "ChatMessage" routed RPC. See <see cref="SendChat"/> for how a non-player sender is made valid.
    /// Only called on ACTIVE -> AFK and AFK -> ACTIVE, so it adds nothing to the periodic check.
    /// Nothing in here may throw into the detector: a failing channel logs one warning and the other channel still works.
    /// </summary>
    internal sealed class AnnouncementService
    {
        const int MaxNameLength = 32;
        const string ChatSenderName = "AFKManager";
        // "Prefix_id" parses to platform "AFKManager", id "0". Identity is the (platform, id) pair, never the display name:
        // PlatformUserID equality compares platform AND id. Real users always carry a store platform ("Steam", "Xbox",
        // "PlayStation", "Nintendo", "GameCenter"), and Server_devcommands' fake server uses "Steam"/"playfab", so this pair
        // cannot equal any of them. The entry has no character (ZDOID.None), so clients also never address chat to it.
        const string ChatSenderId = "AFKManager_0";
        // A vanilla chat message always also shows as an in-world text at the given position (off-screen ones are clamped to the
        // screen edge, like shouts). It is anchored above the player the message is about.
        static readonly Vector3 ChatBubbleOffset = new Vector3(0f, 1.9f, 0f);

        readonly AFKConfig _config;
        readonly ManualLogSource _log;
        readonly List<ZNetPeer> _recipients = new List<ZNetPeer>();
        // Private vanilla methods, resolved and validated once at plugin load. Either may be null: every use has a fallback.
        readonly MethodInfo _sendPlayerList;      // void ZNet.SendPlayerList()
        readonly MethodInfo _writePlayerInfo;     // ZPackage ZNet.WritePlayerInfo(List<ZNet.PlayerInfo>)
        bool _chatWarned, _notificationWarned;

        internal AnnouncementService(AFKConfig config, ManualLogSource log)
        {
            _config = config; _log = log;
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo send = typeof(ZNet).GetMethod("SendPlayerList", flags, null, Type.EmptyTypes, null);
                if (send != null && send.ReturnType == typeof(void)) _sendPlayerList = send;
                MethodInfo write = typeof(ZNet).GetMethod("WritePlayerInfo", flags, null, new[] { typeof(List<ZNet.PlayerInfo>) }, null);
                if (write != null && write.ReturnType == typeof(ZPackage)) _writePlayerInfo = write;
            }
            catch (Exception e) { _log.LogWarning("Could not inspect ZNet (" + e.GetType().Name + "): chat announcements use the built-in fallback."); }
            if (_sendPlayerList == null || _writePlayerInfo == null)
                _log.LogWarning("ZNet.SendPlayerList/WritePlayerInfo not found in this game version: chat announcements use a fallback " +
                    "that may hide other mods' player-list entries for up to 2 seconds per announcement.");
        }

        internal void AnnounceAfk(string playerName, Vector3 playerPosition) { if (_config.AnnounceAfk) Send(_config.AfkMessage, playerName, playerPosition); }
        internal void AnnounceReturn(string playerName, Vector3 playerPosition) { if (_config.AnnounceReturn) Send(_config.ReturnMessage, playerName, playerPosition); }

        void Send(string template, string playerName, Vector3 playerPosition)
        {
            if (string.IsNullOrEmpty(template) || ZRoutedRpc.instance == null) return;
            string text = template.Replace("{player}", SanitizeName(playerName));

            if (_config.AnnounceNotification)   // TopLeft = the small message feed; least intrusive during play.
            {
                try { ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage", (int)MessageHud.MessageType.TopLeft, text); }
                catch (Exception e)
                {
                    if (!_notificationWarned) { _notificationWarned = true; _log.LogWarning("Notification announcement failed: " + Describe(e)); }
                }
            }
            if (_config.AnnounceInChat)
            {
                try { SendChat(text, playerPosition + ChatBubbleOffset); }
                catch (Exception e) { WarnChat("Chat announcement failed (notifications are unaffected): " + Describe(e)); }
            }
            if (_config.DebugLogging) _log.LogInfo("announced: " + text);
        }

        /// <summary>
        /// Vanilla has no server/system chat type: a client only prints a "ChatMessage" when the sender's platform id is in the
        /// player list it received from the server (Terminal.AddString -> ZNet.TryGetPlayerByPlatformUserID), and it shows that
        /// entry's name. So, per transition: (1) send the vanilla "PlayerList" with one extra non-player entry "AFKManager",
        /// (2) send the vanilla "ChatMessage" from that identity — Steam clients accept senders from another platform
        /// synchronously (crossplay rule in RelationsManager), (3) let vanilla re-send the real player list immediately.
        /// All three travel in order on the same reliable connection, so the extra entry exists on a client only for the instant
        /// between two consecutive messages. Runs synchronously on the main thread: two transitions in one poll are two complete
        /// add/chat/restore sequences, never interleaved. Once step 1 went to anyone, step 3 always runs (finally); and even if
        /// step 3 failed completely, vanilla re-sends the real list by itself every 2 seconds.
        /// </summary>
        void SendChat(string text, Vector3 bubblePosition)
        {
            ZNet znet = ZNet.instance;
            if (znet == null || !znet.IsServer()) return;

            // Snapshot: the restore must reach exactly the peers that got the temporary list, whatever happens in between.
            _recipients.Clear();
            List<ZNetPeer> peers = znet.GetPeers();
            for (int i = 0; i < peers.Count; i++)
                if (peers[i] != null && peers[i].IsReady()) _recipients.Add(peers[i]);
            if (_recipients.Count == 0) return;

            bool environmentList; int entries;
            ZPackage list = BuildTemporaryList(znet, out environmentList, out entries);   // nothing sent yet: a failure here needs no restore
            UserInfo sender = new UserInfo();
            sender.Name = ChatSenderName;
            sender.UserId = new PlatformUserID(ChatSenderId);

            bool temporaryListSent = false, restoredByVanilla = false;
            int delivered = 0;
            try
            {
                for (int i = 0; i < _recipients.Count; i++)
                {
                    ZNetPeer peer = _recipients[i];
                    try
                    {
                        if (peer.m_rpc == null || !peer.m_rpc.IsConnected()) continue;   // left since the snapshot: nothing to send, nothing to undo
                        temporaryListSent = true;
                        peer.m_rpc.Invoke("PlayerList", list);
                        ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "ChatMessage", bubblePosition, (int)Talker.Type.Normal, sender, text);
                        delivered++;
                    }
                    catch (Exception e) { WarnChat("Chat announcement to one player failed (others and notifications are unaffected): " + Describe(e)); }
                }
            }
            finally
            {
                if (temporaryListSent) restoredByVanilla = RestorePlayerList(znet);
                _recipients.Clear();
            }
            if (_config.DebugLogging)
                _log.LogInfo("chat: delivered=" + delivered + " temporaryList=" + entries + " entries (" + (environmentList ? "environment+1" : "rebuilt+1") + ")" +
                    " restore=" + (restoredByVanilla ? "vanilla SendPlayerList" : "fallback"));
        }

        /// <summary>
        /// The temporary list = exactly what this server would send right now (vanilla's own WritePlayerInfo, so entries that
        /// other mods add there — e.g. Server_devcommands' "Server" — are kept) with the count raised by one and our entry appended.
        /// Fallback when that method is unavailable or fails: the list rebuilt from ZNet.GetPlayerList() (real players only).
        /// </summary>
        ZPackage BuildTemporaryList(ZNet znet, out bool environmentList, out int entries)
        {
            environmentList = false; entries = 0;
            ZPackage list = null;
            if (_writePlayerInfo != null)
            {
                try
                {
                    list = (ZPackage)_writePlayerInfo.Invoke(znet, new object[] { znet.GetPlayerList() });
                    int end = list.Size();
                    list.SetPos(0);
                    int count = list.ReadInt();
                    if (count < 0 || end < 4) throw new InvalidOperationException("unexpected player list package");
                    list.SetPos(0);
                    list.Write(count + 1);
                    entries = count + 1;
                    list.SetPos(end);
                    environmentList = true;
                }
                catch (Exception e)
                {
                    list = null;
                    WarnChat("ZNet.WritePlayerInfo could not be used (" + Describe(e) + "); chat announcements use a rebuilt player list.");
                }
            }
            if (list == null)
            {
                List<ZNet.PlayerInfo> players = znet.GetPlayerList();
                list = new ZPackage();
                list.Write(players.Count + 1);
                entries = players.Count + 1;
                WritePlayers(list, players);
            }
            WriteEntry(list, ChatSenderName, ZDOID.None, ChatSenderId, ChatSenderName, "", "", false, Vector3.zero);
            return list;
        }

        /// <summary>
        /// Never throws. Preferred: vanilla's own ZNet.SendPlayerList (refreshes and sends the list exactly as this server and its
        /// mods produce it). Fallback: the real players rebuilt by us — other mods' extra entries then come back with vanilla's
        /// next periodic send (at most 2 seconds later). Returns true when the vanilla path was used.
        /// </summary>
        bool RestorePlayerList(ZNet znet)
        {
            if (_sendPlayerList != null)
            {
                try { _sendPlayerList.Invoke(znet, null); return true; }
                catch (Exception e) { WarnChat("ZNet.SendPlayerList failed (" + Describe(e) + "); the player list was restored by the fallback."); }
            }
            try
            {
                List<ZNet.PlayerInfo> players = znet.GetPlayerList();
                ZPackage real = new ZPackage();
                real.Write(players.Count);
                WritePlayers(real, players);
                for (int i = 0; i < _recipients.Count; i++)
                {
                    try { if (_recipients[i].m_rpc != null) _recipients[i].m_rpc.Invoke("PlayerList", real); }
                    catch (Exception) { }   // this peer is going away; vanilla's periodic list covers everyone else
                }
            }
            catch (Exception e) { WarnChat("Player list fallback restore failed (" + Describe(e) + "); vanilla re-sends the list within 2 seconds."); }
            return false;
        }

        static void WritePlayers(ZPackage pkg, List<ZNet.PlayerInfo> players)
        {
            for (int i = 0; i < players.Count; i++)
            {
                ZNet.PlayerInfo p = players[i];
                WriteEntry(pkg, p.m_name, p.m_characterID, p.m_userInfo.m_id.ToString(), p.m_userInfo.m_displayName,
                    p.m_userInfo.m_serverAssignedDisplayName, p.m_userInfo.m_playfabId, p.m_publicPosition, p.m_position);
            }
        }

        // Same field order as vanilla ZNet.WritePlayerInfo / RPC_PlayerList.
        static void WriteEntry(ZPackage pkg, string name, ZDOID characterId, string platformId, string displayName,
            string serverAssignedName, string playfabId, bool publicPosition, Vector3 position)
        {
            pkg.Write(name ?? "");
            pkg.Write(characterId);
            pkg.Write(platformId ?? "");
            pkg.Write(displayName ?? "");
            pkg.Write(serverAssignedName ?? "");
            pkg.Write(playfabId ?? "");
            pkg.Write(publicPosition);
            if (publicPosition) pkg.Write(position);
        }

        // One chat warning per server run is enough to act on; repeats only in debug mode.
        void WarnChat(string message)
        {
            if (!_chatWarned) { _chatWarned = true; _log.LogWarning(message); }
            else if (_config.DebugLogging) _log.LogInfo(message);
        }

        static string Describe(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e.GetType().Name + ": " + e.Message;
        }

        /// <summary>
        /// Player names are user-controlled. Keep Unicode letters as they are, but remove what could change how the message
        /// is rendered: rich-text brackets, the '$' localization prefix, and control characters. Length is capped.
        /// </summary>
        internal static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length && sb.Length < MaxNameLength; i++)
            {
                char c = name[i];
                if (c == '<' || c == '>' || c == '$' || char.IsControl(c)) continue;
                sb.Append(c);
            }
            string result = sb.ToString().Trim();
            return result.Length == 0 ? "?" : result;
        }
    }
}
