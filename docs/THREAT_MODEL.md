# Threat model: untrusted WebAssembly guests on a 7 Days to Die dedicated server

Systemic view of the attack surface this repository creates: what can be
attacked, across which boundary, what it costs, and which mitigations the
code actually implements. `SECURITY.md` keeps the operator-facing guarantee
table; this document is the model behind it. Point vulnerabilities and their
fixes are not recorded here, only the threat and where it lives.

Ranking: **High** = reachable by anyone who can drop a file on the server or
open the console, and the damage reaches beyond this mod. **Medium** = needs
a guest, an operator mistake, or a game-side dependency. **Low** = bounded,
detectable, or only reachable by someone who already has the game process.

Every entry carries a `path:line` reference so the next pass can re-verify it
against the code instead of against this document.

## 1. Risk-ranked summary

| # | Threat | Boundary | Rank | State |
|---|---|---|---|---|
| T1 | Any console operator can load arbitrary guest code from disk into the live game process | operator to host | High | Unmitigated here; gated only by the game's console auth |
| T2 | A loaded guest makes the bot servant deal damage to any living entity, players included | guest to game | High | Shooter is ownership-gated, target is not |
| T3 | A guest arms or clears a glide flag on a player the guest does not own | guest to game | High | Target class gated to players; the flag belongs to the module that holds it |
| T4 | Guest-controlled volume (module count x fuel per tick) is unbounded by module count, and a module's own manifest may raise its fuel ceiling | guest to availability | High | Fuel per call, rate caps, telemetry; no module-count cap |
| T5 | An installed modlet, not the operator, can supply the module tree and the shared `wasm.toml` that sets the engine's limits | build to runtime | High | Unmitigated; every staged `Wasm/` folder is scanned |
| T6 | The game process is the sandbox host, and a bridge bug runs with game privileges | bridge to game | High | Out of scope by design, stated in `SECURITY.md` |
| T7 | Wasmtime engine version lags the published advisories | dependency | Medium | Tracked in `SECURITY.md`, recheck needed |
| T8 | Cross-guest information disclosure: world entity snapshot, player names, shared settings | guest to guest | Medium | Unmitigated, treated as intended sharing |
| T9 | A committed telnet password in the playtest evidence hands the whole `wasm` surface to anyone who reads the repository | repository to console | Medium | Evidence of a past run, left intact |
| T10 | Telnet password in plaintext `serverconfig.xml`, reachable by any local reader | secrets | Medium | Documented rule, not code |
| T11 | Log, chat, and console output are attacker-shaped text | guest to log | Low | Mitigated (`TextSanitizer`, rate caps) |
| T12 | Process-wide `PATH` mutation for the native engine lookup | build to runtime | Low | Mitigated by a fixed staging path, still a plant surface |
| T13 | Wasmtime native library and guest modules ship unverified | build to runtime | Low | Unmitigated, no checksum or signature at load |

## 2. Attack surface inventory

### 2.1 Entry points present in code

