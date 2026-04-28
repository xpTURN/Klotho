# Brawler Appendix I — How to Run

> Related: [Brawler.md](Brawler.md) · [Brawler.E.Bootstrap.md](Brawler.E.Bootstrap.md) (controller flow) · [Brawler.H.DedicatedServer.md](Brawler.H.DedicatedServer.md) (headless server)
> Scope: step-by-step guide to running the Brawler sample in each of the three supported network configurations
>
> - **P2P** — host + guest peers, no dedicated server
> - **ServerDriven · single-room** — one headless server hosting a single match
> - **ServerDriven · multi-room** — one headless server hosting up to N rooms in parallel

---

## I-1. Common setup

Steps that apply regardless of mode.

### I-1-1. Open the sample

1. Open the `Klotho/` project in Unity 6.3
2. Open the scene: `Assets/Klotho/Samples/Brawler/Scenes/BrawlerScene.unity`
3. In the scene, confirm `BrawlerGameController` Inspector slots are bound:
   - **Simulation Config** — `Assets/Klotho/Samples/Brawler/Config/SimulationConfig.asset`
   - **Static Colliders Asset** — `Data/BrawlerScene.StaticColliders.bytes`
   - **Nav Mesh Asset** — `Data/BrawlerScene.NavMeshData.bytes`
   - **Data Asset** — `Data/BrawlerAssets.bytes`
   - **Game Menu / View Sync / Entity View Updater** — wired to scene objects

If any `.bytes` slot is empty, regenerate it via the editor menus listed in [Brawler.H.DedicatedServer.md §H-2-2](Brawler.H.DedicatedServer.md).

| File | Editor menu |
|---|---|
| `BrawlerScene.StaticColliders.bytes` | `Tools > Klotho > Export Static Colliders` |
| `BrawlerScene.NavMeshData.bytes` | `Tools > Klotho > Export NavMesh` |
| `BrawlerAssets.bytes` | `Tools > Klotho > Convert > DataAsset JsonToBytes` |

### I-1-2. BrawlerSettings quick reference

`BrawlerGameController._brawlerSettings` (Inspector) drives every runtime option.

| Field | Type | Description |
|---|---|---|
| `_mode` | `NetworkMode` | `P2P` or `ServerDriven` |
| `_hostAddress` | string | IP / hostname to host on or connect to |
| `_port` | int | Default `777` |
| `_roomId` | int | SD multi-room only (`-1` = single-room; ignored in P2P) |
| `_isHost` | bool | P2P only — toggled at runtime by the **Host / Guest** buttons |
| `_maxPlayers` | int | P2P host capacity |
| `_botCount` | int | Bots spawned by the host (P2P) or server (SD) |
| `_characterClass` | int | `0=Warrior, 1=Mage, 2=Rogue, 3=Knight` |

### I-1-3. GameMenu buttons

| Button | Role |
|---|---|
| **Host** | Switch to host mode (P2P only meaningful) — relabels to **Create Room** |
| **Guest** | Switch to guest mode — relabels to **Join Room** |
| **Action** (label changes with state) | Drives the lifecycle: `Create Room` / `Join Room` → `Ready` → `Stop` |
| **Replay** | Plays back `Replays/brawler.rply` (auto-saved on **Stop**) |
| **Spectator** | Connects as spectator to `_hostAddress:_port` |

The IP input field is bound to `_brawlerSettings._hostAddress`, so edits take effect immediately.

### I-1-4. Player lifecycle

```
Create Room / Join Room  ──▶  Ready  ──▶  Playing  ──▶  Stop
       (Action)              (Action)    (Action)     (Action)
```

- **Ready** — calls `KlothoSession.SetReady(true)`. Once `AllPlayersReady` is true and `MinPlayers` is satisfied, the host/server starts the match.
- **Stop** — ends the session. The host's last match is saved to `Replays/brawler.rply` for later **Replay**.

---

## I-2. P2P mode

In P2P, one peer is the host (authoritative) and the rest are guests. No dedicated server.

### I-2-1. Host (Player 1)

