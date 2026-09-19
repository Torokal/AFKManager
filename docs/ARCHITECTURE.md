# AFKManager — Architecture

AFKManager is a BepInEx plugin for Valheim **dedicated servers**. Its one invariant: a completely vanilla client must be
able to join and play. Everything below follows from that.

## What a dedicated server can see

A dedicated server has no `Player` objects and never sees keyboard, mouse or UI input. What it does receive from every
vanilla client is the player's own **ZDO** (networked data object): position, the ship or bed the player is attached to,
look target, emote counter, visible equipment, a few animator values, the in-bed and dead flags. AFKManager reads only
that, plus the peer list (`ZNet.GetPeers()`).

Signals that would need hooks on the generic RPC dispatch (chat, attacks, interactions) are deliberately not used: they
are unreliable on a server (a lone player's chat never reaches it) and patching networking infrastructure hurts
compatibility with other mods.

## Source layout

| File | Role |
|---|---|
| `src/AFKManagerPlugin.cs` | BepInEx entry point. `Update()` is one float comparison; the check runs every `CheckIntervalSeconds`. Exposes `IsAfk(long peerUid)`. |
| `src/AFKConfig.cs` | Settings, range-validated and NaN-safe. |
| `src/PlayerAfkState.cs` | In-memory state of one connected session, keyed by `ZNetPeer.m_uid`. Never persisted. |
| `src/AfkService.cs` | The detector. No Harmony patches. |
| `src/AnnouncementService.cs` | Chat and notification announcements, sent on state transitions only. No Harmony patches. |
| `src/SleepIntegration.cs` | Optional sleep module: the only Harmony patch in the project. |

## Detection loop

Every `CheckIntervalSeconds` (default 5 s), on the main thread, for each ready peer:

1. Resolve the player ZDO: `ZDOMan.GetZDO(peer.m_characterID)`. If there is none, or the ZDO is flagged dead, the player
   is loading or dead: nothing is compared, the AFK timer is **paused**, and the next usable check only re-baselines.
2. Read the values once and compare them with the previous usable check:

   | Signal | Source | Rule |
   |---|---|---|
   | Movement | ZDO position, or `s_relPosHash` while attached to a parent (ship) | horizontal displacement since the last check > `MovementThresholdMeters` |
   | Look | `s_lookTarget` direction | angle to the last look anchor > `LookThresholdDegrees`; the anchor only moves on look activity, so slow deliberate turns add up |
   | Emote | `s_emoteID` | value changed |
   | Equipment | the nine visible-equipment keys | combined hash changed |
   | Crafting station | animator int `crafting` (`438569 + Animator.StringToHash("crafting")`) | value changed (open/close) |
   | Bed | `s_inBed` | false → true |

3. Neutral events only re-baseline and never count as activity: first spawn of the session, respawn / new character id,
   attaching to or leaving a parent (boarding, bed), leaving a bed. Movement and look are skipped for the check in which
   the coordinate frame changed.
4. If no activity was seen for `AfkAfterMinutes`, the session becomes AFK; any activity makes it active again. These two
   transitions are the only places announcements are sent, so each produces exactly one announcement.
5. Sessions that are no longer in the peer list are dropped silently.

Time comes from `Time.realtimeSinceStartup`, never from world time (which jumps when a night is skipped). Cost is
proportional to the number of connected players; there are no world or scene scans and no allocations in the loop.

## Announcements

Both channels use messages every vanilla client already understands.

**Notification:** the vanilla routed RPC `ShowMessage` (the one used for "World saved"), type `TopLeft`, to everybody.

**Chat:** vanilla has no server/system chat type. A client prints a `ChatMessage` only if the sender's platform id is in
the player list it received from the server, and it shows that entry's name. So, per transition and per ready peer,
in order on the same reliable connection:

1. the vanilla `PlayerList` message: the list the server would send right now, produced by the game's own
   `ZNet.WritePlayerInfo` (so entries added there by other mods are kept), with one extra entry
   `AFKManager` / platform id `AFKManager_0` / no character;
2. the vanilla `ChatMessage` (type Normal) from that identity, positioned above the player the message is about;
3. the real list again through the game's own `ZNet.SendPlayerList`.

The extra entry therefore exists on a client only between two consecutive messages. Properties:

- **No impersonation.** Clients identify the sender by platform **and** id. Platform `AFKManager` differs from every
  store platform (`Steam`, `Xbox`, `PlayStation`, `Nintendo`, `GameCenter`), so it can never equal a real player's id,
  whatever their display name. The entry has no character, so clients never address chat to it.
- **Failure safety.** Recipients are snapshotted; each one is sent to inside its own try/catch; the restore runs in a
  `finally` and never throws. Independently of that, the game re-sends the full player list every 2 seconds.
  Nothing in the announcement code can throw into the detector: a failing channel logs one warning and the other
  channel keeps working.
- **Reflection.** The two private `ZNet` methods are resolved and validated once at plugin load. If either is missing,
  a list rebuilt from `ZNet.GetPlayerList()` is used instead; in that fallback another mod's extra player-list entry may
  be absent on clients for up to 2 seconds per announcement.
- Steam clients accept a sender from another platform synchronously. Other client platforms are untested; if the chat
  line is dropped there, the notification is unaffected.

## Sleep module

One relax-only Harmony Postfix on the server-side `private bool Game.EverybodyIsTryingToSleep()`, with its own Harmony id.
It can only turn `false` into `true`:

- at least one player must actually be in bed;
- a player in bed always counts as a sleeper, even if flagged AFK;
- a player who is not in bed is ignored only if flagged AFK; unknown or not yet initialized sessions still block;
- loading or dead players are skipped exactly as vanilla skips them.

Because a Postfix still runs when another mod's Prefix skips the original, this composes with other mods that patch the
same method. If the method is missing in a future game version, the module disables itself with a warning and AFK
detection keeps working.

## Game members the plugin depends on

Useful when checking a new Valheim version.

| Area | Members |
|---|---|
| Peers | `ZNet.instance`, `ZNet.IsServer()`, `ZNet.GetPeers()`, `ZNetPeer.IsReady()`, `ZNetPeer.m_uid`, `m_playerName`, `m_characterID`, `m_rpc` |
| ZDO access | `ZDOMan.instance`, `ZDOMan.GetZDO(ZDOID)`, `ZDO.GetPosition()`, `GetVec3`, `GetInt`, `GetBool`, `GetConnectionZDOID(ZDOExtraData.ConnectionType.SyncTransform)` |
| ZDO keys | `ZDOVars.s_relPosHash`, `s_lookTarget`, `s_emoteID`, `s_inBed`, `s_dead`, and the nine equipment keys (`s_rightItem`, `s_leftItem`, `s_rightBackItem`, `s_leftBackItem`, `s_chestItem`, `s_legItem`, `s_helmetItem`, `s_shoulderItem`, `s_utilityItem`) |
| Notification | `ZRoutedRpc.InvokeRoutedRPC`, `ZRoutedRpc.Everybody`, RPC `ShowMessage`, `MessageHud.MessageType.TopLeft` |
| Chat | `ZNet.GetPlayerList()`, `ZNet.PlayerInfo`, `ZRpc.Invoke`, `ZRpc.IsConnected()`, `ZPackage` (`Write`, `Size`, `SetPos`, `ReadInt`), `ZDOID.None`, `UserInfo`, `Talker.Type.Normal`, `Splatform.PlatformUserID(string)`; by reflection with fallbacks: `ZNet.SendPlayerList()`, `ZNet.WritePlayerInfo(List<ZNet.PlayerInfo>)` |
| Sleep | `Game.EverybodyIsTryingToSleep()` |
