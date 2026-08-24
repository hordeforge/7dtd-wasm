# Guest ABI (v0)

The contract between the host and any guest module. Breaking changes here
require updating every guest and the tests together.

## Module shape

A guest is a `wasm32-wasip1` module (cdylib) that:

- imports the host API functions under module name `hordeforge`
- exports `hordeforge:mod/init`, `hordeforge:mod/tick`, and optionally
  `hordeforge:mod/shutdown`
- declares an explicit memory maximum (the host rejects modules without one;
  the shared guest toolchain pins 32 MiB via `--max-memory`)

## Host imports (module `hordeforge`)

| Import | Signature | Meaning |
|---|---|---|
| `log` | `(level: i32, ptr: i32, len: i32) -> ()` | Write a log line. Level: 0 debug, 1 info, 2 warn, 3 error |
| `get_tick` | `() -> i64` | Current game tick. The bridge maintains a monotonic counter incremented once per game tick (20 TPS on the dedicated server); `GameTimer.ticks` reads 0 on the dedicated server, so it is not used |
| `get_world_time` | `() -> i64` | World time in game minutes, 0 when no world is loaded |
| `get_setting` | `(key_ptr: i32, key_len: i32, out_ptr: i32, out_cap: i32) -> i32` | Read a shared setting. Returns written byte count, -1 not found, -2 buffer too small |
| `send_chat` | `(ptr: i32, len: i32) -> i32` | Send a global chat message. 0 accepted, -1 rejected |
| `get_join_player_name` | `(out_ptr: i32, out_cap: i32) -> i32` | During an `on_player_join` call: the joining player's name written into the guest buffer. Returns byte count, -1 no event, -2 buffer too small |

Strings are passed as `(pointer, length)` pairs into the **guest's own
linear memory**; the host reads exactly `len` bytes starting at `ptr` and
never touches guest memory beyond that range. For `get_setting`, the guest
provides the output buffer and the host writes at most `out_cap` bytes into
it.

## Guest exports

| Export | Signature | Required | Meaning |
|---|---|---|---|
| `hordeforge:mod/init` | `(boot_ptr: i32, boot_len: i32) -> i32` | yes | Called once at load. Boot payload is empty (0, 0) in v0 |
| `hordeforge:mod/tick` | `(tick: i64) -> i32` | yes | Called once per game tick |
| `hordeforge:mod/shutdown` | `() -> i32` | no | Called on unload and host dispose |
| `hordeforge:mod/on_player_join` | `() -> i32` | no | Called when a player spawns into the world; fetch the name via the `get_join_player_name` import |

Export status codes: 0 ok, 1 not implemented, 2 internal error.

## Host behavior guarantees

- Every call runs under a fresh fuel budget (default 1,000,000 instructions).
  Exceeding it stops the call with `FuelExhausted`; the module stays loaded.
- Traps return a structured `ModRunResult`; the game loop and other modules
  are unaffected.
- WASI preview 1 is linked with stdout and stderr inherited (they surface in
  the server console), no preopened directories, empty environment, no
  stdin. Guests must not rely on WASI beyond that.
- `get_world_time` and `get_setting` are safe to call before a world loads;
  they degrade to 0 and not-found.

## ABI versioning

Export and import names carry no version yet. When the surface stabilizes,
names will move to versioned forms (for example `hordeforge:mod/init@1`)
and the host will reject mismatched versions at load. Until then, treat
every change as breaking.

## Per-mod manifests (wasm-mod.json)

An operator may place `wasm-mod.json` next to a module to tighten the
host defaults. The manifest is a trusted operator file; it can only lower
limits, never raise them above the host caps.

```json
{
  "limits": {
    "fuelPerCall": 1000000,
    "maxMemoryBytes": 33554432
  }
}
```

- `fuelPerCall` overrides the host default for that module. Must be >= 1 and
  at most 50,000,000 (the host ceiling, so a runaway module cannot be given
  an unbounded budget by a careless operator).
- `maxMemoryBytes` is an additional ceiling applied on top of the host cap:
  the module's declared memory maximum must fit under both.
- Any other keys are ignored; malformed values reject the module with a
  specific reason and the bridge skips it with a warning.