1. Inspector → `BrawlerSettings`:
   - `_mode = P2P`
   - `_hostAddress = localhost` (listen on all interfaces) or LAN IP
   - `_port = 777`
   - `_maxPlayers` = total players (e.g. `2`)
   - `_botCount` = bots to fill (optional — `_maxPlayers + _botCount` must not exceed the room entity budget)
   - `_characterClass` = class to use
2. Press Play
3. In the **Game Menu**, click **Host** (label flips to **Create Room**)
4. Click **Create Room** → label becomes **Ready**
5. Wait for guests (`Players: N` should update), then click **Ready**

### I-2-2. Guest (Player 2+)

1. Inspector → `BrawlerSettings`:
   - `_mode = P2P`
   - `_hostAddress` = host IP (`localhost` if same machine, LAN IP otherwise)
   - `_port = 777`
   - `_characterClass` = class to use
2. Press Play
3. Click **Guest** (label flips to **Join Room**)
4. Click **Join Room** → handshake runs; on success the label becomes **Ready**
5. Click **Ready**

### I-2-3. Same-machine testing

To run host and guest on the same Mac:

- Make a standalone (Player) build for the guest and run it while the editor hosts (or the reverse)

Use `localhost` for the guest's `_hostAddress`.

### I-2-4. Notes

- The P2P host is **not** a cold-start reconnect target — if the host quits, the match ends for all peers. Guests can auto-reconnect to a live host (see `PlayerPrefsReconnectCredentialsStore`).
- `BrawlerGameController.StartHost()` overrides `SimulationConfig.Mode = NetworkMode.P2P` regardless of the asset's value.
- `_roomId` is forced to `-1` in `Start()` whenever `_mode != ServerDriven`.

---

## I-3. ServerDriven · single-room

A single headless server runs the simulation; clients only send input commands.

### I-3-1. Start the headless server

```bash
cd Klotho/Tools/BrawlerDedicatedServer
./build.sh                                         # dotnet build -c Debug

# Run: <port> <maxPlayers> <botCount> [logLevel]
dotnet run --project BrawlerDedicatedServer.csproj -- 7777 4 0 Information
```

