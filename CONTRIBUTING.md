# Contributing to AFKManager

Thanks for your interest! A few rules keep this mod useful and safe for server owners.

## The one rule that never bends

**AFKManager is server-side only.** A completely vanilla client must be able to join and play.
Pull requests that add client code, custom RPCs, handshakes, version checks, config sync, Jotunn or ServerSync will not
be merged, even if they would detect more activity.

## Design principles

- Only use data a dedicated server already receives from vanilla clients (the player's own ZDO and peer data).
- The AFK core uses **no Harmony patches**. The optional sleep module uses exactly one targeted Postfix. No transpilers,
  no patches on generic networking / RPC dispatch.
- Work is proportional to connected players: no world or scene scans, no per-frame work, no allocations in the check loop.
- A new activity signal must be verified on a dedicated server with a vanilla client before it is added. A false
  "active" is worse than a missing signal. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how detection works.
- Keep the configuration small and understandable for a normal server admin.

## Development

- Build: see the README (`build.ps1` without the .NET SDK, or `dotnet build`). The source stays C# 5 compatible so
  both work.
- Package: `packaging/build-package.ps1` creates the Thunderstore zip in `dist/`.
- Never commit game files, BepInEx binaries, decompiled code or IL dumps.

## Reporting issues

Please include the Valheim version, the BepInEx version, the other server mods, and the server `LogOutput.log`
(with `DebugLogging = true` if the problem is about detection).