| Entry point | Where | Reachable by |
|---|---|---|
| Mod entry `ModApi.InitMod` (mod load by the game) | `src/GameBridge/ModApi.cs:27` | the game mod loader, on dedicated servers only |
| Harmony patch application by name (`GameManager.Update`, `RequestToSpawnPlayer`) | `src/GameBridge/ModApi.cs:66` | the game assembly; a rename in a game update silently removes the patch |
| Console command `wasm list/load/reload/unload/status/help` | `src/GameBridge/Commands/CmdWasm.cs:26`, dispatched in `Execute` at `src/GameBridge/Commands/CmdWasm.cs:61` | telnet console and in-game console, whoever the game lets run console commands |
| Module tree scan at start and on `wasm load` | `src/GameBridge/Bridge/BridgeHost.cs:579` | filesystem under `Mods/Wasm` and every staged modlet's `Wasm/` |
| Extra root collection (every modlet under `Mods/` with a `Wasm/` folder) | `src/HordeForge.WasmHost/Registry/ModuleRoots.cs:53` | any installed modlet, not only the operator's own tree |
| Shared `wasm.toml` selection (top level, else the first modlet tree that has one) | `src/GameBridge/Bridge/BridgeHost.cs:181` | filesystem; sets the engine's fuel and memory ceilings |
| `module.wasm`, `wasm-mod.toml`, `config.toml` files | `src/GameBridge/Bridge/BridgeHost.cs:1082`, `src/HordeForge.WasmHost/Registry/ManifestFiles.cs:44` | filesystem |
| Mod id from console input | `src/GameBridge/Commands/CmdWasm.cs:90`, validated by `src/HordeForge.WasmHost/Registry/ModId.cs:52` and `src/HordeForge.WasmHost/Registry/ModuleRoots.cs:126` | console |
| Harmony postfix on `GameManager.Update` (tick) | `src/GameBridge/Hooks/GameTickHook.cs:14` | the game loop, 20 calls per second |
| Harmony postfix on `GameManager.RequestToSpawnPlayer` (join) | `src/GameBridge/Hooks/PlayerSpawnHook.cs:14` | a remote player joining or respawning |
| Player name from `ClientInfo` | `src/GameBridge/Bridge/BridgeHost.cs:370` | a remote player, through the join hook |
| Host imports: `log`, `tick`, `get_world_time`, `get_setting`, `send_chat`, `get_join_player_name` | `src/HordeForge.WasmHost/Core/WasmModHost.cs:661` (definitions at `:665` through `:820`) | every loaded guest |
| zdtd imports: `log`, `tick`, `config`, `queue`, `sense`, `query` | `src/HordeForge.WasmHost/Core/WasmModHost.cs:700` (definitions at `:709` through `:796`) | every loaded guest |
| Guest exports the host resolves: `on_enable`, `on_tick`, `on_player_join`, `shutdown`, `on_admin_command` | `src/HordeForge.WasmHost/Core/WasmModHost.cs:306` | every loaded guest, signature checked at load |
| Host-to-guest string writes (settings values, config bytes, sense snapshots, join name, query answers) | `src/HordeForge.WasmHost/Core/WasmModHost.cs:904` | every loaded guest |
| Guest-to-host string reads (bounds checked, traps out of range) | `src/HordeForge.WasmHost/Core/WasmModHost.cs:924` | every loaded guest |
| Native engine resolution: `LD_LIBRARY_PATH` / `DYLD_LIBRARY_PATH` / `PATH` | `src/GameBridge/Bridge/NativeBootstrap.cs:35`, `:48` | process start, operator |
| Game network port (players) and the telnet port | game server, configured in `serverconfig.xml`; loopback playtest binding in `evidence/playtest-1/run_server.sh:29` | network |
| Developer CLIs: `targetcheck [GAME_DIR]`, `apicheck.py`, `doccheck.py`, `sbom.py` | `tools/targetcheck/Program.cs:46`, `tools/apicheck.py`, `tools/doccheck.py`, `tools/sbom.py` | the developer running them, not shipped |

Servant verbs reachable from the `queue` import: `spawn`, `remove`, `count`,
`move`, `look`, `shoot`, `skill`, `cfg`, plus `glide`
(`src/GameBridge/Bridge/BotServant.cs:400`). The last three carry no
game-side effect: `skill` and `cfg` acknowledge and log the guest's own
policy text (`src/GameBridge/Bridge/BotServant.cs:426`), and `count` is
bounded by the same command limiter as the rest.

`on_admin_command` is a guest export the host resolves and signature checks
but nothing dispatches: no bridge path calls `HasAdminCommandHandler`
(`src/HordeForge.WasmHost/Core/WasmMod.cs:207`), as ADR 0007 records. It is
listed here so a later pass does not read "validated export" as "reachable
console hook"; wiring it would add a B2 surface that does not exist today.

`query` is the mirror case on the import side: it is defined and linked
(`src/HordeForge.WasmHost/Core/WasmModHost.cs:796`) and the bridge answers
`null` for every request (`src/GameBridge/Bridge/GameHostApi.cs:381`), so it
carries no data today.

### 2.2 SECURITY.md claims checked against the code

Every row of the "What the sandbox guarantees" table resolves:

- Fuel per call: engine built with fuel consumption on, budget re-armed
  before every call (`src/HordeForge.WasmHost/Core/WasmModHost.cs:124`,
  `src/HordeForge.WasmHost/Core/WasmMod.cs:285`).
- Memory maximum: engine static memory maximum, checked against the module's
  declared maximum at load, a module with no declared maximum treated as the
  wasm32 ceiling (`src/HordeForge.WasmHost/Core/WasmModHost.cs:120`,
  `:316`).
- Module size: file length checked before the read, then byte length in
  `LoadModule` (`src/GameBridge/Bridge/BridgeHost.cs:690`,
  `src/HordeForge.WasmHost/Core/WasmModHost.cs:271`).
