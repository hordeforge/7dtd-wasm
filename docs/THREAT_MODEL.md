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
| T2 | A loaded guest makes the bot servant deal damage to any living entity, players included, with no per-guest ownership of the bots it fires | guest to game | High | Unmitigated |
| T3 | A guest rewrites world state for entities it does not own (glide flags, entity positions) | guest to game | High | Partly mitigated, gaps recorded in section 6 |
| T4 | Guest-controlled volume (modules x fuel per tick) is unbounded by module count, so the tick budget is lost | guest to availability | High | Partly mitigated: fuel per call, rate caps, telemetry |
| T5 | The game process is the sandbox host, and a bridge bug runs with game privileges | bridge to game | High | Out of scope by design, stated in `SECURITY.md` |
| T6 | Wasmtime engine version lags the published advisories | dependency | Medium | Tracked in `SECURITY.md`, recheck needed |
| T7 | Cross-guest information disclosure: world entity snapshot, player names, shared settings | guest to guest | Medium | Unmitigated, treated as intended sharing |
| T8 | A committed telnet password in the playtest evidence grants T1 to anyone who reads the repository | repository to console | Medium | Evidence of a past run, left intact |
| T9 | Telnet password in plaintext `serverconfig.xml`, reachable by any local reader | secrets | Medium | Documented rule, not code |
| T10 | Log, chat, and console output are attacker-shaped text | guest to log | Low | Mitigated (`TextSanitizer`, rate caps) |
| T11 | Process-wide `PATH` mutation for the native engine lookup | build to runtime | Low | Mitigated by a fixed staging path, still a plant surface |
| T12 | Wasmtime native library and guest modules ship unverified | build to runtime | Low | Unmitigated, no checksum or signature at load |

## 2. Attack surface inventory

### 2.1 Entry points present in code

| Entry point | Where | Reachable by |
|---|---|---|
| Console command `wasm list/load/reload/unload/status` | `src/GameBridge/Commands/CmdWasm.cs:32` | telnet console and in-game console, whoever the game lets run console commands |
| Module directory scan at start and on `wasm load` | `src/GameBridge/Bridge/BridgeHost.cs:338` | filesystem under `Mods/Wasm` and each staged modlet's `Wasm/` |
| `module.wasm`, `wasm-mod.toml`, `config.toml`, `wasm.toml` files | `src/GameBridge/Bridge/BridgeHost.cs:410`, `src/HordeForge.WasmHost/Registry/ManifestFiles.cs:30` | filesystem |
| Mod id from console input | `src/GameBridge/Bridge/BridgeHost.cs:511`, validated by `src/HordeForge.WasmHost/Registry/ModId.cs:25` | console |
| Harmony postfix on `GameManager.Update` (tick) | `src/GameBridge/Hooks/GameTickHook.cs:14` | the game loop, 20 calls per second |
| Harmony postfix on `GameManager.RequestToSpawnPlayer` (join) | `src/GameBridge/Hooks/PlayerSpawnHook.cs:15` | a remote player joining or respawning |
| Player name from `ClientInfo` | `src/GameBridge/Bridge/BridgeHost.cs:233` | a remote player, through the join hook |
| Host imports: `log`, `tick`, `get_world_time`, `get_setting`, `send_chat`, `get_join_player_name` | `src/HordeForge.WasmHost/Core/WasmModHost.cs:550` | every loaded guest |
| zdtd imports: `log`, `tick`, `config`, `queue`, `sense`, `query` | `src/HordeForge.WasmHost/Core/WasmModHost.cs:597` | every loaded guest |
| Guest strings written into guest memory (settings values, config bytes, sense snapshots, join name) | `src/HordeForge.WasmHost/Core/WasmModHost.cs:726` | every loaded guest |
| Environment: `LD_LIBRARY_PATH` / `DYLD_LIBRARY_PATH` / `PATH` for the native engine | `src/GameBridge/Bridge/NativeBootstrap.cs:31` | process start, operator |
| Game network port (players) and the telnet port | game server, configured in `serverconfig.xml`; playtest binding in `evidence/playtest-1/run_server.sh:22` | network |
| Developer CLIs: `targetcheck [GAME_DIR]`, `apicheck.py`, `doccheck.py`, `sbom.py` | `tools/targetcheck/Program.cs:46`, `tools/apicheck.py`, `tools/doccheck.py`, `tools/sbom.py` | the developer running them, not shipped |

### 2.2 Listed in `SECURITY.md` but not in the code

