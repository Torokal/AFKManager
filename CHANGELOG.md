# Changelog

All notable changes to AFKManager are documented here. The project follows [Semantic Versioning](https://semver.org/).

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