- Stack: engine maximum stack size, validated non-zero
  (`src/HordeForge.WasmHost/Core/WasmModHost.cs:121`,
  `src/HordeForge.WasmHost/Config/WasmHostConfig.cs:62`).
- Chat cap: one global limiter at 10/second, shared by every module
  (`src/GameBridge/Bridge/GameHostApi.cs:60`, `:429`).
- Log cap: per-source limiter at 10 lines/second
  (`src/GameBridge/Bridge/GuestRateLimiter.cs:39`,
  `src/GameBridge/Bridge/GameHostApi.cs:59`).
- Trap isolation: every call returns a `ModRunResult` and the tick walk
  continues (`src/HordeForge.WasmHost/Core/WasmMod.cs:280`,
  `src/HordeForge.WasmHost/Core/WasmModHost.cs:524`).
- WASI: linked with no preopens, no environment, no stdin; standard streams
  inherited only on operator opt-in (`src/HordeForge.WasmHost/Core/WasmModHost.cs:124`,
  `:360`, `src/HordeForge.WasmHost/Config/WasmHostConfig.cs:53`).

Nothing in that table names a control the code lacks. The reverse holds: the
bot servant, the `sense` snapshot, the `glide` verb, the module-count limit,
and the modlet-carried module tree are real surfaces `SECURITY.md` does not
mention. Sections 3, 5, and 8 name them.

### 2.3 Inputs treated as trusted that arrive from outside the boundary

- Player names from `ClientInfo.playerName` are handed to every guest that
  exports `on_player_join` with no validation beyond a non-empty check
  (`src/GameBridge/Bridge/BridgeHost.cs:370`). The value is not written to
  the log by this path; the guests receive it raw through
  `get_join_player_name` (`src/HordeForge.WasmHost/Core/WasmModHost.cs:691`).
- `wasm-mod.toml` and `config.toml` are third-party content. The manifest is
  parsed by a hand-written TOML reader
  (`src/HordeForge.WasmHost/Registry/MiniToml.cs:29`); `config.toml` is never
  parsed by the host and is served verbatim to the guest that owns it
  (`src/GameBridge/Bridge/BridgeHost.cs:778`).
- A manifest may set `fuel_per_call` and `max_memory_bytes`. Both are
  bounded: fuel at or below 50,000,000 instructions
  (`src/HordeForge.WasmHost/Registry/ModManifest.cs:47`, `:197`) and memory
  only tightens (`src/HordeForge.WasmHost/Core/WasmModHost.cs:318`). Fuel can
  still be raised 50x over the 1,000,000 default by the module author, which
  is logged once at load (`src/GameBridge/Bridge/BridgeHost.cs:759`).
- A modlet-carried `wasm.toml` becomes the shared limits file when the
  top-level one is absent, so a third-party modlet can set the engine's fuel
  and memory ceilings the whole server runs under
  (`src/GameBridge/Bridge/BridgeHost.cs:181`,
  `src/GameBridge/Bridge/BridgeHost.cs:933`).
- The two zdtd guest modules the modlet ships are copied unmodified out of a
  sibling `zdtd-server` checkout (`ZDTD_SERVER` in the Makefile) with no
  checksum over either binary, so what a built modlet carries is whatever
  that local checkout holds. It is a build-time input, not a runtime one, and
  the NuGet closure beside it is hash-pinned; the two modules are inventoried
  in `dist/SBOM.json` and `THIRD-PARTY-NOTICES.md` by name and version, with
  no checksum, because this repository has nothing to check one against.

## 3. Trust boundaries

| Boundary | Inside | Outside | Crossing point |
|---|---|---|---|
| B1 player to game server | game process | remote player, no identity beyond the game session | join hook `src/GameBridge/Hooks/PlayerSpawnHook.cs:14` |
| B2 operator to host | bridge and host library | telnet console operator, filesystem writer | `src/GameBridge/Commands/CmdWasm.cs:61`, `src/GameBridge/Bridge/BridgeHost.cs:579` |
| B3 guest to host | Wasmtime store, linear memory | guest code, untrusted by contract | `src/HordeForge.WasmHost/Core/WasmModHost.cs:258` |
| B4 guest to game | game world state | guest, via host imports | `src/GameBridge/Bridge/GameHostApi.cs:336`, `:381`, `:429` |
| B5 guest to guest | per-mod settings and bot ownership | other loaded guests | shared settings table, `sense` snapshot, join name |
| B6 build to runtime | modlet files, native engine, module trees | whatever was copied into `dist/` or into any `Mods/` folder | `Makefile:368` (`dist`), `src/GameBridge/Bridge/NativeBootstrap.cs:35`, `src/HordeForge.WasmHost/Registry/ModuleRoots.cs:53` |
| B7 secrets to code | none in the host | `serverconfig.xml`, env | the host reads no credential |

