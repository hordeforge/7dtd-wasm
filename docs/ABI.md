# Guest ABI (v0)

The contract between the host and any guest module. Breaking changes here
require updating every guest and the tests together.

The ABI is aligned with the sibling `zdtd-server` plugin contract
(docs/PLUGIN_API.md in that repository): guest hooks are exported under
their bare names
(`on_enable`, `on_tick`, `on_player_join`, `on_shutdown`), host imports live
under one project-named module with bare field names, and data crosses as
flat bytes in the guest's linear memory. The one structural difference is
noted under on_player_join.

## Module shape

A guest is a `wasm32-wasip1` module (cdylib) that:

- imports the host API functions under module name `hordeforge`
- exports `on_enable`, `on_tick`, and optionally `on_shutdown` and
  `on_player_join`
- declares an explicit memory maximum. A module without one is treated as
  declaring the wasm32 ceiling (4 GiB) and loads only when the operator
  raised the effective cap accordingly (see "Modules without a declared
  memory maximum" below); the shared guest toolchain pins 32 MiB via
  `--max-memory`, which fits the host default cap by construction

## Host imports (module `hordeforge`)

| Import | Signature | Meaning |
|---|---|---|
| `log` | `(level: i32, ptr: i32, len: i32) -> ()` | Write a log line. Level: 0 debug, 1 info, 2 warn, 3 error |
| `tick` | `() -> i64` | Current game tick. The bridge maintains a monotonic counter incremented once per game tick (20 TPS on the dedicated server); `GameTimer.ticks` reads 0 on the dedicated server, so it is not used. Same name as zdtd's `tick()` |
| `get_world_time` | `() -> i64` | World time in game minutes, 0 when no world is loaded |
| `get_setting` | `(key_ptr: i32, key_len: i32, out_ptr: i32, out_cap: i32) -> i32` | Read a setting (per-mod settings win over shared; see docs/CONFIG.md). Returns written byte count, -1 not found, -2 buffer too small |
| `send_chat` | `(ptr: i32, len: i32) -> i32` | Send a global chat message. 0 accepted, -1 rejected. Messages over 256 Unicode code points are rejected (an emoji counts as one) |
| `get_join_player_name` | `(out_ptr: i32, out_cap: i32) -> i32` | During an `on_player_join` call: the joining player's name written into the guest buffer. Returns byte count, -1 no event, -2 buffer too small |

Strings are passed as `(pointer, length)` pairs into the **guest's own
linear memory**; the host reads exactly `len` bytes starting at `ptr` and
never touches guest memory beyond that range. For `get_setting`, the guest
provides the output buffer and the host writes at most `out_cap` bytes into
it. No host pointer is ever handed to a guest.

## Guest exports

| Export | Signature | Required | Meaning |
|---|---|---|---|
| `on_enable` | `() -> i32` | yes | Called once when the mod is loaded and enabled |
| `on_tick` | `() -> i32` | yes | Called once per game tick; the tick number is read via the `tick` import |
| `on_shutdown` | `() -> i32` | no | Called on unload and host dispose |
| `on_player_join` | `(entity_id: i32) -> i32` | no | Called when a player spawns into the world; fetch the name via the `get_join_player_name` import |

An optional export that is present must have exactly this signature
(`on_shutdown` may return void for zdtd-style plugins); any other shape is
rejected at load time rather than silently dropping the handler.

Export status codes: 0 ok, 1 not implemented, 2 internal error. When
verdict-style hooks are added (deny/adjust events), they will follow the
zdtd convention: <0 deny, 0 keep, >0 percent-adjust.

Hook names are exactly zdtd's plugin hooks (`on_enable`, `on_tick`,
`on_player_join`, `on_shutdown`), so a guest author familiar with one host
recognizes the other. Two deliberate differences, both documented:

- zdtd's `on_player_join(slot, entity_id)` passes a player slot and the
  entity id; we have no ECS slot, so we pass only `(entity_id)` and the
  player name comes through the `get_join_player_name` import.
- zdtd arms one lifetime fuel budget per plugin and disables a plugin that
  exhausts it; we re-arm a fresh budget per call (ADR 0002), so a guest
  that burns fuel every tick keeps running at bounded cost instead of being
  disabled.

## zdtd-server compatibility (module `zdtd`)

Sibling zdtd-server plugins (the fps_bot and its kin) import module `zdtd`
with bare field names and export bare hooks. Quarantine defines the same
surface so those plugins run unmodified:

