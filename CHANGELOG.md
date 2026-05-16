# Changelog

## [0.1.6] - 2026-05-16

- IMP-38: Added `KlothoState.Aborted` — abnormal terminal state distinct from `Finished`; `IsEnded()` extension covers both terminal states.
- IMP-38: `AbortMatch(AbortReason)` API + `OnMatchAborted` event — surfaces ChainStallTimeout / StateDivergence / ReconnectFailed reasons; Brawler subscriber includes double-trigger guard.
- IMP-38: P2P chain-stall watchdog — `CheckChainStallTimeout()`; calls `AbortMatch(ChainStallTimeout)` when lag ≥ `MinStallAbortTicks` ( or reconnect timeout + 100 ticks).
- IMP-38: Corrective Reset — host broadcasts `FullStateKind.CorrectiveReset` via `TryCorrectiveReset()` on hash mismatch; `ApplyReason` enum drives retreat policy; `CorrectiveResetCooldownMs` (default 5 s) prevents broadcast storms; `OnMatchReset(ResetReason)` event for non-terminal recovery.
- IMP-38: Hash gate hardening — `ApplyFullState` blocks post-state application when localHash ≠ remoteHash; `OnHashMismatch(tick, localHash, remoteHash)` event; desync/resync telemetry counters (`ResyncHashMismatchCount`, `ConsecutiveDesyncPeak`, `ResyncRequestTotalCount`, `PostResyncDesyncCount`, `UnexpectedFullStateDropCount`) surfaced in `PresumedDrop` metrics log.
- IMP-38: Reconnect input gap recovery — reconnecting peer input injection changed from `JoinTick` to `LastSentTick + 1`; `IsPlayerInActiveCatchup()` guard suppresses presumed-drop false positives during reconnect catchup.
- IMP-38: `OnPendingWipe(tick, playerId, WipeKind)` event — tracks inputs and SyncedEvents wiped before chain verification; added `RelaySealDropCount` counter.
- DS: `--rtt-metrics` CLI flag for single/multi-room.

## [0.1.5] - 2026-05-13

- IMP-38: RTT distribution metrics from `ServerNetworkService` with `RttMetricsEnabled` runtime toggle; structured metrics for LateJoin/Reconnect extraDelay (game-level playerId); preserve RTT sample across warm Reconnect.
- IMP-38: Phase 2 — clamp policy + interface promotion; replaced `ApplyExtraDelay` bool flag with `ExtraDelaySource` enum.
- IMP-38: Phase 3 — dynamic InputDelay mid-match push + reactive fallback; server-driven `RecommendedExtraDelay` for LateJoin/Reconnect cold-start path; same-tick multi-cmd allowed in monotonic clamp; `avgRtt` clamped to sanity cap; `Sync` extra-delay seed buffered until engine subscribed and branched per `JoinKind` in `SDClientService`; skip `LagReductionLatency` tracker on Reconnect.
- IMP-38: P2P port of `RecommendedExtraDelay` — `IKlothoEngine.IsHost` + `OnChainAdvanceBreak` event; route `RecommendedExtraDelayUpdate` to engine on peers; host `PingPong` hook → dynamic-delay push smoother (full pipeline); guest reactive fallback redesigned from chainbreak-burst to rollback-amplitude (with overflow guard); RTT spike schedule driver + match-scoped metrics collector.
- IMP-38: P2P reconnect hardening — host self-wipe + chain-advance stall P0 fixes (forward gap fill); 3-peer reconnect defects from Phase 2-1 playtest; catchup batch silent drop (defect ④); bundle-based stale peer cleanup on reconnect (dropped `_pendingPeers` keep guard); catchup `InputDelay` window + `Connect` socket cleanup; diagnostic log pruning from reconnect/relay paths.

## [0.1.4] - 2026-05-09

- IMP-36: Unified single-room SD on `RoomManager` bootstrap; exposed drain phase counters and lifetime metric.
- IMP-37: Closed multi-match determinism leak — gated `DeterminismVerification` assembly behind `UNITY_INCLUDE_TESTS` (drops typeId 9000–9002 from runtime layout), skipped `OnInitializeWorld` on SD client to prevent double-init race, added per-component hash dump + pool counters for desync diagnostics.
- IMP-38: Bootstrap & recovery hardening across phases — hardened `InputDelayTicks` validation for SD (Phase 0), state-driven spawn query + resync reconciliation hook (Phase 1), bootstrap handshake removing structural first-tick race (Phase 2), command rejection feedback unicast (Phase 3), fault-injection infrastructure & scenario matrix. Follow-ups: route initial `FullState` through bootstrap path on countdown-skip clients; hybrid (Version + OwnerId) dedup for ghost view; escalate spawn cmd lead on `PastTick` reject.

## [0.1.3] - 2026-04-30

- IMP-35: 3-layer defense against malformed wire packets — L1 `MessageSerializer.Deserialize` try/catch + cache invalidation (overflow-safe boundary check), L3 `Room.DrainInboundQueue` try/finally (guaranteed buffer recovery + loop continuation), L2 server `_pendingPeers` atomicity + immediate disconnect on malformed/unknown payload (pending and regular dispatch). Minimal `ZLogWarning` traceability at 3 client-side wire-input sites.

## [0.1.2] - 2026-04-30

- IMP-32: `LiteNetLibTransport` connection key — constructor injection with `DefaultConnectionKey` constant fallback.
- IMP-33: Propagate disconnect reason via `INetworkTransport.OnDisconnected` — added 6-value `DisconnectReason` enum; `Listen`/`Connect` now return `bool` to surface startup failures immediately. Client handlers gate auto-reconnect to `NetworkFailure`/`ReconnectRequested` only.
- IMP-34: 64-bit `SessionMagic` with CSPRNG generation + device-binding on cold-start reconnect.
- Aligned spectator transport connection key with the main transport.
- Aligned `Connect` API deviceId with the provider pattern; added send-site logging.

## [0.1.1] - 2026-04-29

- IMP-30: Unified Engine `_playerCount` semantics — roster source-of-truth consolidated to `_activePlayerIds.Count`. Replaced `OnPlayerCountChanged` with `OnPlayerJoinedNotification`.
- IMP-31: Split Spectator RoomRouter capacity gate — separated into two layers: an absolute upper bound (DoS protection, `MaxPlayersPerRoom + MaxSpectatorsPerRoom`) and the spectator slot gate (`HandleSpectatorJoin`), resolving the regression where spectators were blocked once 4 players filled the room.
- Added `SessionConfig.MaxSpectators` + wired into `BrawlerDedicatedServer` single-room and multi-room paths (including expanded Listen capacity). Removed the `maxPlayers` CLI argument.

## [0.1.0] - 2026-04-26

- First release
