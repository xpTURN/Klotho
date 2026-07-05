# Changelog

## [0.5.3] - 2026-07-05

### Sample identity — dependency-free Ed25519

- **The reference identity backend now signs and verifies logins with a dependency-free, pure-C# Ed25519 instead of BouncyCastle.** The samples use Atropos — a Zero-GC RFC 8032 implementation — so the shared identity reference no longer bundles a ~4.6 MB crypto library: the .NET and Godot samples pull it from NuGet (`xpTURN.Atropos`) and the Unity samples reference it as a UPM git package. Nothing changes on the wire — Atropos is byte-identical to any conformant implementation, so tickets minted before the switch still verify and players see no difference.
- **Verifying a login ticket now allocates nothing.** The check a dedicated server runs on every join is Zero-GC after a one-time per-thread warm-up, where it previously allocated several kilobytes per verification; signing a ticket allocates only the returned signature. BouncyCastle remains only as an independent cross-check inside the identity tests, off every runtime path.

## [0.5.2] - 2026-07-05

### Logging — configurable timestamp format

- **The log timestamp format is now yours to set instead of being fixed.** A single `SetTimestampFormat` on the builder applies one format to every sink added afterwards (console and rolling file alike), and each sink can still override it — `AddConsole(format)` or the rolling-file options' `TimestampFormat`. The Unity (`KlothoLogger.CreateDefault`) and Godot (`GodotKlothoLogger.CreateDefault`) entry points each gained a matching `timestampFormat` parameter. Optional: the default is unchanged (`yyyy-MM-dd HH:mm:ss.fff`), so nothing changes unless a format is supplied.
- **A malformed format can no longer make logging throw.** A custom format is validated once when the sink is built and quietly falls back to the default if it is invalid, so a bad config degrades the timestamp rather than tearing down every write. The rolling-file sink also spills to a heap string for a format longer than its stack buffer, so an unusually long timestamp is never silently truncated.

### Navigation — deterministic corridor fix

- **Fixed a long path being truncated from the wrong end.** When a computed corridor exceeded the fixed capacity (`MAX_CORRIDOR`), `FPNavMeshPathfinder` kept the segment nearest the destination and dropped the part nearest the agent — so the corridor never contained the agent's current triangle, and corridor-advance tracking in `FPNavAgentSystem` lost the agent on any sufficiently long path. Truncation is now anchored at the start, keeping the triangles the agent actually walks through first. A length bound on the reconstruction guards against a malformed (cyclic) predecessor chain.

### Samples

- **A baked NavMesh sample was added to Brawler** — the `Field` scene plus its exported deterministic NavMesh data, so the navigation path can be exercised end to end.
- **The samples now log with a compact `HH:mm:ss.fff` timestamp** — the Brawler client and dedicated server, the Godot peer-to-peer and dedicated-server samples, and the Godot dedicated-server host all drop the date (it already lives in the log filename) while keeping the wall clock, using the new format hook.

## [0.5.1] - 2026-07-03

### Trusted player data (entitlements)

- **A player can now carry server-authoritative "entitlement" data into a match.** This is an opaque, lobby-signed blob attached when the login is verified — distinct from the client-authored player config, which a client can freely set. The engine carries it through the join and hands it to the game, which reads it to seed the simulation deterministically. Optional: with no entitlement path wired, nothing changes.
- **Start-of-match loadouts are now checked against what a player actually owns.** A game can supply a guard that cross-checks a client's selection against its entitlement and either passes it, clamps it to an owned default, or rejects it. On a dedicated server the decision is authoritative and broadcast once; in peer-to-peer every peer applies the same deterministic clamp — so an unowned pick can neither desync the match nor grant an unearned advantage.
- **In-match actions can be gated the same way.** A game can supply a gate that drops a client-submitted reliable command whose effect the player isn't entitled to — the action simply doesn't happen. This is aimed at ownership that lives off the wire (e.g. a server-only catalog) rather than in the replicated simulation.

> Guides: [Entitlement lifecycle](Docs/EntitlementLifecycle.md) (how the data flows origin → dispose) · [Lobby integration §9](Docs/LobbyIntegrationGuide.md) (which hooks to wire, plus an end-to-end play test).

### Stronger peer-to-peer trust

- **Each peer-to-peer guest can now re-verify a peer's login itself instead of trusting the host's word.** The host propagates the original lobby-signed ticket and every guest independently checks its signature, so no single peer is the sole authority on who everyone is. When not enabled, the previous host-relayed identity is used unchanged.

### Late join carries entitlements

- **A player joining mid-match now gets the same trusted-data treatment as players present from the start.** A new per-join world-seeding hook lets the game initialize that player's simulation state (e.g. an entitlement-derived loadout) deterministically at the exact tick they enter, on every peer, the same way tick-0 players are seeded. Loadout and action enforcement apply to late joiners too.

### Networking

- **Join ordering hardened for late join** — a join is now sequenced ahead of the same-tick gameplay commands, so the joining player's slot and world setup exist before anything else on that tick runs.
- **A reject reason coming back from a game's identity validator is now clamped to the identity range** — an out-of-range code could otherwise be read as a retryable "room full" and send the client into a retry-loop with a credential the authority had already rejected.
- **Fixed a peer-to-peer late-join desync when joining an in-progress match.** The joiner was not sent the confirmed inputs for the exact tick its state snapshot was taken at, so it re-simulated that tick with empty input while everyone else used the real input — its physics (positions/velocities) diverged within a single step, tripping the first post-join sync check and stalling the joiner out of the match. Most visible when joining during active movement/combat. The join now backfills inputs starting at the snapshot tick, matching the reconnect and desync-resync paths.

### API additions

- **Identity validation can return an entitlement blob alongside the verified account** via a new `Accept` overload; the existing one is unchanged.
- **The engine exposes each player's entitlement (`GetPlayerEntitlement`)** for use while seeding the world.
- **Peer-to-peer setup gained an opt-in to wire the loadout guard and enable ticket propagation.**

### Breaking changes

- **`ISimulationCallbacks` gained `OnPlayerJoinedWorld`.** Every implementer must add it; leave it empty when there is no per-join state to seed.
- **`IServerDrivenNetworkService` gained `GetPlayerEntitlement`.** Custom implementers must add it.
- **`IKlothoEngine` gained `GetPlayerEntitlement`.** Consumers only *calling* the engine are unaffected; a custom or mock engine implementation must add it.
- **Custom transports must deliver reliable-ordered messages on a single per-peer stream across both send and broadcast.** Late join relies on a roster update reaching a peer before the join that consumes it. The built-in transport already satisfies this; a custom `INetworkTransport` that routes broadcasts through a separate channel must be corrected.
- **A verified account longer than 62 UTF-8 bytes is now rejected at join instead of being silently truncated.** The account is an identity key, and truncation could collide two distinct accounts into one roster identity. A custom `IPlayerIdentityValidator` must ensure the account it accepts is at most 62 UTF-8 bytes; otherwise those users — previously admitted with a truncated account — are now turned away with the identity-invalid reason. Display names are unaffected (still truncated, as they are cosmetic). The reference validator already bounds account length, so it is unaffected.

### Samples

- **The Brawler sample now wires lobby entitlements** (config-driven on/off, mode-aware), demonstrating loadout seeding and in-match action gating end to end. The dedicated-server demo's guest permissions were aligned with the peer-to-peer rules.
- **Fixed the dedicated-server sample handing remote clients a loopback address** — the lobby now advertises the server's real address instead of the local one.

## [0.5.0] - 2026-06-28

### Verified player identity from the lobby

- **Players can now bring a verified identity into a match.** When a lobby issues a login ticket, the engine carries it through the join handshake and asks the game to verify it — an online check on dedicated servers, or an offline signature check in peer-to-peer — then shares the confirmed account and display name with everyone in the room. This is optional: with no lobby configured, sessions behave exactly as before.
- **A join with a bad login is turned away cleanly.** An expired, invalid, already-used, or banned login is rejected with a specific reason and without taking up a player slot, so the game can ask the user to sign in again instead of silently retrying.
- **Reconnecting players keep their identity.** A returning player's account and name are restored from the session rather than re-checked, because a one-time login ticket cannot be presented twice.
- **For LAN or lobby-less play, a client may suggest its own display name.** This unverified nickname is used only when no verifier is configured; whenever a lobby verifier is present it is ignored, so it cannot be used to impersonate someone.

### Consistent names and live presence

- **Every player now sees the same display name for each participant.** The host (peer-to-peer) or server (dedicated) is the single source of names and shares them through the room roster, so a player is no longer shown a different — or blank — name depending on who is looking.
- **Spectators and clients now see connection changes as they happen** — a player dropping, disconnecting, or reconnecting is reflected live rather than only at the next full sync.
- **A player's own ready state now updates immediately** on their own screen instead of waiting for a round-trip.

### Dedicated-server slot hygiene

- **A peer that connects but never joins no longer holds a slot forever.** On a dedicated server a peer could finish the transport connection (and, in a multi-room server, be routed to a room) yet never send its first message — to join, reconnect, or spectate — and the server would keep its slot reserved indefinitely, slowly starving the room of capacity. Such a peer is now disconnected after a short grace period (10s) if it stays silent, freeing the slot; a peer that joins, reconnects, or spectates within that window is unaffected.

### Serialization

- **`FixedString32`/`FixedString64` are now serialized directly.** These fixed-size text fields can be used in game-defined messages and configs without writing serialization code by hand.
- **`List<T>` and `T[]` of `[KlothoSerializableStruct]` elements are now serialized directly.** Lists and arrays of serializable structs are handled automatically wherever they appear, with no hand-written serialization code.

### Breaking changes