- "Wasm caller stack bounded (`MaximumStackBytes`, default 1 MiB)": configured
  on the engine at `src/HordeForge.WasmHost/Core/WasmModHost.cs:83` and
  validated non-zero. Present.

Nothing else in `SECURITY.md` names a surface the code lacks. The reverse
holds: the bot servant, the `sense` snapshot, the `glide` verb, and the
console `wasm` command are all real surfaces that `SECURITY.md` does not
mention. Sections 3 and 5 name them.

### 2.3 Inputs treated as trusted that arrive from outside the boundary

- Player names from `ClientInfo.playerName` are logged and handed to guests
  without validation beyond a non-empty check
  (`src/GameBridge/Bridge/BridgeHost.cs:233`); the log line is sanitized, the
  value handed to the guest is not.
- `wasm-mod.toml` and `config.toml` are third-party content. The manifest is
  parsed by a hand-written TOML reader
  (`src/HordeForge.WasmHost/Registry/MiniToml.cs`); `config.toml` is never
  parsed by the host and is served verbatim to the guest that owns it.
- A manifest may set its own `fuel_per_call` and `max_memory_bytes`; the host
  accepts the fuel value unconditionally and the memory value only to
  tighten (`src/HordeForge.WasmHost/Core/WasmModHost.cs:231`,
  `src/HordeForge.WasmHost/Core/WasmModHost.cs:262`).

## 3. Trust boundaries

| Boundary | Inside | Outside | Crossing point |
|---|---|---|---|
| B1 player to game server | game process | remote player, no identity beyond the game session | join hook `src/GameBridge/Hooks/PlayerSpawnHook.cs:15` |
| B2 operator to host | bridge and host library | telnet console operator, filesystem writer | `src/GameBridge/Commands/CmdWasm.cs:32`, `src/GameBridge/Bridge/BridgeHost.cs:338` |
| B3 guest to host | Wasmtime store, linear memory | guest code, untrusted by contract | `src/HordeForge.WasmHost/Core/WasmModHost.cs:169` |
| B4 guest to game | game world state | guest, via host imports | `src/GameBridge/Bridge/GameHostApi.cs:190`, `:231`, `:252` |
| B5 guest to guest | per-mod settings and stores | other loaded guests | shared settings table, `sense` snapshot, join name |
| B6 build to runtime | modlet files, native engine | whatever was copied into `dist/` | `Makefile:257` (`dist`), `src/GameBridge/Bridge/NativeBootstrap.cs:31` |
| B7 secrets to code | none in the host | `serverconfig.xml`, env | the host reads no credential |

Privilege transitions, each with a named code path:

1. A console operator types `wasm reload <id>` and a file on disk becomes
   executing code inside the game process, with the bot-servant authority of
   section 4. There is no allowlist of module ids, no signature check, and no
   record of who ran the command (`src/GameBridge/Bridge/BridgeHost.cs:511`).
2. A guest calls `queue` and the bridge performs entity creation, teleports,
   damage application, buff changes, and player position clamps
   (`src/GameBridge/Bridge/BotServant.cs:151`).
3. A guest calls `send_chat` and the message reaches every connected player
   (`src/GameBridge/Bridge/GameHostApi.cs:252`).
4. A guest calls `get_setting` and reads operator-authored configuration,
   including the shared `[settings]` block of `wasm.toml` that belongs to no
   single mod (`src/GameBridge/Bridge/WasmSettingsProvider.cs:56`).

Secrets: the host library reads no credential and holds no key material.
`SECURITY.md` states the rule that `wasm.toml` and `wasm-mod.toml` are
readable by every guest, which is why they must stay secret free; the code
matches that claim. The only credential in the deployment surface is the
telnet password in `serverconfig.xml` (see T8, T9).

## 4. Assets and impact

| Asset | Why it matters | Blast radius if lost or abused |
|---|---|---|
| Game process availability | a dedicated server is a multi-day session for its players | tick budget overrun degrades every player, not the guest's own world |
| Player-facing chat | global channel, not per-guest | a guest can impersonate the server to every connected player |
| World entity state (positions, health, buffs) | combat, base defense, glide | a guest can kill or teleport players and their bases |
| Player names and presence | identity in logs and in guest memory | a scraping guest harvests every join |
| Operator configuration (`wasm.toml`, manifests) | drives limits for all mods | reading it maps the operator's whole deployment |
| Native engine and the modlet DLLs | the process runs them natively | a replaced `libwasmtime` is arbitrary native code, not a sandboxed guest |
| Server reputation and player trust | "unknown mod installed" | chat and entity tampering are visible to players, not just to the operator |