The server prints a banner with the bound endpoint once it is ready to accept clients. See [Brawler.H.DedicatedServer.md §H-5](Brawler.H.DedicatedServer.md#h-5-single-room-mode-phase-1) for bootstrap details.

> The server does not need Unity at runtime, but the three `.bytes` files under `Tools/BrawlerDedicatedServer/Data/` must have been exported once from the editor (see §I-1-1).

### I-3-2. Client (every player)

1. Inspector → `BrawlerSettings`:
   - `_mode = ServerDriven`
   - `_hostAddress` = server IP
   - `_port` = server port (`7777` per §I-3-1)
   - `_roomId = -1` (single-room)
   - `_characterClass` = class to use
2. Press Play
3. Click **Guest** (the **Host** button has no meaning for SD clients — every client is a guest)
4. Click **Join Room** → handshake runs; on success the label becomes **Ready**
5. Click **Ready**

Once `MinPlayers` (`sessionconfig.json`, default `2`) are connected and ready, the server starts the match.

### I-3-3. Server tuning

Edit these files in the server's working directory or build-output directory (`AppContext.BaseDirectory`):

| File | Notable options |
|---|---|
| `simulationconfig.json` | `TickIntervalMs`, `SDInputLeadTicks`, `SyncCheckInterval`, `EnableErrorCorrection` |
| `sessionconfig.json` | `MinPlayers`, `AllowLateJoin`, `ReconnectTimeoutMs`, `ReconnectMaxRetries` |

Restart the server to pick up changes. The server propagates `simulationconfig.json` to clients via `SimulationConfigMessage`, so a client's local `USimulationConfig` **cannot** override the server's tickrate.

---

## I-4. ServerDriven · multi-room

A single server process hosts up to `maxRooms` matches concurrently on one shared port.

### I-4-1. Start the multi-room server

```bash
cd Klotho/Tools/BrawlerDedicatedServer

# --multi <port> <maxRooms> <maxPlayersPerRoom> <botCount> [logLevel]
dotnet run --project BrawlerDedicatedServer.csproj -- --multi 7777 4 2 0 Information
```

Listens on `0.0.0.0:7777`, hosts up to `4` rooms of `2` players each. See [Brawler.H.DedicatedServer.md §H-6](Brawler.H.DedicatedServer.md#h-6-multi-room-mode-phase-2) for routing details.

### I-4-2. Client — choosing a room

The Brawler client identifies the target room via `BrawlerSettings._roomId`, sent as `RoomHandshakeMessage` before `PlayerJoinMessage` (see `BrawlerGameController.JoinGameAsync`).

1. Inspector → `BrawlerSettings`:
   - `_mode = ServerDriven`
   - `_hostAddress` = server IP
   - `_port` = server port
   - `_roomId` = room to join (e.g. `0`, `1`, `2`, `3` for `--multi ... 4 ...`)
   - `_characterClass` = class to use
2. Press Play
3. Click **Guest** → **Join Room**
4. Click **Ready**

Each room is fully isolated. Messages do not cross between rooms (`RoomManager` + `RoomRouter`). Over-capacity → connection rejected; non-existent `_roomId` → connection rejected.

### I-4-3. Matching players into the same room

The sample has no matchmaker — clients pick a room directly via `_roomId`. To play together, all players must use the **same** `_roomId`. Simplest workflow:

- Agree that room 0 is "table 1", room 1 is "table 2", etc.
- Each client sets the chosen `_roomId` before pressing Play

`MinPlayers` is enforced per room. The server starts a room as soon as that room reaches `MinPlayers` ready peers.

### I-4-4. Spectating a specific room

The **Spectator** button connects with `BrawlerSettings._roomId`, so set the room ID before clicking. The spectator path defers `Engine` creation until both `SimulationConfig` and `SessionConfig` arrive from the server, so spectators run with the room's authoritative settings.

---

## I-5. Replay playback

The host (P2P) or each client (SD) auto-saves a replay to `Replays/brawler.rply` when the match ends via **Stop**.

1. Press Play
2. With **Phase = None** or **Disconnected**, click **Replay**
3. The session restarts in replay mode; the **Action** label becomes **Stop**

Replay playback does not record commands, so **the file is not overwritten while replaying** (see `BrawlerGameController.StopGame` and `engine.IsReplayMode`). To preserve a replay, copy `Replays/brawler.rply` aside before starting a new match.

---

## I-6. Reconnect behavior

`BrawlerGameController.Start()` attempts auto-reconnect on boot if prior credentials exist (`PlayerPrefsReconnectCredentialsStore`).

- **P2P host** — never auto-reconnects (host is the source of truth; if it dies, the match is gone).
- **P2P guest / SD client** — eligible for cold-start reconnect. The **Action** button shows **Cancel** while reconnecting.

Credentials are cleared on:

- **Cancel** during reconnect
- A hard rejection (`InvalidMagic`, `InvalidPlayer`, `TimedOut`, `AlreadyConnected` — see `HandleReconnectFailure`)

---

## I-7. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `Players: 1` never increases | Firewall blocking the UDP port | Open `_port` (default 777 for P2P, 7777 for SD) on the host firewall |
| Clients hang on `Connecting...` then drop back to **Join Room** | Wrong `_hostAddress` / `_port`, or server not running | Verify the server log shows it listening on the same address |
| Match never starts after **Ready** | `MinPlayers` not satisfied | Lower `MinPlayers` in `sessionconfig.json` (SD) or add players (P2P) |
| `Reconnect rejected: AlreadyConnected` | Same `PlayerId` already connected from another device | Quit the other client; credentials are auto-cleared after a hard reject |
| Multi-room join rejected | `_roomId` invalid (over-capacity or not active) | Check the server log for `RoomReject` and pick another room |
| Editor freezes on Play | Bot count too high vs `MaxEntities` | Reduce `_botCount` or raise `MaxEntities` in `simulationconfig.json` |
