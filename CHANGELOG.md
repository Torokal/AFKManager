# Changelog

All notable changes to AFKManager are documented here. The project follows [Semantic Versioning](https://semver.org/).

## 0.3.1

- Rewritten description: what AFKManager does comes first, the technical details follow. Added a short trailer.
  No functional changes (the plugin DLL is the same as in 0.3.0).

## 0.3.0

- New: manual AFK. A player can mark themselves AFK at once by typing an emote in the chat window (`[Manual AFK]
  ManualAfkEmote`, default `/rest`). It only ever affects the player who typed it, repeating it does nothing, and any normal
  activity returns them to active. Sleeping in a bed cannot trigger it.
- Verified on a crossplay (`-crossplay` / PlayFab) dedicated server: detection, manual AFK, chat announcements and
  notifications all work there. PC Game Pass / Microsoft Store and console players never install anything, but their server
  must be started with `-crossplay`; see the README.
- No new Harmony patches, and no change to the existing detection, announcement, sleep or raid behaviour.

## 0.2.1

- Package description now mentions AFK-aware raid protection. No functional changes.

## 0.2.0

- New: AFK-aware random raids (`[Raids] ExcludeAfkFromRandomRaids`, default on). A new vanilla random raid does not start in
  an area where every player is AFK. An active player in the area still allows the raid, running raids are never
  cancelled, and boss, scripted and admin-started events are not affected. Server-side only.

## 0.1.0

Initial release.

- Server-side AFK detection for Valheim dedicated servers. Vanilla clients install nothing.
- Configurable AFK timeout (default 10 minutes).
- Activity signals: movement (relative to the ship while aboard), look direction, emotes, equipment changes,
  crafting-station open/close, getting into bed.
- Neutral events: login, death, respawn, boarding or leaving a ship, getting out of bed. The AFK timer is paused
  while a player is dead or loading.
- AFK / return announcements in the normal chat window (sender "AFKManager") and in the top-left message feed.
  Each channel can be switched off; message texts are configurable.
- Optional AFK-aware sleep: AFK players who are not in bed no longer block the night skip.
- `AFKManagerPlugin.IsAfk(long peerUid)` for other server-side mods.
- Tested on Valheim 1.0.15 (network version 40) with BepInExPack Valheim 5.4.2350.