Privilege transitions, each with a named code path:

1. A console operator types `wasm reload <id>` and a file on disk becomes
   executing code inside the game process, with the bot-servant authority of
   section 4. There is no allowlist of module ids, no signature check, and no
   record of who ran the command (`src/GameBridge/Bridge/BridgeHost.cs:821`).
2. An installed modlet drops a `Wasm/<id>/module.wasm` tree and, when the
   top-level `Mods/Wasm/wasm.toml` is absent, a `wasm.toml` that sets the
   engine's fuel and memory ceilings. Both take effect at the next server
   start with no operator step (`src/HordeForge.WasmHost/Registry/ModuleRoots.cs:53`,
   `src/GameBridge/Bridge/BridgeHost.cs:181`).
3. A guest calls `queue` and the bridge performs entity creation, teleports,
   damage application, buff changes, and player position clamps
   (`src/GameBridge/Bridge/BotServant.cs:420`).
4. A guest calls `send_chat` and the message reaches every connected player
   (`src/GameBridge/Bridge/GameHostApi.cs:429`).
5. A guest calls `get_setting` and reads operator-authored configuration,
   including the shared `[settings]` block that belongs to no single mod
   (`src/HordeForge.WasmHost/Registry/SettingsTable.cs:36`, `:58`).

Secrets: the host library reads no credential and holds no key material.
`SECURITY.md` states the rule that `wasm.toml` and `wasm-mod.toml` are
readable by every guest, which is why they must stay secret free; the code
matches that claim. The only credential in the deployment surface is the
telnet password in `serverconfig.xml` (see T9, T10).

## 4. Assets and impact

| Asset | Why it matters | Blast radius if lost or abused |
|---|---|---|
| Game process availability | a dedicated server is a multi-day session for its players | tick budget overrun degrades every player, not the guest's own world |
| Player-facing chat | global channel, not per-guest | a guest can impersonate the server to every connected player |
| World entity state (positions, health, buffs) | combat, base defense, glide | a guest can kill or teleport players and their bases |
| Player names and presence | identity in logs and in guest memory | a scraping guest harvests every join |
| Operator configuration (`wasm.toml`, manifests) | drives limits for all mods | reading it maps the operator's whole deployment; a modlet-supplied one changes every limit |
| Native engine and the modlet DLLs | the process runs them natively | a replaced `libwasmtime` is arbitrary native code, not a sandboxed guest |
| Server reputation and player trust | "unknown mod installed" | chat and entity tampering are visible to players, not just to the operator |

## 5. Threats per boundary

STRIDE classes, tied to real entry points. Where a mitigation exists it is
named here and mapped in section 7; gaps are in section 8.

**B2 operator to host**

- *Elevation of privilege*: `wasm reload <id>` compiles and instantiates a
  file from a module tree and runs its `on_enable` in the game process
  (`src/GameBridge/Bridge/BridgeHost.cs:821`). Telnet access is the only
  credential required.
- *Spoofing*: the bridge does not authenticate the operator itself, it reads
  the sender the game hands it. `wasm load`, `wasm reload`, and `wasm unload`
  name that sender (player name and address, or the local console) in the
  server log, so the change is attributable while the log survives
  (`src/GameBridge/Commands/CmdWasm.cs:185`, `:102`). It is not a credential:
  any sender the game accepts runs the command, and the name is the sender's
  own claim.
- *Tampering*: a module id differing only in case resolves to the same folder
  on Windows and macOS; the code refuses it by confirming the on-disk
  spelling (`src/HordeForge.WasmHost/Registry/ModuleRoots.cs:207`), which
  removes the double registration. The same filesystem accepts two spellings
  that are not the same id at all: a name ending in a space or a period is
  stored without it, and a device name (`con`, `com1`, and the rest) is no
  directory
  there. `ModId.IsValid` rejects both, so an id is either a folder on every
  platform or reported as not one
  (`src/HordeForge.WasmHost/Registry/ModId.cs:52`).