## 5. Threats per boundary

STRIDE classes, tied to real entry points. Where a mitigation exists it is
named here and mapped in section 7; gaps are in section 8.

**B2 operator to host**

- *Elevation of privilege*: `wasm reload <id>` compiles and instantiates a
  file from `Mods/Wasm` or a modlet tree and runs its `on_enable` in the game
  process (`src/GameBridge/Bridge/BridgeHost.cs:511`). Telnet access is the
  only credential required.
- *Spoofing*: no operator identity is captured. `CmdWasm.Execute` ignores
  `CommandSenderInfo` (`src/GameBridge/Commands/CmdWasm.cs:32`), so a reload
  leaves no record of the sender beyond the game's own console log.
- *Tampering*: a module id differing only in case resolves to the same folder
  on Windows and macOS; the code refuses it by confirming the on-disk
  spelling (`src/HordeForge.WasmHost/Registry/ModuleRoots.cs:146`), which
  removes the double registration.
- *Repudiation*: `Reload` and `Unload` report only success or failure to the
  console (`src/GameBridge/Commands/CmdWasm.cs:58`); nothing durable records
  the change.
- *Information disclosure*: `wasm status` prints module ids, counters, and
  armed glide net ids to whoever can run it
  (`src/GameBridge/Bridge/BridgeHost.cs:258`).
- *Denial of service*: `wasm load` recompiles every module on disk; a large
  tree repeated on demand costs compile time inside the console thread, and
  the `Gate` it waits on is the same one the tick dispatch takes.

**B3 guest to host (the sandbox)**

- *Denial of service*: instruction loop (fuel), memory growth (declared
  maximum checked at load), stack recursion (engine stack ceiling), oversized
  module (size cap on the file length before the read).
- *Tampering*: guest pointers and lengths reach the host only through
  `ReadGuestString` and `Memory.GetSpan`, which are bounds-checked by the
  binding and trap out of range (`src/HordeForge.WasmHost/Core/WasmModHost.cs:744`).
- *Information disclosure*: the guest sees only its own linear memory. The
  host passes strings into it, never pointers out.
- *Elevation of privilege*: the guest reaches the engine only through the
  declared imports. WASI preview 1 is linked with no preopens, no
  environment, and no stdin, and standard streams are discarded unless the
  operator opts in (`src/HordeForge.WasmHost/Config/WasmHostConfig.cs:45`,
  `src/HordeForge.WasmHost/Core/WasmModHost.cs:271`).
- *Repudiation*: a guest that traps or floods leaves counters in
  `wasm status` and the per-source log caps, but nothing that names the
  operator-visible cause beyond the sanitized trap text
  (`src/GameBridge/Bridge/BridgeHost.cs:315`).

**B4 guest to game**

- *Elevation of privilege*: the sharpest one. `ShootBot` requires the shooter
  to be a servant bot but places no restriction on the target, so any guest
  can deal 12 or 24 damage per command to any living entity, other players
  included, at up to 200 commands per second
  (`src/GameBridge/Bridge/BotServant.cs:841`,
  `src/GameBridge/Bridge/GuestRateLimiter.cs:34`). Bots are owned by the
  servant, not by the guest that fires them.
- *Tampering*: `glide <net_id> 1` arms a descent clamp on a player the guest
  does not own, and any guest can clear another guest's flag, because the
  table is global and keyed by net id alone
  (`src/GameBridge/Bridge/BotServant.cs:177`).
- *Tampering, bounded*: `bot move` and `bot look` act only on ids the servant
  tracks (`src/GameBridge/Bridge/BotServant.cs:802`), and `bot remove` only
  despawns tracked bots (`src/GameBridge/Bridge/BotServant.cs:767`).
- *Information disclosure*: `sense` returns up to 41 entity records with
  position, health, velocity, and glider state to whichever guest asks
  (`src/GameBridge/Bridge/BotServant.cs:331`,
  `src/HordeForge.WasmHost/Abi/SenseSnapshotWriter.cs:1`).
- *Denial of service*: entity creation is capped at 16 live bots
  (`src/GameBridge/Bridge/BotServant.cs:29`) and topped up at most once a
  second, and both `queue` and `sense` are per-module rate capped because the
  work they trigger happens outside the fuel budget.
- *Information disclosure*: `send_chat` broadcasts to every player, bounded
  to 256 code points and 10 messages per second globally
  (`src/GameBridge/Bridge/GameHostApi.cs:266`).

**B5 guest to guest**

