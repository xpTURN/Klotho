# Changelog

## [0.1.1] - 2026-04-29

- IMP-30: Unified Engine `_playerCount` semantics — roster source-of-truth consolidated to `_activePlayerIds.Count`. Replaced `OnPlayerCountChanged` with `OnPlayerJoinedNotification`.
- IMP-31: Split Spectator RoomRouter capacity gate — separated into two layers: an absolute upper bound (DoS protection, `MaxPlayersPerRoom + MaxSpectatorsPerRoom`) and the spectator slot gate (`HandleSpectatorJoin`), resolving the regression where spectators were blocked once 4 players filled the room.
- Added `SessionConfig.MaxSpectators` + wired into `BrawlerDedicatedServer` single-room and multi-room paths (including expanded Listen capacity). Removed the `maxPlayers` CLI argument.

## [0.1.0] - 2026-04-26

- First release