- *Repudiation*: `Reload` and `Unload` report success or failure and the
  reason a call was refused to the console, and the server log records the
  same call with its sender; nothing outside that log files the change
  (`src/GameBridge/Commands/CmdWasm.cs:61`, `:124`).
- *Information disclosure*: `wasm status` prints module ids, counters, the
  effective limits, and armed glide net ids to whoever can run it
  (`src/GameBridge/Bridge/BridgeHost.cs:423`, `:468`).
- *Denial of service*: `wasm load` recompiles every module in every tree
  (`src/GameBridge/Bridge/BridgeHost.cs:579`); a large tree repeated on demand
  costs compile time inside the console thread, and the `Gate` it holds is the
  same one the tick dispatch takes.

**B3 guest to host (the sandbox)**

- *Denial of service*: instruction loop (fuel), memory growth (declared
  maximum checked at load), stack recursion (engine stack ceiling), oversized
  module (size cap on the file length before the read).
- *Tampering*: guest pointers and lengths reach the host only through
  `ReadGuestString` and `WriteGuestString`, which are bounds checked by the
  binding and trap out of range
  (`src/HordeForge.WasmHost/Core/WasmModHost.cs:924`).
- *Information disclosure*: the guest sees only its own linear memory. The
  host passes strings into it, never pointers out.
- *Elevation of privilege*: the guest reaches the engine only through the
  declared imports. WASI preview 1 is linked with no preopens, no
  environment, and no stdin, and standard streams are discarded unless the
  operator opts in (`src/HordeForge.WasmHost/Config/WasmHostConfig.cs:53`,
  `src/HordeForge.WasmHost/Core/WasmModHost.cs:360`).
- *Repudiation*: a guest that traps or floods leaves counters in
  `wasm status` and the per-source log caps, but nothing that names the
  operator-visible cause beyond the sanitized trap text
  (`src/GameBridge/Bridge/BridgeHost.cs:532`).

**B4 guest to game**

- *Elevation of privilege*: the sharpest one. `ShootBot` requires the shooter
  to be a live servant bot the calling module owns
  (`src/GameBridge/Bridge/BotServant.cs:1216`) but places no restriction on
  the target beyond "a living entity", so any guest can deal 12 or 24 damage
  per command to any player, at up to 200 commands per second
  (`src/GameBridge/Bridge/BotServant.cs:1227`,
  `src/GameBridge/Bridge/GuestRateLimiter.cs:53`).
- *Tampering*: `glide <net_id> 1` arms a descent clamp and a buff on a player
  the guest does not own, which is the open part of T3. One guest can no
  longer clear another's flag: the flag table is keyed by net id and the
  ownership registry beside it by the module that armed it, and a command
  from another module is refused before the table is written
  (`src/GameBridge/Bridge/BotServant.cs:336`,
  `src/HordeForge.WasmHost/Core/GlideOwnershipRegistry.cs:21`).
- *Tampering, bounded*: `bot move` and `bot look` act only on ids the module
  owns (`src/GameBridge/Bridge/BotServant.cs:1156`, `:1178`, `:1238`), and
  `bot remove` only despawns owned bots
  (`src/GameBridge/Bridge/BotServant.cs:1057`).
- *Information disclosure*: `sense` returns up to 41 entity records with
  position, health, velocity, and glider state to whichever guest asks
  (`src/GameBridge/Bridge/BotServant.cs:87`, `:391`,
  `src/HordeForge.WasmHost/Abi/SenseSnapshotWriter.cs:43`). The records are
  the lowest 41 net ids, so the snapshot is a function of the entity set
  rather than of proximity or arrival order
  (`src/GameBridge/Bridge/SenseRecordPicker.cs:42`); that makes it stable to
  reason about and means a guest polling it sees the same long-lived low ids
  rather than a moving picture of everything.
- *Denial of service*: entity creation is capped at 16 live bots
  (`src/GameBridge/Bridge/BotServant.cs:42`) and topped up at most once a
  second (`src/GameBridge/Bridge/BotServant.cs:147`), and both `queue` and
  `sense` are per-module rate capped because the work they trigger happens
  outside the fuel budget (`src/GameBridge/Bridge/GameHostApi.cs:336`,
  `:381`).
- *Information disclosure*: `send_chat` broadcasts to every player, bounded
  to 256 code points and 10 messages per second globally
  (`src/GameBridge/Bridge/GameHostApi.cs:112`, `:429`).
- *Tampering via parsed numbers*: every SimCommand number is parsed
  invariantly and every float must be finite, so a hostile command cannot
  persist a NaN yaw into every later snapshot
  (`src/GameBridge/Bridge/BotServant.cs:1270`).