- *Information disclosure*: the shared `[settings]` of `wasm.toml` is
  readable by every guest, and each guest can enumerate keys it knows.
  Per-mod settings are not shared (`src/HordeForge.WasmHost/Registry/SettingsTable.cs`).
- *Information disclosure*: `get_join_player_name` hands the joining player's
  name to every guest that exports `on_player_join`, not only the one that
  asked for it (`src/HordeForge.WasmHost/Core/WasmModHost.cs:575`).
- *Tampering*: one guest can disable another's glide state through the same
  global table.

**B6 build to runtime**

- *Tampering / elevation of privilege*: `make dist` copies the Wasmtime
  native engine and the net48 bridge closure into `dist/` with no checksum or
  signature check, and the host loads `libwasmtime` by name from a path the
  operator controls (`src/GameBridge/Bridge/NativeBootstrap.cs:31`). A
  replaced engine is native code, outside every guest guarantee above.
- *Tampering*: on Windows the bridge prepends the modlet's `Native/` directory
  to the process-wide `PATH` (`src/GameBridge/Bridge/NativeBootstrap.cs:133`),
  so anything dropped in that folder is preferred for later library lookups.

**B1 player to game server**

- *Spoofing / information disclosure*: a joining player's name is logged
  verbatim apart from control-character stripping
  (`src/GameBridge/Bridge/BridgeHost.cs:244`) and passed to every guest.
- *Denial of service*: a player spamming joins drives one dispatch per spawn,
  including respawns, straight into guest `on_player_join` handlers with fuel
  budget each.

## 6. Abuse cases

1. **Guest-to-player damage.** A hostile module calls `queue` with
   `bot shoot <own bot> <player entity> head` at the command cap. The server
   applies 24 damage per command, attributed to a bot entity, up to 200 times
   a second. Nothing in the path checks that the target is a zombie or that
   the firing guest is entitled to that bot.
2. **Guest-to-guest glide denial.** The parachute plugin's flag table is
   global; a second module issuing `glide <net_id> 0` for a player removes the
   first module's armed flag, and the server stops clamping that player's
   descent.
3. **Shared settings as a config oracle.** A guest loops `get_setting` over
   candidate keys and learns the operator's shared configuration, which
   describes the deployment (limits, other mod names, connection hints).
4. **Presence harvesting.** A guest exporting `on_player_join` receives every
   player's name and entity id, including respawns, and can keep them.
5. **World scraping.** A guest polling `sense` within its per-module cap
   reconstructs entity positions and health, enough to expose base layouts
   to a second account on the same server.
6. **Tick-budget capture.** Several modules each burn their full fuel budget
   every tick. Per call the fuel stops them; per tick nothing caps the total.
   The cost shows up as a slow-dispatch warning once a second and in the
   heartbeat, and the game loop loses time.
7. **Console-to-code.** An operator with the telnet password runs
   `wasm reload <id>` after a module file has been replaced, giving the file
   full game-process authority with no per-load decision point.
8. **Log and console forgery attempts.** A guest sends escape sequences, C1
   controls, or bidi overrides in `log` or `send_chat`. The sanitizer
   replaces them with '?' and the rate caps bound volume
   (`src/HordeForge.WasmHost/Registry/TextSanitizer.cs:24`).

## 7. Mitigations mapping