- **The player name field was renamed and an account field was added.** `IPlayerInfo.PlayerName` is now `IPlayerInfo.DisplayName`, and a new `IPlayerInfo.Account` (a stable login id, empty when no lobby is used) was added. Game code that read `PlayerName` must switch to `DisplayName`.
- **The join and roster network format changed, so clients and servers must run the same version.** How the player roster travels over the wire was unified into one compact per-player record; there is no compatibility with older builds on the wire.
- **A dedicated-server identity validator now receives the routed room.** `IdentityValidationRequest` gained a `RoomId` field and a matching constructor argument, so a server-driven validator can bind a ticket to the specific room the player was routed to (the lobby rejects a player who routes to a room their ticket is not bound to). Game code that *implements* a validator only reads the new field — no change required; only code that *constructs* `IdentityValidationRequest` (typically a custom validator's own unit tests) must pass the new argument.

### Dependencies — bundled `Unsafe.dll` and LZ4 removed

- **The package no longer ships `System.Runtime.CompilerServices.Unsafe.dll`** — Unity projects that also obtain this assembly from elsewhere (notably the Unity AI Assistant package, or Burst in IL2CPP player builds) could hit a compile error (`CS0103: 'Unsafe' does not exist…`) or an IL2CPP link failure caused by two copies of the same assembly identity. The handful of low-level helpers the core actually used (`SizeOf`/`As`/`AsRef`) are now implemented inside the package, so the conflicting bundled assembly is gone and the clash can no longer occur. No public API or runtime/determinism behavior change for consumers.
- **Replays are now written uncompressed** — the bundled LZ4 compressor (`K4os.Compression.LZ4`) was removed together with its transitive `Unsafe` dependency. New replay files are somewhat larger but pull in no third-party dependency. **Replay files saved by earlier versions (LZ4-compressed) can no longer be loaded**.

## [0.3.5] - 2026-06-23

### Pre-game lobby consistency

- **Players now see each other the moment they join or leave the lobby** — until a match started, the room only became consistent once the game began: a player who had just joined saw only itself, players already waiting were never told someone new had arrived, and a player leaving the lobby went unnoticed by the rest. Each peer now receives the full roster as it joins and is notified of every later join and leave, so the lobby reflects who is actually present — together with their ready state and names — well before the game starts. Applies to both peer-to-peer and server-driven sessions.
- **A room no longer gets stuck when an unready player leaves** — if everyone else had readied up and the last unready player left, the room could end up all-ready with nothing left to trigger the start, so the only way out was for everyone to leave and try again. A departure from the lobby now re-checks the start condition, so the match begins as soon as the remaining players are all ready. The check is guarded so a player leaving mid-countdown can neither restart nor cancel a start already under way.

## [0.3.4] - 2026-06-22

### Hierarchical FSM — framework upgrade

- **Safer graphs by construction** — the builder now validates the whole state hierarchy up front (with runtime depth guards as a backstop), so a malformed graph fails at build time instead of misbehaving later.
- **Transition inheritance** — a parent state's transitions now apply to all of its children, evaluated leaf→root so a child can preempt its parent; per-state OnUpdate runs across the whole active chain. Shared "escape" transitions can live once on a parent instead of being repeated on every child.
- **Configurable decision/action values (`AIParam`)** — decisions and actions read their thresholds through a small value-source layer, so the same logic can be fed a constant or a per-entity/runtime value (e.g. bot difficulty) without rewriting it. Allocation-free. Build-time checks back it up: every referenced value must be wired to a source (a forgotten wiring fails at init rather than resolving to a silent zero at runtime), and the sources — including custom ones — are scanned for determinism, so one that would desync is caught at build rather than mid-match.
- **Multiple HFSMs per entity (Compound Agent)** — a single entity can now run several independent HFSM axes at once (e.g. movement vs. combat) on the shared manager, with guidance on how the axes arbitrate shared resources. The required host-component layout is verified at build time, so a misplacement that would corrupt FSM state can't ship.
- **Lifecycle & cleanup** — context-injected init + explicit teardown, idempotent registration, named transition priorities for the Brawler bot, and read-only queries that return a safe "no FSM" answer for an entity that carries none instead of throwing. Re-triggering an already-pending event is idempotent.

### Session driver — selectable dt source

- **dt source is now selectable per driver** — the session driver measured its per-frame dt from an engine-independent real-time clock. It can now also drive the session from the engine's own frame delta (Unity `Time.deltaTime` / Godot `_Process` delta), chosen via a single option. The default keeps the existing real-time behavior, so nothing changes unless the engine-frame mode is opted into; the explicit-tick path for headless/deterministic loops is unaffected. Applied identically to the Unity and Godot drivers.

### Serialization — reusable inline struct codec

- **`[KlothoSerializableStruct]`** — bundle a group of fields into a reusable `unmanaged` struct that serializes inline wherever it appears, including inside components. Define a common field group once (e.g. an embedded FSM state) and let the generator emit its codec/hash automatically. Round-trip, hash, and nesting tests added.

### Runtime — per-tick allocation cleanup

- **Trimmed garbage-collector pressure on the hot path** — several spots that quietly allocated a small object on every simulation tick (and again on every rollback re-simulation) now reuse a cached one instead. The biggest were the physics step's trigger callbacks and the server-driven client's per-tick input-resend copies; a per-tick input-cleanup step and a handful of low-frequency network lookups and event messages were tidied up the same way. No behavioral or on-the-wire change — purely fewer short-lived allocations, which means steadier frame times with fewer GC-induced spikes.

### Memory — singleton component storage

- **Singleton components no longer reserve room for the whole entity budget** — a component marked as a singleton only ever has one instance in a match (e.g. the shared random seed or the match-end state), yet its storage was sized for as many copies as the maximum entity count, just like an ordinary component — nearly all of it permanently unused. Singletons now reserve a single value slot while keeping the full-size entity lookup table, so any entity can still carry one. The saving is multiplied across every rollback snapshot the engine keeps. The on-the-wire format and the per-tick state hash are unchanged, so determinism, full-state sync, and replays are unaffected.

### Unity 2022.3 LTS compatibility

- **AI Navigation dependency pinned to 1.1.7** — the 2.0 line requires Unity 2023.1+, breaking the package's declared 2022.3 minimum; 1.1.7 is the latest 1.x that supports 2022.3 LTS.
- **Unity 2022 validation sample** — a `Samples/Unity2022` project on 2022.3.62f3 to verify the package imports and compiles on 2022 LTS.

### Packaging — assembly isolation

- **Bundled Unsafe.dll renamed to avoid a duplicate-assembly clash** — Unity dedups precompiled assemblies by file name, so another package shipping its own `System.Runtime.CompilerServices.Unsafe.dll` (e.g. Unity AI Assistant) could win and hide the Unsafe type, causing CS0103. The bundled DLL is renamed to `xpTURN.Klotho.Unsafe.dll` with Auto Reference off, referenced explicitly by Runtime and LiteNetLib.

### Inspector

- **`[Header]` attribute target fixed** — `[Header]` on `[field: SerializeField]` auto-properties is now `[field: Header]` so it binds to the backing field, restoring inspector header grouping.

## [0.3.3] - 2026-06-18

### Synchronization diagnostics & robustness

- **Mesh geometry folded into the static-geometry fingerprint** — the diagnostic fingerprint of the loaded static colliders hashed only each mesh collider's placement (position/rotation), not its actual shape. Two peers that loaded the same scene but ended up with differing mesh vertices or triangle winding produced an identical fingerprint, so a geometry-only divergence stayed invisible. The mesh's vertex and index content is now folded in (computed once when the mesh is set, allocation-free), so such a divergence surfaces at its source. The fingerprint is a log-only diagnostic outside the per-tick state hash and is not serialized — determinism, consensus, and the wire format are unaffected.
- **Server-driven reconnect no longer cut short by the full-state abort timer** — on a server-driven client, a pending full-state request runs a short abort countdown (~15s) as its own safety net. If the server dropped while that request was outstanding, the client also began its much longer reconnect attempt (~60s) — but the short abort fired first and ended the match, throwing away a reconnect that could still have recovered. The abort countdown is now suspended for the duration of a reconnect: a successful reconnect's incoming full-state resolves the request naturally, and only if the reconnect itself ultimately fails does the match terminate. Ordinary reconnect failures with no full-state request outstanding are still left to the game layer, unchanged.

## [0.3.2] - 2026-06-18

### Reliable command channel

- **One-shot commands get a dedicated reliable channel (server-driven)** — actions that must land exactly once yet aren't latency-sensitive (character spawn, and similar buy/surrender-type commands) used to compete for the same per-tick input slot as movement and leaned on a retry / collision-avoidance workaround to get through. In server-driven mode they now travel a separate channel: the client submits without picking a tick and the server assigns one authoritative execution tick, so the command applies exactly once, in a stable order, on every peer — never predicted or rolled back. A per-player sequence guard makes a resend harmless. Peer-to-peer keeps the existing path; spawn is the first command moved over.
- **Owning player decided by the authority on placement** — a reliable command's player is now taken from the server's validated connection mapping rather than the submitted payload. Previously two players' spawns both collapsed onto the same default id, so only one character appeared and the other was dropped as a duplicate; fixing it also cleared a spurious mismatch in the client's command reconciliation.
- **Reliable-retry collision check false positive** — the guard that stops the legacy retry path from stamping over an in-flight command also fired on the command's own last-attempt tick, suppressing the empty filler that keeps the per-tick quorum advancing and risking a stall rather than preventing a collision. The bad clause was removed, so the guard now blocks only a genuine future-tick collision.

## [0.3.1] - 2026-06-17

### Multi-room dedicated server — straggler lifecycle thread safety

- **Straggler state-machine concurrency fixes** — three races in the multi-room server's straggler handling, surfacing only under `MaxRooms >= 2` load. A per-cycle barrier was stored under every ready room id and then waited/disposed twice (`ObjectDisposedException` → process abort) — it is now a single shared instance disposed exactly once. A timeout marked *all* ready rooms as stragglers, including ones that finished in budget (tick stalls, inflated counts that could force-close healthy rooms) — only rooms that didn't complete this cycle are marked now. And teardown could tear down a room whose worker tick was still running — guarded so a straggler is never collected mid-update.
- **`EndRequested` cross-thread publication fence** — the room's end-request timestamp is written by the worker thread (match-end/abort) and read by the main thread (join validation) without a publication fence, so the reader could see a stale/torn value. Split into a volatile bool gate plus its backing fields, written gate-last / read gate-first, so the companion fields are always visible; the public property and callers are unchanged.

### Input command model

- **Unified per-tick input command (`PlayerInputCommand`)** — the keep-first regression meant a move command claimed each `(tick, playerId)` slot, so a same-tick attack or skill was silently dropped as a duplicate. Move, attack, and skill are now folded into one per-tick command (move axis + a button bitmask + aim/skill-slot), so a single tick can carry movement *and* an action. A prediction hook clears one-shot bits (jump/attack/skill) on repeat so only movement repeats during prediction; humans and bots both emit the unified command, and the three legacy command types were removed.

### Build & packaging hygiene

- **Brawler server fixture-copy glob fix** — a pre-existing path glob for the Brawler server's `Data` test fixtures copied to the wrong location; corrected.

## [0.3.0] - 2026-06-16

### Repository layout

- **Unity adapter layer neutralized to a top-level sibling of Godot~** — the Unity-specific adapter was moved out of the engine-neutral core to a top-level folder, equal in standing to the Godot adapter, so no platform code lives inside the neutral core. 141 files moved with full rename tracking; assembly names and references are unchanged, and the repo-layout docs were realigned.

### Synchronization safety-net fixes — 2nd review

#### Desync detection & recovery ladder

- **Desync anchor veto order-dependence + counter masking (D-F1)** — in 3+ player P2P, a desync could roll back to a tick a peer had already flagged as diverged (depending on message order), and a global desync counter let one peer's match reset it so the resync threshold was never reached. The rollback anchor now keeps a 1-step history so a mismatch can demote it, and the desync counter is tracked per-peer so any single peer can still escalate to resync.
- **Pending-rollback / chain-gap hash mis-send** — two paths broadcast a sync-hash for a tick that was about to be rewound (or while the verified chain was still behind), which a remote peer saw as a false desync and an unnecessary rollback. Both now defer the hash send until the tick is actually verified, so the corrected hash is sent exactly once.
- **Recovery-ladder rung 3↔4 time arithmetic** — at the corrective-reset-budget / abort boundary, a stale in-flight failure report could abort the match too eagerly, while a timeout-type persistent failure decayed the budget faster than it accrued so the abort was never reached. The exhausted-budget path now decides by divergence tick (stale pre-reset reports are absorbed), and the decay window is widened to the worst-case report cadence so a persistent failure still reaches abort.
- **Rollback FullState-cache + post-apply SyncHash invalidation** — after a rollback the host could serve a stale cached full-state, and after a resync the network layer kept pre-reset peer hashes that produced false mismatches. The rollback now invalidates the full-state cache and a resync clears the stale per-tick peer hashes. (A minor early-game tick-0 anchor case is deferred.)
- **FullStateResync verified-chain starvation** — after a desync-triggered resync the requesting guest's verified chain could stall forever, because the resync path never re-supplied the input stream. The host now feeds catchup input batches to the resyncing peer and the guest ingests them, so the gap fills and the chain advances.
- **Residual low-risk fixes bundle (D-F10 · R-11 · Timescale)** — three single-site, behavior-neutral residuals from the post-G8 catalog: a full-state jump now clears orphan deferred check-tick entries that could otherwise cause a duplicate hash send or a small leak (D-F10); a late-join no longer completes on a corrective-reset broadcast, which would anchor catchup on the wrong tick (R-11, latent/defensive — the branch is currently unreachable); and the render clock now exposes its real drift-proportional convergence rate instead of a hardcoded 1.0, with no live consumer so behavior is unchanged (Timescale).

#### Server-Driven (SD) client

- **SD resync-path robustness** — three gaps on the Server-Driven client's full-state resync path, the SD counterpart of the P2P hardening. A lost resync response could leave the request flag stuck forever (a verified freeze) — the SD path now has its own timeout/retry that eventually fails the match locally. The two apply sites now honor the apply result (retry on a guarded skip, terminate on an unrecoverable hash mismatch). A currently-unreachable branch that didn't clear the flag is hardened as a guard against a future in-place-reconnect lifecycle.
- **SD input stamping/acceptance alignment (T-F4 · T-F7)** — on the SD client the local buffer (keep-first) and the server (last-write-wins) could keep different commands for the same tick, eating buttons and desyncing: an auto-injected empty could land on a real input's slot (T-F4), and a delay decrease could collapse several real inputs onto one tick (T-F7). The client now sends to the server only the command the local buffer actually kept, so both sides keep-first regardless of arrival order, with a server-side backstop that refuses to overwrite a stored real input with an empty.
- **SD fault-injection in-session reconnect** — the diagnostic disconnect→reconnect scenario did nothing on the SD client: the reconnect arm was wired only for the P2P service type, and the SD reconnect-completion path never restored the Playing phase. Both are fixed (an SD-specific arm + phase restore), unblocking SD measurement of the resync-robustness scenarios. Diagnostics-only; the normal reconnect path is unchanged.

#### Event system (pool / dispatch / exactly-once)

- **Shared EventPool engine-scoped ClearAll + spectator rollback diff truncation (E-3 · E-4)** — two single-site event defects. E-3: a full-state apply called a process-global pool clear, which in a multi-engine process (editor host+client, or a multi-room server) wiped other engines' pool tracking and stock — the redundant global clear is removed, since this engine's own buffer clear already covers it. E-4: a spectator rollback fired spurious event cancellations for its truncated prediction tail (a cancel→re-predict flicker) — a one-line guard skips that tail. Spectator-view only; P2P/SD are unaffected.
- **Match-end divergence policy: fire-forward backstop + state-based pause-grace gate (E-1-residual · E-7 · E-8)** — adopts a one-way "match-end fires forward, never un-fires" contract behind a new simulation state query. After a full-state restore the engine re-fires the match-end event once if the restored state is ended but it was never dispatched (recovering a match-end lost to a deep stall or a forward jump), and the pause-grace stop-injection gate is now state-based so a rollback that un-ends the match releases the gate without un-firing the latch.
- **Event pool Return leaks — gap-tick / replay-seek / SD-server-drop (E-5a · E-5b)** — three places raised events without returning them to the pool (a leak: pool-tracking growth in dev builds, per-event allocation in release). The SD gap-tick fill and the replay-seek re-simulation now open and drain the event collector and return the events, and the SD server's collector returns the regular events it drops. Events are output-only, so determinism is unaffected; an unused legacy collector was deleted.
- **Replay backward-seek watermark + dev-guard (E-6)** — after seeking a replay backward, synced events went silent (the dispatch watermark wasn't rewound) and a long backward seek spammed a dev-guard error. The seek now lowers the watermark and resets the event buffer (mirroring the spectator reset), so rewound playback re-dispatches correctly and the false errors stop.
- **Ring-wrap Synced dispatch guard (E-1)** — during a very deep P2P prediction stall the event ring could wrap, so a tick's synced events fired at the wrong tick and again at the real one (an exactly-once over-fire). Dispatch now skips any event whose stamped tick doesn't match the dispatch tick, so wrapped events fire exactly once at their real tick; normal play is unchanged. (The deeper under-fire loss inherent to the fixed-size ring is a known limit, covered by other backstops.)

#### Reconnect / late-join / disconnect

- **Reconnect/late-join catchup prediction reconcile** — a reconnecting or late-joining peer kept predicting an absent player's input, but the real input arriving via the catchup path was added without reconciling the misprediction, so the frozen state got promoted to verified and desynced. The catchup path now runs the same prediction-mismatch reconcile (rollback) as the normal receive path, with a resync fallback when the needed rollback is older than the snapshot window. (Smoke: zero desync after reconnect.)
- **Reconnect real-over-unsealed-empty overwrite (host → all P2P peers)** — a reconnecting player's real input was dropped when it landed on an unsealed empty placeholder the host had proactively filled, leaving that peer stuck on empty (a permanent divergence). A real input now overwrites an unsealed empty (with a rollback) on every peer, not just the host; a committed (sealed) slot still blocks it. (Smoke: gated drops fell to 0 on all clients.)
- **Cold-start reconnect late-join degradation** — in the diagnostic harness a returning guest could be misrouted as a late-join (saturating the roster and rejecting the next guest) because a stale prior network service sent a join message first. The harness now arms the in-session reconnect path directly so the returner sends a reconnect request first, matching production. Production is unaffected; the racy "live session on top of cold-start" combination is documented unsupported.
- **Graceful Reconnect on null credentials** — a reconnect attempt with no persisted credentials threw a null-reference instead of failing cleanly; it now reports a normal reconnect failure. Always-compiled hardening.
- **Late-join entity-id divergence** — a late-joining guest spawned its own character with a different entity id than its peers (an off-by-one that desynced every sync window), because the slot-creation gate keyed off the non-deterministic engine roster. It now keys off deterministic simulation state, so every node creates the slot exactly once. A state-determinism fix, unrelated to timing.
- **Late-join/reconnect guest timesync activation** — a late-join/reconnect guest never enabled its own frame-advantage throttle (only the normal start path did), so it never self-throttled — a behavior asymmetry purely by join path. Timesync is now enabled at the late-join/reconnect seed point. Local pacing only; no effect on determinism.
- **Quorum-miss presumed-drop watchdog hardening** — the watchdog could fire on a present, alive player and seal its slot with empty, amplifying a desync into a storm. It no longer self-releases on its own echoed fill (network arrivals only), the release condition is relaxed so a recovering peer isn't latched until the wall-clock timeout, and two false-positive thresholds are tightened. (Re-smoke: storm gone, single-resync convergence.)
- **Disconnected-peer empty-prediction** — during a disconnect window the guest predicted the departed player as repeat-last while the host filled empty, mismatching every tick and forcing a rollback each tick (visible stutter). A confirmed-disconnected peer is now predicted as empty, matching the host's fill, so no rollback fires. Speculative only; the verified chain and state hash are unchanged.
- **Guest-side disconnect awareness propagation** — in 3+ player P2P a guest had no way to learn another guest's drop, so it couldn't exclude that peer from the timing vote. The host now broadcasts roster transitions (disconnect/reconnect/leave) and guests mirror them, replacing the accidental reliance on a proxy-fill side effect. (Phase 1 of the frame-advantage exchange work.)
- **Quieter ChainBreak log during a disconnect window** — the per-tick chain-break warning spammed the log throughout a disconnect window; when every missing player is a confirmed-disconnected peer it now logs at debug instead of warning. Logging-only; no behavior change.

#### Timing / throttle / frame-advantage

- **Extra-delay lifecycle redesign — additive split + reactive server report** — reworks the adaptive input-delay so it stops ratcheting and racing. The delay is split into a server-authoritative baseline plus a client-reactive component; a server push now drains the reactive part instead of stacking on it, the reactive part de-escalates when conditions are stable, and the server/host absorbs a new client report (taking the max across peers in P2P). A clamp invariant is enforced and observability was added.
- **Frame-advantage real exchange + proactive disconnected-peer fill** — closes the timing-data-contamination series and removes the verified-chain trail during a disconnect. Each peer now piggybacks its own measured frame-advantage on the wire so the throttle recovers GGPO's one-way-delay cancellation (instead of a self-mirror), proxy-fills no longer pollute the timing vote, and the host fills a disconnected player's slots up to the input frontier so the chain stops trailing. **Wire format change — all clients must run the same build.**
- **P2P frame-advantage throttle revival (2-player) + companion fixes** — revives the voluntary slow-down throttle (a self-echo had collapsed the advantage estimate so a fast peer never slowed) and ships the two fixes it needs: the throttle no longer permanently latches (a wait is now a one-shot budget that always releases), and the advantage tick stays truthful across a full-state restore so a reconnecting guest isn't throttled to the cap.

### Synchronization safety-net fixes

- **P2P desync detection revived** — the local sync-hash was never stored, so hash comparison and the entire recovery ladder were dormant. The local hash is now stored and compared bidirectionally, with event-based anchor promotion.
- **Event ring trim removed** — a periodic trim wrapped the event ring and destroyed live events a few ticks old (corrupting rollback diffs and losing pending synced events). The ring now self-cleans at execution time, with dev guards added.
- **SD Hard-Tolerance deprecated** — the rejection deadline was structurally unreachable; the dead machinery was removed and the related config/enum kept as no-ops for wire compatibility.
- **Rollback/snapshot alignment** — fixed a retention off-by-one that made deep-rollback clamping dead, unified the snapshot-ring capacity, and clamped the SD prediction lead so it no longer overruns the ring.
- **FullState resync path hardening** — a retreat-guarded skip no longer masquerades as a successful apply (it re-arms the retry instead of wiping input history), post-apply hash mismatches now feed the escalation counter, duplicate desync reports for one tick count once, and the sync-check interval is clamped to stay within the rollback window.
- **Recovery ladder rungs 3–4 wired** — the ladder previously ended at rung 2 in live code. A guest→host failure report now drives a host-side corrective-reset budget, and exhausting it broadcasts a match-abort (opt-out available).
- **Synced-event divergence made observable** — a recovery rollback below the dispatch watermark could silently change already-fired synced history. A new divergence callback reports added/removed synced events (the exactly-once invariant is kept — nothing re-fires), with a match-end exception so the match can still end.
- **Timing/prediction minor fixes** — a batch of smaller fixes: the reject-tracking window now initializes correctly and shares the escalation cooldown; bounded back-scans; duplicate arrivals keep-first without a double pool-return; rollback resim runs in the resimulate stage; quorum checks by membership rather than count; and a failed self-test restores forward state and auto-disables after repeated failures.
- **Dead code & doc realignment** — removed an unused render-clock/EMA pair, fixed and exposed prediction-accuracy accounting, pooled the SD verified-batch list containers, and realigned the design and spec docs to the implementation.
- **BREAKING** — removed the never-fed engine-side snapshot store; simulations now own their rollback history and the engine queries them for the nearest restorable tick.
- **BREAKING** — the per-tick snapshot save joins the simulation interface, completing the snapshot-ownership unification, so non-ECS simulations that keep history can roll back on all paths. Implementations may no-op but must only expose ticks they can actually restore.
- **Command ownership contract documented + DEBUG diagnostic** — the single-owner rule for submitted commands was left unstated at the public surfaces and is now spelled out; a debug-only pool diagnostic rejects cross-thread, double, or foreign returns so misuse can't poison the pool.
- **Command lifetime/ownership unified** — one rule replaces the previous mixed scheme: a deserialized command has exactly one owner (the buffer if accepted, else the caller), and rejected arrivals are no longer pool-returned by the buffer. **BREAKING** — re-initializing the engine without stopping it now throws instead of silently double-subscribing.
- **Replay ghost network service removed** — a replay session wrongly attached a live network service to the main transport, polluting the server's RTT smoothing and leaking session messages into the replay engine. Replay sessions now run with no network service, plus a server-side guard that discards stale ping samples.

#### Docs & build hygiene

- **Documentation consistency + residual line re-alignment** — the final doc-consistency cleanup: realigned five design-doc sections to the implementation (clock offset is fixed at handshake; rung-2/3 state source; the host never self-escalates; the resync delta contract; event-ring self-clean), plus two trivial code touches (a comment-source fix and using the recording's tick interval during replay). Six items found to be live code defects rather than doc-only are tracked separately as future candidates.
- **Fault-injection salt field compiled unconditionally (editor/player layout parity)** — a fault-injection-only field compiled only under a build symbol caused an editor↔player serialization-layout mismatch (a player-build failure). The field is now always compiled (default, inert) while its behavior stays guarded, so the layout matches and runtime behavior is unchanged.

## [0.2.14] - 2026-06-10

### Godot physics tooling

- **FPStaticCollider exporter (IMP56)** — export Godot static colliders to `FPStaticCollider` `.bytes` (+ `.json` sidecar); converter and viewer wired as editor menu / FileSystem dock tools.
- **FPPhysics visualizer (IMP57)** — in-editor physics world overlay: debug panel, immediate-mode drawer, and world visualizer. Static collider viewer hardened (load robustness, HUD corner option).

### Samples & docs

- **GodotPolySample** — added test scenes + exported data.
- Docs — `PhysicsWorld.md` (deterministic physics overview), `PhysicsVisualizer.Godot.md`; adapter comments de-Unity'd; Installation/README touch-ups.

## [0.2.13] - 2026-06-09

### Godot NavMesh

- **Steep-slope traversal fix** — a ramp baked as one large triangle could be impassable (its Y-span exceeds the multi-floor threshold). Resolved data-side with no core change: re-bake with finer tessellation (`edge_max_length <= 3`).
- **Visualizer `Floor Y Thr` knob** — apply `MultiFloorYThreshold` live in the editor sim for diagnosis/tuning (editor-only; default 2.0, behavior unchanged unless raised).

### Packaging

- Moved the generated addon dist (`addons/klotho/`) to `dist/addons/klotho/` — it's a build artifact, not canonical source. Pure relocation; pack/deploy scripts updated.

## [0.2.12] - 2026-06-09

### Godot NavMesh tooling

- **NavMesh exporter** — `NavigationMesh` → `FPNavMesh` exporter; emits a `.bytes` payload plus a `.json` sidecar (shared `ToJson`).
- **Editor NavMesh visualizer** — load a NavMesh, run pathfinding, and simulate an agent in-editor.

### Godot Addon Packaging

- Renamed the addon DLL dir `bin/` → `lib/` so the prebuilt core/server DLLs are no longer dropped by default `bin` ignore filters during export/packaging.

## [0.2.11] - 2026-06-08

### Godot adapter parity

- **`GodotFlowSetupBuilderExtensions`** — adds `WithGodotDefaults()`: reads app version from `ProjectSettings` + injects `GodotDeviceIdProvider` via `WithHandshake` in one call; falls back to `"0.0.0"` when no version is set.
- **`KlothoSessionFlow.Logger` / `DeviceIdProvider` made public** — promoted from `internal` so cross-assembly Godot adapters can access them directly without re-passing as explicit parameters.
- **`GodotSessionFlowAsync` fix** — removed the explicit `logger` parameter from all join/reconnect methods; `flow.Logger` and `flow.DeviceIdProvider` are now referenced directly, matching `KlothoSessionFlowAsync` on the Unity side. Fixes `PlayerJoinMessage.DeviceId` always being empty.
- **Deterministic Geometry adapters** — added Godot conversion helpers for `FPRay3`, `FPPlane`, and `FPBounds3`.
  - `FPRay3`: tuple decomposition (`ToRay` / `ToFPRay3`) + `PhysicsRayQueryParameters3D` helper (`ToRayQuery`).
  - `FPPlane`: `ToPlane` / `ToFPPlane` — applies sign inversion (`Godot.Plane.D = −FPPlane.distance`; different equation convention, not a handedness issue).
  - `FPBounds3`: `ToAabb` / `ToFPBounds3` — maps `Godot.Aabb` Position=min-corner / Size=full-size layout.
- **`GodotKlothoLogger`** — adds `CreateDefault()`: `GodotLogSink` + `RollingFileSink` combined, defaulting to `ProjectSettings.GlobalizePath("user://logs")` (required for exported apps where relative paths are not writable).
- **Samples** — `GodotP2pSample` and `GodotSdSample` updated to use `KlothoFlowSetupBuilder` + `WithGodotDefaults()` and `GodotKlothoLogger.CreateDefault()`.

## [0.2.10] - 2026-06-07

### Fix

- `KlothoJsonContextMenu`: fixed `AddContextMenuItem` callback signature — `Callable.From(OnConvert)` wrapped a zero-argument method while Godot passes the selected paths array, causing an `ArgumentException`; changed to `Callable.From<string[]>` with a matching `Action<string[]>` signature.

## [0.2.9] - 2026-06-07

### Godot (.NET) support

- Godot view-layer adapter (`GodotEntityView`, `GodotEntityViewUpdater`, etc.) — opt-in view pooling, self-driven interpolation via `GodotSessionDriver`, `GodotSessionFlowAsync` join helpers, `GodotPlayerViewRegistry` (EVU auto-population), reconnect support, `EngineEventOneShot` port, `ErrorVisualState` desync blending, and Resource-based editor config assets.
- **GodotP2pSample** — standalone Godot 4 P2P sample (top-down 3D, file + console logging, export-safe asset loading).
- **GodotSdSample** — standalone Godot 4 ServerDriven sample; dedicated server moved to a sibling project `GodotSdSampleServer`.
- Klotho Godot addon (`addons/klotho/`) — stable script UIDs, no-op `EditorPlugin` (enable-able), DataAsset JSON→bytes editor tool, and `KlothoServer.dll` bundled for server builds.

### Fix

- Added fallback for `IDataAsset` `$type` mismatch across engine assembly names.
- `PlayerPrefsReconnectCredentialsStore`: switched JSON serializer from `JsonUtility` to Newtonsoft.Json.

### Docs

- Install guide split per engine (Unity / Godot) with server `.csproj` guidance.
- Official docs fully revised to reflect dual-engine support.

## [0.2.8] - 2026-06-03

### Packaging — flat layout (breaking)

- The framework package is promoted to the repository top level (`com.xpturn.klotho/`); the nested dev-project wrapper is gone. Samples now consume the package through a `file:` manifest reference to that single top-level package (no embedded copy).
- UPM install URL path changes to `?path=com.xpturn.klotho`. Dedicated-server csproj references the per-assembly projects under `com.xpturn.klotho/Server~/` directly.
- LICENSE files added.

### Session lifecycle — IKlothoSessionObserver is the only surface (breaking)

- `IKlothoSessionObserver` is now the single session-observation surface. The deprecated per-event `KlothoSession` state events and the per-mode `KlothoSessionFlow.On*SessionCreated` events are removed — session creation is observed through one `OnSessionCreated(session, SessionEntryKind kind)` callback (branch on `kind`).
- `Phase` / `AllPlayersReady` lifted onto the session facade; state transitions surface as `OnStateChanged` / `OnPhaseChanged` / `OnPlayerCountChanged` / `OnAllPlayersReadyChanged`.
- Session stop is idempotent through a single `OnSessionStopped` path; the driver's transient teardown guard is internal.

### Flow / role — unified entry (breaking)

- Guest join unified into a strategy-dispatched `JoinAsync(strategy, ...)`; `JoinP2PAsync` / `JoinServerDrivenAsync` are convenience overloads that delegate to it.
- Mode and local role resolved into a single `KlothoRole` (`P2PHost` / `P2PGuest` / `SdClient`).
- Join/reconnect reject reasons typed as enums (`JoinFailReason` / `JoinFailedException`, `ReconnectRejectReason`); guest join gains a `connectTimeoutMs`.
- The driver owns the main transport: it pumps it while idle and routes idle disconnects to `OnIdleDisconnected` (`BindTransport`); connect-attempt cancellation ownership moved into the flow.

### Builders & config

- `KlothoFlowSetupBuilder` for flow setup, with `WithReplaySave(path, dumpJson)` to declare the replay output path at build time.
- `RoomManagerConfigBuilder` for dedicated-server room config; `SimulationFactory` removed (the simulation is derived from the simulation config, e.g. `WithDerivedSimulation`). Per-room logging replaces the standalone simulation logger.
- `IKlothoEngine.InitFrame` added; samples read frames via `Engine.PredictedFrame.Frame` instead of an `EcsSimulation` downcast.

### Fixes

- ServerDriven reconnect was mis-routed as a late-join — the stale connection is now disposed on a failed handshake.
- ServerDriven client replay: the initial-state snapshot is persisted before recording starts.
- Server join-reject reason surfaced to the client via the disconnect payload.

### Docs

- `README.md` and `Docs/**` refreshed end-to-end for the flat layout and the consolidated session/flow API surface.

## [0.2.7] - 2026-05-31

### ECS — HFSMBuilder (IMP49)

- New fluent `HFSMBuilder` (`Runtime/ECS/FSM/`) replaces the manual array-of-structs HFSM graph with a `State/Default/To/OnEnter/OnUpdate/OnExit` chain. `Build()` validates the graph at registration and fails fast on structural defects (duplicate ids, dangling target/parent/defaultChild, default-not-set, dense `States[i].StateId == i`), runs a reachability BFS, and stably sorts each state's transitions by descending priority. Since the runtime evaluates transitions by array order (not the `Priority` field), the stable descending sort is what gives priority its meaning. Advisory findings (unreachable / duplicate priority / self-transition) warn via `IKLogger` by default and throw under strict.
- `BotHFSMRoot` converted to the builder (graph unchanged — transitions were already descending, so the sorted arrays match 1:1), keeping the `Has()` idempotency guard and decision/action instances.
- `HFSMBuilderTests`: synthetic-graph coverage for each structural throw, advisory warn-vs-strict, stable sort, and same-target transitions.

### Analyzer — DeterminismAnalyzer (DET002~004)

- First true `DiagnosticAnalyzer` in the `KlothoGenerator` DLL (existing ones are generator-embedded helpers). Surfaces determinism hazards at build time instead of waiting until replay/rollback desync. Rules (Category `KlothoGenerator.Determinism`, Warning): `KLOTHO_DET002` (float/double in a deterministic context), `KLOTHO_DET003` (non-deterministic API/type — `Mathf`, `Random`, `System.Math`, `DateTime`, float-backed `UnityEngine.Vector2/3/4`/`Quaternion`/`Matrix4x4`), `KLOTHO_DET004` (`UnityEngine.Time` wall-clock).
- Context gating (`CompilationStart` → `SymbolStart(NamedType)` → `OperationAction`): rules 002/003 fire only on types implementing a deterministic interface or inheriting a deterministic base; rule scanning for ref-`Frame` helper types is limited to their ref-`Frame` methods (covers `FPNavAvoidance`, `CombatHelper`, `BotFSMHelper`). The FP64 conversion boundary (`FromFloat`/`FromDouble`/`ToFloat`/`ToDouble`/`ToFP64`) exempts argument float-ness while DET003/004 still scan the argument subtree. Test/tool assemblies are skipped.
- Tests: `Tools/KlothoGenerator.Tests` (NUnit, manual analyzer driver) — 10/10 pass.

### Flow — StartHostAndListen single-entry host bootstrap

- `KlothoSessionFlow.StartHostAndListen` folds the `StartHost` + `HostGame` + `Transport.Listen` sequence into a single call with framework-side teardown, matching the guest path's single-call symmetry. Reads `MaxPlayers` from `sessionConfig` once and listens via `KlothoFlowSetup.Transport`. Listen-bind failure returns `null` (recoverable); other failures (`HostGame`/`CreateRoom`) `Stop()` then rethrow, so a half-started session is never orphaned.
- P2pSample implements `IKlothoSessionObserver` + wires `LifecycleObserver` so `session.Stop()` cascades to game-side teardown via `OnSessionStopped`; teardown converged into `StopGame()` (IsStopping-guarded). Brawler aligned to `StartHostAndListen`, preserving post-success `SendPlayerConfig` / menu transition. `GameDevAPI.md`: documents `StartHostAndListen`; marks `StartHost` as a low-level escape hatch.

### Logging — re-entrancy / shutdown hardening

- `CharBufferWriter`: per-thread reusable buffer switched from an always-shared `[ThreadStatic]` array to a rent/return pool. A re-entrant log on the same thread (e.g. an argument's `ToString` that logs) now finds the buffer already rented and allocates its own, so the outer message is never corrupted; the pool self-heals if a buffer is not returned (exception mid-format). The non-re-entrant path stays allocation-free.
- `RollingFileSink.Write`: drops the line when termination has begun (`_stopping`) instead of reopening a writer that would never be flushed or closed — prevents a file-handle leak and a stray log file when a write races with `Dispose`/`ProcessExit`.

### Samples

- **LoggingMelConsole** — new console sample (`Samples/LoggingMelConsole`) routing Klotho's `IKLogger` surface through a standard `Microsoft.Extensions.Logging` pipeline via `MelKLogger`, logging to both console and rolling files under `Logs/` with a ZLogger provider.
- **P2p/Sd HUD** — `P2pHud`/`SdHud` frame access lifted from the unsafe `((EcsSimulation)engine.Simulation).Frame` downcast to the typed `Engine.PredictedFrame.Frame` accessor, aligning the intro samples with Brawler's standard View pattern. Per-tick polling gains an explicit `Frame == null` guard; one-shot init swaps the cast only (frame presence is a hard precondition). Sim-side init casts preserved (authoritative write paths).
- **SdSample** — tick rate raised to 60Hz (`TickIntervalMs` 33 → 16, synced across server `simulationconfig.json` + client `SimulationConfig.asset`), halving per-tick input-stamp and remote-render latency from ~66ms to ~32ms at unchanged InputDelay/Interp ticks.

## [0.2.6] - 2026-05-30

### Dedicated server — project references

- New per-assembly server projects under `Server~/` that mirror the client asmdef boundaries (`LiteNetLib`, `xpTURN.Klotho.Logging` / `.Runtime` / `.Gameplay` / `.LiteNetLib`, and a `KlothoServer` aggregate holding the server-only helpers), as an alternative to source-sharing via `KlothoServer.Core.props`. Server helpers (`ConfigPathResolver` / `SessionConfigLoader` / `SimulationConfigLoader`) moved into `KlothoServer/`. Existing `.props`-based builds unchanged.
- With Klotho split across referenced assemblies, registration runs through `[ModuleInitializer]` (fires only once an assembly loads). New `KlothoServerBootstrap` loads every Klotho/game assembly from the deploy directory and runs warmups before any factory is built — a directory scan (not a reference walk, since the compiler drops references to assemblies whose types are never used directly). Assumes a multi-file, untrimmed deploy.
- `BrawlerDedicatedServer` migrated to ProjectReferences + `KlothoGenerator` analyzer; `Program.cs` calls `KlothoServerBootstrap.Initialize("Brawler")` at startup. Verified: build clean, `--test` suite passes (79 tests, 0 failures).
- `DeterminismVerification` migrated from ~50 lines of hand-listed source includes to ProjectReferences (Runtime + Logging only — no networking, so no KlothoServer/Gameplay/transport); `Program.cs` calls `WarmupRegistry.RunAll()`. Verified: determinism run passes (3 seeds × 10k ticks + seed-42 self-verify).
- `SdSample` embedded Klotho synced to the same mirror layout; `SdSampleServer` migrated to ProjectReferences with `KlothoServerBootstrap.Initialize("SdSample", "xpTURN.Samples")`. Verified: build clean, server starts and warmup serializes all registered commands/messages with no errors.

### Docs

- Dedicated-server guidance updated from props source-sharing to project references.

## [0.2.5] - 2026-05-30

### Sample — P2pSample

- New minimum P2P sample under `Samples/P2pSample/` — a 2-player sumo match (cube-on-plane, WASD move, push-off-edge respawn, 60s GameOverEvent). Independent Unity 6.3 project sibling of `Klotho/`, consumes `com.xpturn.klotho 0.2.4` via the same UPM git URL that external users would use (`?path=Klotho/Packages/com.xpturn.klotho#v0.2.4`). Game-side LOC ≈ 795 across 15 files (Sim 7 / View 8) — ~66% the size of Brawler with the same deterministic-core + view-bootstrap pattern. Visuals use Unity built-in primitives only (Cube / Plane) — no external assets.
- Documentation: [`Samples/P2pSample/README.md`](Samples/P2pSample/README.md) (4-step quick start) and [`Docs/Samples/P2pSample.md`](Docs/Samples/P2pSample.md) (architecture walkthrough + common pitfalls). README "Optional samples" anchor updated.
- Common pitfalls surfaced while building this sample (now documented in `Docs/Samples/P2pSample.md` §6): Synced events dispatch via `engine.OnSyncedEvent` (not `OnEventConfirmed`); `session.HostGame(...)` is mandatory after `flow.StartHost(...)` to activate host role; `[KlothoOrder(N)]` is required on every DataAsset field (else generator skips serialization → `.bytes` truncated to header); JoinP2PAsync cancellation does not unwind LiteNetLib peer registration, so a re-entry guard is needed; `AllPlayersReadyChanged` is host-only — guest UI must derive from `SessionPhase` instead.

### Sample — SdSample

- New minimum **ServerDriven** sample under `Samples/SdSample/` — the dedicated-server sibling of P2pSample: same 2-player sumo game, but a Unity client + a minimal .NET 8 dedicated server (`Server/`). `Sim` deterministic code is reused from P2pSample (renamespaced); the server `.csproj` source-shares the same `Sim/*.cs` via the package's `Server~/KlothoServer.Core.props` (KlothoGenerator single-compilation), and uses `RoomManager(MaxRooms=1)` + `RoomRouter` + `ServerLoop`. Client uses `JoinServerDrivenAsync` (no local host), `Mode=ServerDriven`. Game-side LOC ≈ 916 (Sim 292 / Client View 540 / Server 107). Verified: server 1 + 2 clients, server-authoritative + client prediction, per-component hash match (deterministic), WASD move, fall-off respawn, 60s GameOver clean shutdown.
- Documentation: [`Samples/SdSample/README.md`](Samples/SdSample/README.md) (server + 2 clients quick start) and [`Docs/Samples/SdSample.md`](Docs/Samples/SdSample.md) (architecture + SD-specific gotchas). README "Optional samples" anchor updated.
- SD-specific pitfalls surfaced reusing a P2P game on a server topology (documented in `Docs/Samples/SdSample.md` §5): the SD client skips `OnInitializeWorld` (boots from the server FullState), so callbacks must not depend on state cached there — `OnPollInput` dropped all input via a stale `_engine` guard (SG11), and the static ground registered there was missing on the client → landing-tick desync, fixed by registering statics in `RegisterSystems` which runs on every peer (SG14); SD network playerIds are 1-based, so entity spawn / id mapping must match `MoveCommand.PlayerId` (SG12); the match-end Synced event must implement `IMatchEndEvent` or the server never drains (SG13).

### Logging — AsyncEvent flush (opt-in)

- `RollingFileSink` gains an opt-in `KFlushMode` (`PerLine` default — unchanged behavior; `AsyncEvent` — event-driven background-thread flush). `AsyncEvent` removes the per-line flush syscall from the hot path and coalesces bursts into a single flush, while a unified termination path (`Dispose` / `ProcessExit` / `DomainUnload`) drains the tail on graceful exit. Exposed via `KRollingFileOptions.FlushMode` / `AddRollingFile`.
- Dedicated server (`Tools/BrawlerDedicatedServer`) now selects `AsyncEvent` for its rolling file log.

### Diagnostics — static collider fingerprint (IMP48)

- Regression guard for the SD client static-ground-missing desync: static colliders are not part of the state hash, so the omission used to surface only as a downstream dynamic-body desync. A low-cost `FPHash` fingerprint (collider count + per-collider fields) is now logged at boot (`[Physics][StaticGeometry] boot: count=N fp=0x...` — a server-vs-client diff reveals a `count=0` omission) and in the desync dumps. Core state hash / snapshot serialization unchanged.
- `FullStateResponseMessage` gains a `StaticFingerprint` field — the server self-populates it on every FullState send and the client compares on receive, logging `[KlothoEngine] Static geometry mismatch: ...` at join (before the desync, pointing at the cause); `0` is a "not provided" sentinel, logged once per divergence episode. No interface / delegate / `ApplyFullState` signature changes; `IStaticColliderService` gains `GetStaticFingerprint()`.
- Verified (Brawler SD): normal match server = client fingerprint match with zero false positives; with the client static dropped, the mismatch `KError` fires at the join tick ahead of the first `Determinism failure`.

## [0.2.4c] - 2026-05-29

### Fix — Logging file sink

- `RollingFileSink`: per-line flush + `ProcessExit` hook — logs immediately visible, Unity-client quit-time tail loss fixed.

## [0.2.4] - 2026-05-28

### Logging — IKLogger transition (breaking)

- ZLogger + `Microsoft.Extensions.Logging` dependencies fully removed → in-house `xpTURN.Klotho.Logging` (`IKLogger` + `KLogHandler{Trace,Debug,Information,Warning,Error,Critical}` ref-struct interpolated handlers + `KInformation` / `KDebug` / `KWarning` / `KError` extension methods + `UnityDebugSink` / Rolling-File sink). Core stays zero external-logging dependency with `noEngineReferences: true`.
- Optional MEL interop: `Plugins~/Logging.Mel` adapter (`xpTURN.Klotho.Logging.Mel`) — activates when the consumer provides `Microsoft.Extensions.Logging.Abstractions` DLL. Exposed through the UPM "Import Sample" mechanism.
- Game-side call migration: `_logger?.ZLogInformation(...)` → `_logger?.KInformation(...)` (same for other levels).

### Packaging — IMP47 UPM (breaking layout)

- Framework relocated to an embedded UPM package (`com.xpturn.klotho`) at `Klotho/Packages/com.xpturn.klotho/` (`Runtime`, `Editor`, `Plugins/Analyzers`, `Prefabs`, `Plugins~/Logging.Mel`, `Server~`). asmdef GUIDs preserved + `.meta` files moved alongside sources — cross-asmdef references remain intact.
- Dev project flattening: `Assets/Klotho/{Samples/Brawler, Samples/NavMesh, Tests, Benchmarks}` → `Assets/{Brawler, NavMesh, Tests, Benchmarks}` (the `Klotho/` namespace folder is gone).
- External install: open Unity Package Manager → "Add package from git URL..." → add `UniTask → Polyfill → Klotho` (`https://github.com/xpTURN/Klotho.git?path=Klotho/Packages/com.xpturn.klotho`) in order. New "Installation" section in README.
- Third-party source vendoring under `Runtime/ThirdParty/`: `LiteNetLib.v2.1.4` / `K4os.Compression.LZ4.v1.3.8` / `System.Runtime.CompilerServices.Unsafe.v6.1.2`. NuGetForUnity removed (`packages.config` is empty, core `precompiledReferences` are empty).
- Three debug/visualization prefabs (`EcsDebugBridge`, `FPPhysicsWorldVisualizer`, `FPStaticColliderVisualizer`) moved into the package's `Prefabs/`.

### Dedicated server build

- `Tools/Server/` → `Packages/.../Server~/`: `KlothoServer.Core.props` uses `$(MSBuildThisFileDirectory)`-relative paths so it works identically in embedded and PackageCache layouts. `ProjectReference KlothoGenerator.csproj` → `<Analyzer Include="...\Plugins\Analyzers\KlothoGenerator.dll" />` (consumer-compatible). ItemGroup consolidated into a single `Runtime/**/*.cs` glob with `Unity/` and `**/Json/**` excludes.
- Because the source generator requires source-sharing, a consumer dedicated server pulls all of Tier 1-3 (Runtime / Logging / Gameplay / LiteNetLib transport / ThirdParty) with a single `<Import Project="Packages\com.xpturn.klotho\Server~\KlothoServer.Core.props" />`. Two install patterns — (A) git submodule (recommended) or (B) PackageCache + `<KlothoPackageRoot>` — documented in the README "Dedicated server" section.

### Source generator

- `KlothoSerializationGenerator.ResolveProjectRoot` now recognizes the `/Packages/` marker in addition to `/Assets/` → moved core assemblies' `Tools/Generated/` debug-dump output restored. Consumer paths under `Library/PackageCache/com.xpturn.klotho@<hash>/...` match neither marker → `projectRoot=null` → emit skipped automatically (no PackageCache pollution on the consumer side).

### Docs

- `README.md` (Tech Stack / Repository Layout / new Installation / Dedicated server / Sample path), `Docs/BaseLibraries.md` (full rewrite — IKLogger / Vendored restructure), `Docs/Specification.md` (Directory Layout tree rewritten), `Docs/FEATURES.md`, `Docs/GameDevAPI.md`, `Docs/Navigation.md`, `Docs/SimulationConfigGuide.md`, and `Docs/Samples/Brawler.{md, E.Bootstrap, F.SceneNumbers, H.DedicatedServer, I.HowToRun}.md` updated end-to-end for the new package layout / flattening / IKLogger transition. Brawler.H gains a "Reference template" section (Brawler-vs-consumer mapping table). All 77 markdown links validated.

## [0.2.3] - 2026-05-25

- IMP-46 A: `INetworkServiceReceiver` (opt-in marker interface) — `ISimulationCallbacks` implementations declare this when they need the `IKlothoNetworkService` handle on host/guest entry. `KlothoSessionFlow.FireOnSessionCreated` dispatches it right after `OnSessionCreated` and before `InitialPlayerConfigFactory`, gated on session kind (Host/Guest) + non-null callbacks + `session.SimulationCallbacks is INetworkServiceReceiver recv`. Brawler `BrawlerGameController.OnHostOrGuestSessionCreated` no longer hand-calls `SetNetworkService`; `BrawlerSimulationCallbacks` drops its empty-body `SetNetworkService(IKlothoNetworkService _)`.
- IMP-46 B: `IKlothoEngine.IssueOnce(Func<ICommand> commandFactory, ReliabilityPolicy policy = null)` → `IReliableCommandHandle` — duplicate / past-tick reject escalation + retry-interval cooldown + empty-move collision avoidance absorbed into the framework `ReliableCommandTracker`. `ReliabilityPolicy.Default` (RetryIntervalTicks=20 / ExtraDelayStep=4 / ExtraDelayMax=40 / TreatDuplicateAsAck=true / TreatPastTickAsEscalation=true) matches Brawler's prior spawn invariant. `OnResyncCompleted` resets every outstanding handle's `LastAttemptTick=-1` automatically. FaultInjection `DropSpawnCommandPlayerIds` / `ForceSpawnRetryPlayerIds` hooks preserved inside the tracker (no game-side branching). Brawler `BrawlerSimulationCallbacks` spawn-retry boilerplate (~100 LOC) absorbed.
- IMP-46 C: `[KlothoDataAsset(typeId, AssetId = ..., Key = ...)]` named-arg + `IDataAssetRegistry.Get<T>()` / `TryGet<T>(out T)` / `GetByKey<T>(string)` / `TryGetByKey<T>(string, out T)` overloads — attribute `AssetId` auto-resolved + `(Type, string)` tuple key index. The generator (`DataAssetEmitter`) auto-emits `private readonly int _assetId` + `public int AssetId => _assetId` + `ctor(int)` + (single-instance assets only) parameterless `ctor() : this(AssetIdFromAttribute)`. `DataAssetMissingConstructor` rule disabled (the analyzer no longer raises it — the descriptor type itself remains in `DiagnosticDescriptors.cs` for source-level compatibility, but has zero callsites; full removal deferred to a separate cleanup); new `DA006 DataAssetMixedUserGenerated` diagnostic added in its place. `DataAssetContractResolver` uses `OverrideCreator` + `CreatorParameters.Clear/Add` to make Newtonsoft's ctor-branch behaviour explicit. Brawler 9 DataAsset ctor + `AssetId` getter boilerplate removed; 22 callsites across 6 assets migrated from magic-id `Get<T>(N)` to the parameterless `Get<T>()` overload.
- IMP-46 D: `IKlothoSession` exposes 11 new members — 5 convenience methods (`HostGame` / `JoinGame` / `LeaveRoom` / `SendPlayerConfig` / `SetReady`), 4 state events (`StateChanged` / `PhaseChanged` / `PlayerCountChanged` / `AllPlayersReadyChanged`), and 2 properties (`PlayerCount` / `IsStopped`) — game code now depends on the interface, not the concrete class. `IKlothoEngine` exposes `PredictedFrame` / `RenderClock` / `OnEventPredicted` / `OnEventConfirmed` / `OnEventCanceled` / `Logger` (previously concrete-only). `IKlothoSession.Engine` returns `IKlothoEngine` via explicit interface implementation (the concrete `KlothoSession.Engine` getter still returns `KlothoEngine` for source-level backwards compatibility) — game code that depends on `IKlothoSession` now gets `IKlothoEngine` directly without a cast. Brawler `BrawlerSimulationCallbacks` / `BrawlerViewSync` / `GameHUD` / `BotFSMHelper` can drop the `ILogger` ctor param (escape hatch — direct `KlothoLogger.CreateDefault` storage — retained).
- IMP-46 E: `EntityView` transform pipeline integrated at frame-time — the lerp + `ApplyTransform` + `UpdatePositionParameter` populate that lived in `InternalUpdateView` moves into `InternalLateUpdateView`, fused with the `_errorVisual.Tick` call so the read-before-tick order is preserved. In tick-rate < frame-rate environments this reflects every per-frame `PredictedAlpha` change in the transform and avoids stale-lerp stutter. `UpdatePositionParameter` zeros `ErrorVisualVector` / `ErrorVisualQuaternion` when `EnableSnapshotInterpolation` is set, so the verified-frame interpolation path doesn't double-correct the rollback delta. `EngineEventOneShot.Subscribe<TEvent>(engine, filter, onPlay, onCancel, lateGuard) → EngineEventSubscription` (sealed `IDisposable`) — Predicted+Confirmed dispatch `onPlay` when filter+lateGuard pass; Canceled dispatches `onCancel` when filter passes. Brawler `CharacterView` drops the no-op `ApplyTransform` override + `LateUpdate` + `SyncFromEntity` (144 → 102 LOC). `CharacterAnimatorViewComponent` / `CharacterActionVfxViewComponent` collapse their 3-way subscribe + `HandlePlay` 4-method pattern into a single `EngineEventOneShot.Subscribe` call. Four character prefabs (Warrior/Mage/Rogue/Knight) null out `_interpolationTarget` to preserve root-direct smoothing.
- IMP-46 F: `KlothoGenerator` auto-emits a `MessageTypeId` override for `[KlothoSerializable(MessageTypeId = (NetworkMessageType)N)]` when `N >= NetworkMessageType.UserDefined_Start` (200), even for raw-cast values outside the enum (`(NetworkMessageType){rawValue}`). Sub-200 enum-external values keep the prior behaviour (silent drop) so `(NetworkMessageType)0` default-value regressions stay blocked. `SerializableTypeInfo.MessageTypeRawValue (int?)` field added; `FactoryEmitter` includes raw-value messages in `MessageRegistry.Register`. The three id-bearing attributes — `[KlothoComponent]` / `[KlothoSerializable]` / `[KlothoDataAsset]` — gain XML doc-comments stating their id planes are mutually independent. Brawler `BrawlerReplayConfig` drops its hand-rolled `MessageTypeId` override + 4-line justification comment. `BrawlerPlayerConfig`'s generated `MessageTypeId` switches from `NetworkMessageType.UserDefined_Start` to `(NetworkMessageType)200` (literal-faithful). Wire bytes 200/201 unchanged — existing replay files remain compatible.
- IMP-46 G: `EcsSimulation.GetSystem<T>()` / `TryGetSystem<T>(out T)` / `GetSystems<T>(List<T> buffer)` added — type-match lookup over registered systems (T : class). `SystemRunner.Find<T>` / `FindAll<T>` are the internal helpers. Brawler `BrawlerSimSetup.PhysicsSystem` static property + 8 callsites demoted to a local `var physicsSystem`. `BrawlerSimulationCallbacks` stashes `_simulation` on `RegisterSystems` entry and resolves `PhysicsProvider` via `_simulation?.GetSystem<PhysicsSystem>()`. `TrapTriggerSyncTests` 3 callsites migrate from `BrawlerSimSetup.PhysicsSystem` to `sim.GetSystem<PhysicsSystem>()`.
- Fix (Reconnect): cold-start Reconnect broken on app relaunch — `LeaveRoom` unconditionally cleared the credentials store, and the normal `OnApplicationQuit` → `TeardownAll` → `Session.Stop` → `LeaveRoom` chain wiped persisted credentials on every quit. Added optional `bool keepReconnectCredentials = false` to `KlothoSession.Stop` / `KlothoSessionDriver.DetachAndStop` / `IKlothoNetworkService.LeaveRoom`; process-exit entry points (`Driver.OnDestroy`, `BrawlerGameController.TeardownAll`) pass `true`. Explicit cancel / reject paths clear credentials directly — unchanged.

- Sample (Brawler): `BrawlerSimulationCallbacks.cs` 330 → 250 LOC (spawn-retry boilerplate absorbed — meets the umbrella §9 ≤ 250 LOC target). `CharacterView.cs` 144 → 102 LOC (transform pipeline branching absorbed — 2 lines over the umbrella §9 ≤ 100 LOC target; the remainder is game-data cache + Renderer/Shield/Boost feedback toggling, outside the lift scope).

- Docs: `GameDevAPI.md` / `GameDevWorkflow.md` / `Specification.md` / `FEATURES.md` / `Brawler.md` / `Brawler.C.DataAssets.md` / `Brawler.B.Systems.md` / `Brawler.E.Bootstrap.md` updated for IssueOnce / ReliabilityPolicy / `IDataAssetRegistry.Get<T>()` 1-shot lookup / `IKlothoSession` 11-member surface / `EngineEventOneShot.Subscribe` / `EntityView` standard transform pipeline / `EcsSimulation.GetSystem<T>()` / `INetworkServiceReceiver` opt-in / attribute ID plane invariant.

## [0.2.2] - 2026-05-24

- IMP-45 A: `IKlothoModeStrategy` + `KlothoModeStrategy.Resolve(simCfg)` — per-mode dispatcher (P2P / ServerDriven 2 impls). Absorbs `_simulationConfig.Mode != ...` if-chain previously scattered across game-side `Start` / `OnBtnHost` / `OnBtnGuest` / `StartHost` / `JoinGameAsync` 5+ call sites.
- IMP-45 A: `KlothoSessionFlowAsync.JoinP2PAsync(transport, host, port, sessionCfg, ct)` / `JoinServerDrivenAsync(transport, host, port, roomId, sessionCfg, ct)` — mode-split join entry points. Old `JoinAsync(transport, host, port, preJoin, roomId, sessionCfg, ct)` marked `[Obsolete]` (forwarding shim retained; removal target IMP46 / 0.3.0).
- IMP-45 A: `KlothoSessionFlow.OnHostSessionCreated` / `OnGuestSessionCreated` / `OnReplaySessionCreated` / `OnSpectatorSessionCreated` — 4 mode-dispatched callbacks alongside the existing `OnSessionCreated`. Absorbs the `engine.IsReplayMode` / `engine.IsSpectatorMode` 2-flag mode-by-flag branching in game-side `OnFlowSessionCreated`.
- IMP-45 B: `KlothoSessionDriver.IsStopping` — external read of the in-flight teardown guard. Game side replaces its own `_isStopping` / `_teardownInvoked` duplicate flags with `if (_sessionDriver.IsStopping) return;`. `IKlothoSessionObserver.OnSessionStopped` invariant documented for the 2 entry paths (Driver.DetachAndStop / Session.Stop direct).
- IMP-45 C: `KlothoFlowSetup.InitialPlayerConfigFactory : Func<PlayerConfigBase>` — auto `SendPlayerConfig` on guest / reconnect paths (skipped on spectator / replay). Game-side `session.SendPlayerConfig(...)` 3-site duplicate (StartHost / JoinGameAsync / ReconnectAsync) removed.
- IMP-45 D: `KlothoSessionFlow.StartReplayFromFile(string path)` — 1-call file-to-session entry (absorbs `ReplaySystem.LoadFromFile` + `simConfig.Validate()` + `StartReplay`). Throws `xpTURN.Klotho.Replay.ReplayLoadException` on load failure.
- IMP-45 D: `KlothoFlowSetup.SpectatorTransportFactory : Func<INetworkTransport>` + `KlothoSessionFlowAsync.SpectateAsync(host, port, roomId, ct)` no-transport overload — library instantiates the spectator transport via the factory. Game-side `new LiteNetLibTransport(...)` inline removed from `StartSpectatorAsync`.
- IMP-45 D (breaking): `xpTURN.Klotho.Replay.ReplaySystem.LoadFromFile` signature changed — `bool LoadFromFile(string, out IReplayData)` → `void LoadFromFile(string)`; on success the loaded data is exposed via `CurrentReplayData`, on failure the method throws `xpTURN.Klotho.Replay.ReplayLoadException`. External callers should migrate to `flow.StartReplayFromFile(path)`; `flow.StartReplay(IReplayData, ISimulationConfig)` retained as the escape hatch for custom `IReplayData` sources.
- IMP-45 E: `KlothoSession.StateChanged` / `PhaseChanged` / `PlayerCountChanged` / `AllPlayersReadyChanged` — state-change events (replacing per-frame `UpdateStatus` 7-field polling). Backed by `KlothoEngine.OnStateChanged` + `IKlothoNetworkService.OnPhaseChanged` / `OnPlayerCountChanged` / `OnAllPlayersReadyChanged` on the service side.
- IMP-45 F: `FaultInjectionRuntime.AttachToSession` / `FaultInjectionLoader.TryLoadAndApply` / `FaultInjection` static collections — callable without `#if KLOTHO_FAULT_INJECTION` guard. Undefined builds return null / false / empty stub (library-internal reader bodies and `FaultInjection.Reset()` body retain their macro guards — release cost stays at zero). Brawler game-side 4 `#if` guards (+ 2 BrawlerSimulationCallbacks guards) removed.
- IMP-45 G: `LateJoinNotificationMessage` (NetworkMessageType=75) — host (P2P) / server (SD) broadcast to existing peers on mid-match late-join so they update `OnPlayerJoined` / `PlayerCount` for the joiner. Guest PlayerInfo baseline aligned (P2P `""` / SD `$"Player{id}"`); forged-sender guards (`P2P: !IsHost return`, `SD: peerId != 0 return`) + idempotency.
- IMP-45 H: Spectator player list surface — `ISpectatorService.PlayerCount` + `event OnPlayerCountChanged`; P2P/SD host LateJoin broadcast extended to `_spectators`; spectator-side `LateJoinNotificationMessage` receive handler; `KlothoSession.PlayerCount` unified getter (`NetworkService → SpectatorService → 0` fallback) + `KlothoSession.SubscribeStateForwarders` wires `_spectatorService.OnPlayerCountChanged → RaisePlayerCountChanged`.
- Sample (Brawler): `BrawlerGameController.cs` 886 → 858 LOC. Mode guards / teardown guards / PlayerConfig sends / Replay & Spectator entry points / per-frame Phase polling / FaultInjection `#if` guards all absorbed into the library. (The umbrella plan's `< 600 LOC` target was not met within IMP45 scope — remaining lines are core game logic [BrawlerSettings wiring, GameMenu state machine, data loading]; further reduction deferred to IMP46+.)
- IMP-43: `ISessionConfig` / `ISimulationConfig` realignment — 12 engine-internal fields (Reactive 6, Rollback 2, Resync 3, `CatchupMaxTicksPerFrame`) moved Session → Sim; 3 service-side fields (`LateJoinDelaySafety`, `RttSanityMaxMs`, `MinStallAbortTicks`) moved Sim → Session. Wire schema breakage in 5 messages — server/client must roll out together.
- IMP-43: `USessionConfig` ScriptableObject — Inspector-editable 16-field session config; `KlothoSessionSetup` mirror fields (11 props) removed in favor of a single `SessionConfig` reference. Brawler: `BrawlerSettings._maxPlayers` removed, `_sessionConfig` is the single SoT.
- IMP-44 A: `KlothoSessionDriver` (Runtime.Unity) — MonoBehaviour Update/Stop adapter with `PreSessionUpdate` / `PostSessionUpdate` / `Stopping` / `IdlePoll` hooks.
- IMP-44 B: `FaultInjectionRuntime` + `FaultInjectionHotkeyDriver` (Runtime.Unity, `#if KLOTHO_FAULT_INJECTION`) — RTT-spike / Disconnect schedule + F12 chain-stall hotkey lifted from sample.
- IMP-44 C: `KlothoSessionFlow` (Runtime.Core) + `KlothoSessionFlowAsync` (Runtime.Unity) — 5-entry-point builder (`StartHost` / `JoinAsync` / `ReconnectAsync` / `SpectateAsync` / `StartReplay`) with single `OnSessionCreated` event. Absorbs `KlothoSession.Create` / `KlothoSession.CreateSpectator` / `KlothoConnectionAsync` calls. `KlothoSpectatorAsync` deleted (BC break).
- IMP-44 D: Bootstrap helpers (Runtime.Unity) — `KlothoAutoReconnect.TryStart`, `KlothoLogger.CreateDefault`, `ReconnectRejectReason.ToDefaultMessage`.
- IMP-44 D.3: EditorBridge auto-wire — `INavMeshProvider` / `INavAgentProvider` / `IFPPhysicsProviderSource` optional `ISimulationCallbacks` interfaces; sample-side `ConnectPhysicsProvider` + `EcsDebugBridge.Register*` calls removed.
- IMP-44 hotfix bundle: StartReplay `_isStopping` reset; cold-start `ApplyFaultInjection` placement; spectator FI scope guard via `IsSpectatorMode`; `_simulationConfig.Mode` ScriptableObject mutation removed (Mode != P2P rejected with logged error); `OnDestroy` / `OnApplicationQuit` consolidated into single-shot `TeardownAll`; spectator detection via canonical `session.Engine.IsSpectatorMode`; `StopGame` `try/finally` + 5 entry-point reset removed; `OnBtnHost` / `OnBtnGuest` P2P-only guard.
- Framework: `UKlothoBehaviour` removed (unused adapter).
- Sample: `ResultScreen._panel` reset on next session start (prevents stale result screen on subsequent matches).

- Docs: `FEATURES.md` / `GameDevAPI.md` / `GameDevWorkflow.md` / `Specification.md` / `Brawler.md` / `Brawler.E.Bootstrap.md` / `README.md` updated for the new Mode strategy / split-Join entry points / 4-dispatch callbacks / Driver.IsStopping / `KlothoFlowSetup` factories / Session state events / FaultInjection macro-agnostic surface / LateJoin propagation / spectator player list.

## [0.2.0] - 2026-05-21

- IMP-41: Singleton component first-class support — `[KlothoSingletonComponent]` + `Frame.GetSingleton<T>` / `GetReadOnlySingleton<T>` / `TryGetSingleton<T>`; `Frame.Add` enforces one-carrier-per-frame via `ComponentStorageRegistry.TypeIdCache<T>.IsSingleton`.
- IMP-41: Built-in `RandomSeedComponent` (singleton, engine-injected at `Start`; restored via FullState on LateJoin / Reconnect / Spectator / Replay) replaces sample-side `GameSeedComponent`; Brawler `GameTimerStateComponent` migrated to singleton.
- IMP-41: `TransformComponent` marker-based prev-snapshot redesign — `PreviousInitialized` marker drives the `Frame.Add` auto-init hook and PreUpdate `SavePrev` pass; `Frame.RefreshPreviousTransform(entity)` covers post-Add ref-set paths. Sample-side `SavePreviousTransformSystem` removed.
- IMP-41 A-1: `KlothoConnectionAsync` lifted to `Assets/Klotho/Unity/` (Runtime.Unity); A-2: EVU `BindBehaviour` / `ViewFlags` 5-flag decision lifted to `EntityViewFactory` base; A-3: Replay `InitialStateSnapshot` auto-inject lifted to `Engine.StartReplay`; A-4: `IReconnectCredentialsStore` injected via `KlothoSessionSetup.CredentialsStore` (+ `AppVersion` / `DeviceIdProvider`); A-5: client-reactive PastTick / rollback-burst escalation lifted to `DynamicInputDelayPolicy` (thresholds in `SessionConfig`); A-6: `EndGracePolicy.Pause` `StopCommand` auto-injected by engine.
- IMP-41 B-1: `IKlothoSessionObserver` bulk-subscribed lifecycle (replaces per-event `+=` wiring across StartHost / JoinGame / Reconnect / StopGame); adds `OnSessionStopped`. B-2: `OnReconnectFailed(byte)` carries `ReconnectRejectReason` enum directly + `ReconnectFailedException.Reason` for cold-start paths. B-3: `KlothoSession.CreateSpectator(SpectatorSessionSetup)` + `KlothoSpectatorAsync.CreateAsync` — framework owns SpectatorService / two-config await / Engine+Simulation construction; game supplies `CallbacksFactory(simCfg, sessionCfg)`. B-4: `ClientShutdownGraceMs` scheduler routed through `KlothoSession.Update`. B-5: `PlayerViewRegistry<TView>` built into `EntityViewUpdater` (driven by `OwnerComponent` add/remove; events for local-view subscription).
- IMP-41: P2P spectator entry fix — unified spectator broadcast condition + `SpectatorAccept.LastVerifiedTick` derivation + `_lastVerifiedTick` default correction. Spectator bootstrap tick-race fix (framework-side gate) + `ItemSpawnSystem` defensive guard for unwritten singleton seed.
- IMP-42: Spectator EC activation — removed `_isSpectatorMode` guards on `CapturePreRollback` / `ComputeErrorDeltas` / Predict-under-Predicted; `HandleSpectatorUpdate` wires the EC pair only when a verified batch arrives.
- Framework: re-simulation invariant diagnostic guard extended to `DEVELOPMENT_BUILD` (was Editor-only). Pause-grace `StopCommand` auto-inject overwrite blocked + regression test. Brawler sample-system sync tests fixed for the new singleton-entity setup.
- Docs: `FEATURES.md` / `GameDevAPI.md` / `Specification.md` / `Brawler.md` / `Brawler.B.Systems.md` / `Brawler.E.Bootstrap.md` updated for the above lifts (singleton API, `IKlothoSessionObserver`, spectator path, `DynamicInputDelayPolicy`, `OnReconnectFailed(byte)`, `RandomSeedComponent` adoption, sample-side `SavePreviousTransformSystem` removal).

## [0.1.8] - 2026-05-18

- Updated LiteNetLib to [2.1.3](https://github.com/RevenantX/LiteNetLib/releases/tag/2.1.3); renamed `ConnectionRequest` → `LiteConnectionRequest` in `LiteNetLibTransport.OnConnectionRequest` to match the new interface signature.


## [0.1.7] - 2026-05-18

- IMP-40 (`KLOTHO_FAULT_INJECTION`-only): SD RTT-spike desync fix — per-peer FIFO clamp in `LiteNetLibTransport._delayedRecvMessages` resolves the fault-injection reorder bug (production transport unaffected).
- IMP-40: `ProcessVerifiedBatchCore` batch-start gap fill refactored to use `SimulateGapTickWithEmptyFallback` helper, closing a latent bug where missing players were not substituted with `EmptyCommand` (separate from the fault-injection fix above; surfaces in high-RTT predict-snapshot rollback paths).
- IMP-40: SD desync runtime alarm — `[SD][ResimGap]` `ZLogWarning` emits when a verified batch leaves resim behind `entry.Tick`; complemented by dev-only diagnostics (`[InputCollector][Reject]`/`[EmptySubst]`, `[SD][PredSource]`, `[CompHash][History]`). `ServerInit` component hash dump promoted to `ZLogInformation` so release builds still surface it.
- IMP-39: All-participants-spawned gate — introduced `SessionParticipantComponent`; engine writes deterministic participant slots into the frame at `Start()`.
- IMP-39: Normal-end lifecycle — `IMatchEndEvent`/`EndReason` + `OnMatchEnded` channel, grace-driven `Room` drain, wired into `BrawlerGameController`/`GameOverSystem`.
- IMP-39: Client match-end reaction — prediction-freeze path + `ClientShutdownGraceMs` (SessionConfig).
- IMP-39: `StopCommand`-based pause behavior — SD/P2P unified, handled in `PlatformerCommandSystem`.
- IMP-39: `SessionConfig` propagation across 17 fields (6 previously missing + Normal-join bug fix), aligned `GameStartMessage`/`LateJoinAcceptMessage`/`ReconnectAcceptMessage`.

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