**B5 guest to guest**

- *Information disclosure*: the shared `[settings]` of `wasm.toml` is
  readable by every guest, and each guest can enumerate keys it knows. Per-mod
  settings are not shared
  (`src/HordeForge.WasmHost/Registry/SettingsTable.cs:36`, `:58`).
- *Information disclosure*: `get_join_player_name` hands the joining player's
  name to every guest that exports `on_player_join`, not only the one that
  asked for it (`src/HordeForge.WasmHost/Core/WasmModHost.cs:521`).
- *Tampering*: the `glide` verb writes a flag only for the module that holds
  it, so a second guest cannot clear another's glide
  (`src/GameBridge/Bridge/BotServant.cs:336`).

**B6 build to runtime**

- *Tampering / elevation of privilege*: `make dist` copies the Wasmtime
  native engine and the net48 bridge closure into `dist/` with no checksum or
  signature check, and the host loads `libwasmtime` by name from a path the
  operator controls (`Makefile:368`,
  `src/GameBridge/Bridge/NativeBootstrap.cs:35`). A replaced engine is native
  code, outside every guest guarantee above.
- *Tampering*: on Windows the bridge prepends the modlet's `Native/`
  directory to the process-wide `PATH`
  (`src/GameBridge/Bridge/NativeBootstrap.cs:48`), so anything dropped in
  that folder is preferred for later library lookups.
- *Elevation of privilege*: any installed modlet can add a `Wasm/` tree that
  loads at the next start, and a `wasm.toml` in that tree sets the shared
  limits when the operator's own is absent
  (`src/HordeForge.WasmHost/Registry/ModuleRoots.cs:53`,
  `src/GameBridge/Bridge/BridgeHost.cs:181`).

**B1 player to game server**

- *Spoofing / information disclosure*: a joining player's name reaches every
  guest unsanitized, while the log records only the entity id
  (`src/GameBridge/Bridge/BridgeHost.cs:370`).
- *Denial of service*: a player spamming joins drives one dispatch per spawn,
  including respawns, straight into guest `on_player_join` handlers with fuel
  budget each (`src/HordeForge.WasmHost/Core/WasmModHost.cs:524`).

## 6. Abuse cases

1. **Guest-to-player damage.** A hostile module calls `queue` with
   `bot shoot <own bot> <player entity> head` at the command cap. The server
   applies 24 damage per command, attributed to a bot entity, up to 200 times
   a second. Nothing in the path checks that the target is a zombie; the
   shooter gate is ownership, not permission
   (`src/GameBridge/Bridge/BotServant.cs:1227`).
2. **Shared settings as a config oracle.** A guest loops `get_setting` over
   candidate keys and learns the operator's shared configuration, which
   describes the deployment (limits, other mod names, connection hints).
3. **Presence harvesting.** A guest exporting `on_player_join` receives every
   player's name and entity id, including respawns, and can keep them.
4. **World scraping.** A guest polling `sense` within its per-module cap
   reconstructs entity positions and health for the lowest 41 net ids, enough
   to expose base layouts to a second account on the same server.
5. **Tick-budget capture.** Several modules each burn their full fuel budget
   every tick, and a module author may set that budget 50x over the default
   from its own manifest. Per call the fuel stops them; per tick nothing caps
   the total. The cost shows up as a slow-dispatch warning once a second
   (`src/HordeForge.WasmHost/Core/TickTelemetry.cs:37`)
   and in the heartbeat.
6. **Console-to-code.** An operator with the telnet password runs
   `wasm reload <id>` after a module file has been replaced, giving the file
   full game-process authority with no per-load decision point.
7. **Modlet-to-limits.** A third-party modlet ships `Wasm/hostile/module.wasm`
   plus `Wasm/wasm.toml` raising the memory and fuel ceilings. On a server
   whose own `Mods/Wasm/wasm.toml` is absent, the next start loads the module
   under the modlet's limits and the operator sees only the effective-limits
   line in the log (`src/GameBridge/Bridge/BridgeHost.cs:245`).
8. **Log and console forgery attempts.** A guest sends escape sequences, C1
   controls, or bidi overrides in `log` or `send_chat`. The sanitizer
   replaces them with '?' and the rate caps bound volume
   (`src/HordeForge.WasmHost/Registry/TextSanitizer.cs:24`).

## 7. Mitigations mapping