| Threat | Control | Where |
|---|---|---|
| CPU burn | fuel budget per call, re-armed before every call | `src/HordeForge.WasmHost/Core/WasmMod.cs:174`, `src/HordeForge.WasmHost/Core/WasmModHost.cs:80` |
| memory growth | engine static memory maximum, checked against the module's declared maximum at load | `src/HordeForge.WasmHost/Core/WasmModHost.cs:82`, `:233` |
| giant module | file length checked before the read, then byte length in `LoadModule` | `src/GameBridge/Bridge/BridgeHost.cs:430`, `src/HordeForge.WasmHost/Core/WasmModHost.cs:182` |
| stack exhaustion | engine maximum stack size | `src/HordeForge.WasmHost/Core/WasmModHost.cs:83` |
| filesystem and env access | WASI linked without preopens, empty environment, no stdin | `src/HordeForge.WasmHost/Core/WasmModHost.cs:86` |
| raw WASI console flooding | standard streams discarded by default, opt-in only | `src/HordeForge.WasmHost/Config/WasmHostConfig.cs:45` |
| trap isolation | every call returns a `ModRunResult`; the tick walk continues | `src/HordeForge.WasmHost/Core/WasmModHost.cs:445`, `src/GameBridge/Hooks/GameTickHook.cs:14` |
| log and chat flooding | per-source rate caps, every 100th drop reported, totals in `wasm status` | `src/GameBridge/Bridge/GuestRateLimiter.cs:22`, `src/GameBridge/Bridge/GameHostApi.cs:78` |
| game-side work outside the fuel budget | per-module caps on `queue` (200/s) and `sense` (200/s) | `src/GameBridge/Bridge/GameHostApi.cs:195`, `:238` |
| entity multiplication | 16 live bot ceiling, top-up throttled to 1/s | `src/GameBridge/Bridge/BotServant.cs:29`, `:628` |
| path traversal through a mod id | id validation, then on-disk spelling confirmation | `src/HordeForge.WasmHost/Registry/ModId.cs:25`, `src/HordeForge.WasmHost/Registry/ModuleRoots.cs:146` |
| manifest slurping | 1 MiB read bound, re-checked after the read | `src/HordeForge.WasmHost/Registry/ManifestFiles.cs:24` |
| wrong-signature exports silently dropped | every optional export validated at load | `src/HordeForge.WasmHost/Core/WasmModHost.cs:242` |
| log, console, and chat text forgery | C0, DEL, C1, bidi, and zero-width characters replaced with '?' | `src/HordeForge.WasmHost/Registry/TextSanitizer.cs:24` |
| engine ABI drift after a game update | `make bridge-check` validates every target against the install | `tools/targetcheck/Program.cs:46` |
| engine patch lag | stated exposure, with the two named advisories assessed | `SECURITY.md` |

Single points of failure: the fuel budget carries every in-guest CPU and
memory threat; the `BridgeHost.Gate` monitor carries both the "two threads in
one engine" trap and the bridge's mutable state; the Wasmtime NuGet version
carries the whole memory-safety argument.

## 8. Gaps, ranked

Recorded here, not fixed here. Each names the code that would have to change.

1. **No restriction on what a bot may shoot** (T2).
   `src/GameBridge/Bridge/BotServant.cs:841`. The servant gates the shooter
   and not the target, and does not record which guest owns which bot.
2. **No module-count cap** (T4). `LoadAllModules`
   (`src/GameBridge/Bridge/BridgeHost.cs:338`) loads every valid folder it
   finds, and the tick cost is a function of that count. The only bound is
   fuel per call per module.
3. **No operator identity or durable record for load, reload, and unload**
   (T1, repudiation). `src/GameBridge/Commands/CmdWasm.cs:32` ignores
   `CommandSenderInfo`; success is reported to the console only.
4. **Cross-guest state has no ownership** (T3). Glide flags, bot bodies, and
   the sense snapshot are keyed by net id alone, with no per-guest
   partitioning (`src/GameBridge/Bridge/BotServant.cs:177`).
5. **Committed telnet credential** (T8).
   `evidence/playtest-1/serverconfig.playtest.xml:8` carries the playtest
   password in plaintext, and `evidence/playtest-1/run_server.sh:22` documents
   it. The file is the record of a past run, so it should not be edited in
   place; a follow-up that rotates and redacts it is the right move.
6. **Unsigned native engine and modules** (T12). `Makefile:257` stages them
   and the host loads them without any integrity check.
7. **Engine version lag** (T6), tracked in `SECURITY.md`; recheck when the
   binding updates.
8. **Join spam is not rate limited** (B1). Each spawn and respawn is a full
   dispatch with fuel budget per guest, and there is no per-player join cap in
   this repository (`src/GameBridge/Bridge/BridgeHost.cs:220`).
9. **No documented path from report to fix.** `SECURITY.md` names no channel
   beyond "report to the repository maintainers" and no severity handling.

## 9. Response readiness

- Events with an audit trail: guest log lines are sanitized and attributed to
  `wasm/<mod id>`; per-module drop totals, per-mod call, trap, and fuel
  counters, dispatch cost, and a once-a-minute heartbeat are all in `wasm
  status` and in the log (`src/GameBridge/Bridge/BridgeHost.cs:206`,
  `:258`).
- Events without one: who ran a console command, when a module file was
  replaced, and a per-guest view of which guest issued which SimCommand
  (the servant log lines name the verb and ids, not the calling mod).
- No documented disclosure-to-fix path beyond the paragraph in `SECURITY.md`.

## 10. Review metadata

- Last reviewed: 2026-09-28, against the tree at that date.
- Owner: not recorded in this repository.
- Review cadence: not recorded in this repository.
- How to keep it true: re-verify each `path:line` when the file it points at
  changes. A reference that no longer resolves is a stale threat, not a
  formatting bug.