| Import | Signature | Meaning |
|---|---|---|
| `log` | `(level: i32, ptr: i32, len: i32) -> ()` | Same as the hordeforge log |
| `tick` | `() -> i64` | Same as the hordeforge tick |
| `queue` | `(ptr: i32, len: i32) -> i32` | Queue a text SimCommand: `bot <verb> ...` for the bot servant, `glide <net_id> <0\|1>` for the parachute mod (ADR 0037), and any other text is broadcast as a chat announce (the parachute deploy message). 0 accepted, -1 rejected |
| `sense` | `(ptr: i32, len: i32, token: i32) -> i32` | Fill the binary world snapshot ('ZBS4', format in SenseSnapshotWriter) into the guest buffer. Returns bytes written, 0 when no world data |
| `query` | `(req_ptr: i32, req_len: i32, out_ptr: i32, out_cap: i32) -> i32` | Text request/response (`cover bx bz tx tz`, `path bx bz tx tz`). Returns response bytes, -1 no answer, -2 buffer too small |
| `config` | `(out_ptr: i32, out_cap: i32) -> i32` | Copy the calling mod's config.toml verbatim, min(out_cap, len) bytes; 0 = no config (module has none, or the buffer is too small). The host never parses it; each guest owns its format (zdtd contract, so the parachute mod's on_enable reads it unchanged) |

Guest hooks are accepted with either an `i32` result (our ABI) or `void`
(zdtd contract) for `on_enable`, `on_tick`, and `on_shutdown`. The optional
`on_admin_command(cmd_ptr, cmd_len, out_ptr, out_cap) -> i32` export is
resolved when present.

The bot servant (Bridge/BotServant.cs) implements `queue` and `sense` over
the live game: bots spawn as zombieSoldier bodies, `bot move` / `bot look` /
`bot shoot` drive them, `glide <net_id> <0|1>` tracks the parachute mod's
glide flags and applies the glide effect (a fall-damage immunity buff synced
to the client, plus a server-side clamp of the descent to the sink rate),
and `sense` reports players, zombies, and our bots in the ZBS4 layout (v4:
40-byte records with server-derived `vy` from the per-tick position history
and the `wearing_glider` bit, ADR 0037). `query` (cover/path) and
`on_admin_command` console wiring are stage 3.

Host-side bounds on the servant, enforced per calling module (the wasm fuel
budget does not cover game-side work):

- SimCommands are rate capped (200/second/module); excess commands are
  rejected (-1) and counted, visible in `wasm status`.
- Sense requests are rate capped (200/second/module): each one scans the
  live world entity list on the host side, work the wasm fuel budget does
  not cover. Excess requests report no world data (0) and are counted,
  visible in `wasm status`.
- Live servant bots are capped at 16; spawn requests beyond the cap are
  refused. `bot remove` only ever despawns the servant's own bots.

Modules without a declared memory maximum are treated as declaring the
wasm32 ceiling (4 GiB) and load only when the effective cap allows it; an
operator raises the cap via `wasm.toml [limits] max_memory_bytes`. This is
how plugins built without `--max-memory` run unmodified (ADR 0004
amendment; see SECURITY.md for the weaker bound).

## Host behavior guarantees

- Every call runs under a fresh fuel budget (default 1,000,000 instructions).
  Exceeding it stops the call with `FuelExhausted`; the module stays loaded.
- Traps return a structured `ModRunResult`; the game loop and other modules
  are unaffected.
- WASI preview 1 is linked with stdout and stderr discarded by default (the
  raw WASI path cannot be rate capped, so guests must report through the
  `log` import; hosts may enable stream inheritance for trusted debugging),
  no preopened directories, empty environment, no stdin. Guests must not
  rely on WASI beyond that.
- `get_world_time` and `get_setting` are safe to call before a world loads;
  they degrade to 0 and not-found.

## ABI versioning

Export and import names carry no version yet. When the surface stabilizes,
names will move to versioned forms (for example `on_enable@1`) and the host
will reject mismatched versions at load. Until then, treat every change as
breaking.

## Per-mod manifests (wasm-mod.toml)

An operator may place `wasm-mod.toml` next to a module to tune its limits
and set per-mod settings. The manifest is a trusted operator file; it
never sets limits beyond the host caps: `fuel_per_call` overrides the
module's effective default but is rejected above the 50,000,000 ceiling,
and `max_memory_bytes` can only tighten the effective cap. The schema
follows the zdtd-server TOML conventions; see docs/CONFIG.md.

```toml
[limits]
fuel_per_call = 1000000
max_memory_bytes = 33554432

[settings]
boss_name = "maci"
```

- `limits.fuel_per_call` overrides the host default for that module. Must be
  >= 1 and at most 50,000,000 (the host ceiling, so a runaway module cannot
  be given an unbounded budget by a careless operator).
- `limits.max_memory_bytes` is an additional ceiling applied on top of the
  host cap: the module's declared memory maximum must fit under both.
- `settings` values are served to the guest through the `get_setting`
  import, per mod (the mod's own settings win over shared `wasm.toml`
  settings).
- Any other keys are ignored; malformed values reject the module with a
  specific reason and the bridge skips it with a warning. The deprecated
  JSON form (`wasm-mod.json`) is still accepted for older modules.