| Threat | Control | Where |
|---|---|---|
| CPU burn | fuel budget per call, re-armed before every call | `src/HordeForge.WasmHost/Core/WasmMod.cs:285`, `src/HordeForge.WasmHost/Core/WasmModHost.cs:124` |
| memory growth | engine static memory maximum, checked against the module's declared maximum at load | `src/HordeForge.WasmHost/Core/WasmModHost.cs:120`, `:316` |
| giant module | file length checked before the read, then byte length in `LoadModule` | `src/GameBridge/Bridge/BridgeHost.cs:690`, `src/HordeForge.WasmHost/Core/WasmModHost.cs:271` |
| stack exhaustion | engine maximum stack size | `src/HordeForge.WasmHost/Core/WasmModHost.cs:121` |
| filesystem and env access | WASI linked without preopens, empty environment, no stdin | `src/HordeForge.WasmHost/Core/WasmModHost.cs:124` |
| raw WASI console flooding | standard streams discarded by default, opt-in only | `src/HordeForge.WasmHost/Config/WasmHostConfig.cs:53`, `src/HordeForge.WasmHost/Core/WasmModHost.cs:360` |
| trap isolation | every call returns a `ModRunResult`; the tick walk continues | `src/HordeForge.WasmHost/Core/WasmModHost.cs:524`, `src/GameBridge/Hooks/GameTickHook.cs:14` |
| log and chat flooding | per-source rate caps, every 100th drop reported, totals in `wasm status` | `src/GameBridge/Bridge/GuestRateLimiter.cs:45`, `src/GameBridge/Bridge/GameHostApi.cs:118`, `src/GameBridge/Bridge/BridgeHost.cs:546` |
| game-side work outside the fuel budget | per-module caps on `queue` (200/s) and `sense` (200/s) | `src/GameBridge/Bridge/GameHostApi.cs:336`, `:381`, `src/GameBridge/Bridge/GuestRateLimiter.cs:53`, `:64` |
| entity multiplication | 16 live bot ceiling across all modules, top-up throttled to 1/s | `src/GameBridge/Bridge/BotServant.cs:42`, `:147` |
| one guest driving another guest's bots | every bot id is checked against the module that asked for it, in the ownership registry, before move, look, shoot, despawn, count, and the `is_self` sense bit | `src/HordeForge.WasmHost/Core/BotOwnershipRegistry.cs:51`, `src/GameBridge/Bridge/BotServant.cs:1238` |
| glide armed on a non-player | the target must resolve to a live `EntityPlayer` | `src/GameBridge/Bridge/BotServant.cs:307` |
| one guest clearing another's glide flag | the flag belongs to the module that first armed or cleared it; another module's command is refused, and unloading the module drops the flags it armed | `src/HordeForge.WasmHost/Core/GlideOwnershipRegistry.cs:21`, `src/GameBridge/Bridge/BotServant.cs:336` |
| NaN or overflow through SimCommand numbers | invariant parsing, finite-float check | `src/GameBridge/Bridge/BotServant.cs:1270` |
| path traversal through a mod id or a module file name | id validation, plain-leaf-name file name, then on-disk spelling confirmation | `src/HordeForge.WasmHost/Registry/ModId.cs:52`, `src/HordeForge.WasmHost/Registry/ModuleRoots.cs:152`, `src/HordeForge.WasmHost/Registry/ModuleRoots.cs:207` |
| manifest slurping | 1 MiB read bound, re-checked after the read, strict UTF-8 decode | `src/HordeForge.WasmHost/Registry/ManifestFiles.cs:24` |
| misspelled or misplaced limit key | `[limits]` is a closed table; unknown keys reject the module, misplaced ones are named in the load log | `src/HordeForge.WasmHost/Registry/ModManifest.cs:92`, `src/GameBridge/Bridge/BridgeHost.cs:725` |
| manifest fuel beyond the parser ceiling | 50,000,000 instruction ceiling on the file, enforced on the host config too | `src/HordeForge.WasmHost/Registry/ModManifest.cs:47`, `src/HordeForge.WasmHost/Core/WasmModHost.cs:148` |
| wrong-signature exports silently dropped | every optional export validated at load | `src/HordeForge.WasmHost/Core/WasmModHost.cs:306` |
| log, console, and chat text forgery | C0, DEL, C1, bidi, and zero-width characters replaced with '?' | `src/HordeForge.WasmHost/Registry/TextSanitizer.cs:24` |
| engine ABI drift after a game update | `make bridge-check` validates every target against the install | `tools/targetcheck/Program.cs:46` |
| engine patch lag | stated exposure, with the two named advisories assessed | `SECURITY.md` |
| runaway tick cost noticed late | once-a-second slow-dispatch warning over 25 ms, once-a-minute heartbeat | `src/HordeForge.WasmHost/Core/TickTelemetry.cs:37`, `:30` |

Single points of failure: the fuel budget carries every in-guest CPU and
memory threat; the `BridgeHost.Gate` monitor carries both the "two threads in
one engine" trap and the bridge's mutable state; the Wasmtime NuGet version
carries the whole memory-safety argument; and the shared `wasm.toml`, whoever
supplied it, carries the fuel and memory ceilings for every module.

## 8. Gaps, ranked

Recorded here, not fixed here. Each names the code that would have to change.

1. **No restriction on what a bot may shoot** (T2).
   `src/GameBridge/Bridge/BotServant.cs:1227`. The servant gates the shooter
   by ownership and not the target, so any guest can damage any living entity
   through its own bots.
2. **No module-count cap** (T4). `LoadAllModules`
   (`src/GameBridge/Bridge/BridgeHost.cs:579`) loads every valid folder in
   every tree, and the tick cost is a function of that count. The only bounds
   are fuel per call per module and the manifest fuel ceiling.
3. **A modlet can supply the module tree and the shared limits** (T5).
   `src/HordeForge.WasmHost/Registry/ModuleRoots.cs:53` and
   `src/GameBridge/Bridge/BridgeHost.cs:181`. A third-party modlet changes
   what loads and under which ceilings, and the only trace is the
   effective-limits line at start.
4. **No permission level and no durable record for load, reload, and
   unload** (T1, repudiation). Each names its sender in the server log
   (`src/GameBridge/Commands/CmdWasm.cs:185`), so the change is attributable
   while the log survives, but the bridge checks no level on the sender and
   nothing outside that log files the change.
5. **Committed telnet credential** (T9).
   `evidence/playtest-1/serverconfig.playtest.xml:8` carries the playtest
   password in plaintext and `evidence/playtest-1/run_server.sh:29` documents
   the loopback binding it was used under. The file is the record of a past
   run, so it should not be edited in place; a follow-up that rotates and
   redacts it is the right move.
6. **Unsigned native engine and modules** (T13). `Makefile:368` stages them
   and the host loads them without any integrity check.
7. **Engine version lag** (T7), tracked in `SECURITY.md`; recheck when the
   binding updates.
8. **Join spam is not rate limited** (B1). Each spawn and respawn is a full
   dispatch with fuel budget per guest, and there is no per-player join cap in
   this repository (`src/HordeForge.WasmHost/Core/WasmModHost.cs:524`).
9. **No documented path from report to fix.** `SECURITY.md` names no channel
   beyond "report to the repository maintainers" and no severity handling.

## 9. Response readiness

- Events with an audit trail: `wasm load`, `wasm reload <id>`, and
  `wasm unload <id>` name their sender in the server log, by player name and
  address or as the local console, so a guest entering or leaving the game
  process is attributable (`src/GameBridge/Commands/CmdWasm.cs:102`, `:124`,
  `:244`). Guest log lines are sanitized and attributed to
  `wasm/<mod id>`; per-module drop totals, per-mod call, trap, and fuel
  counters, the effective limits, armed glide net ids, dispatch cost, and a
  once-a-minute heartbeat are all in `wasm status` and in the log
  (`src/GameBridge/Bridge/BridgeHost.cs:423`, `:1043`).
- Events without one: when a module file was replaced, which modlet tree a
  module came from, and a per-guest view of which guest issued which
  SimCommand (the servant log lines name the verb and ids, not the calling
  mod).
- No documented disclosure-to-fix path beyond the paragraph in `SECURITY.md`.

## 10. Review metadata

- Last reviewed: 2026-09-28, against the tree at that date. Every `path:line`
  in this document was re-resolved against the source on that pass by reading
  the cited line, not by counting: the earlier revision's anchors pointed at
  doc comments and closing braces after the files moved, so the claims they
  supported were right and the citations were not.
- Owner: not recorded in this repository.
- Review cadence: not recorded in this repository.
- How to keep it true: re-verify each `path:line` when the file it points at
  changes. A reference that no longer resolves is a stale threat, not a
  formatting bug, and a reference that resolves to a comment above the code it
  names is the same defect one shift earlier.
