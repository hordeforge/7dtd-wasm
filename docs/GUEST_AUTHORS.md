# Writing a guest mod

Guests are Rust cdylibs compiled to `wasm32-wasip1`. The reference
implementation is `samples/guest-hello`; the shared helpers live in
`samples/guest-common`.

## Setup

```bash
make samples   # builds every guest with the in-project rustup toolchain
```

Each guest crate:

```toml
[package]
name = "my-mod"
version = "0.1.0"
edition = "2021"

[lib]
crate-type = ["cdylib"]

[dependencies]
guest-common = { path = "../guest-common" }
```

The workspace `.cargo/config.toml` already pins `--max-memory=33554432` and
a 1 MiB stack, so the module fits the host caps by construction. Do not
override `--max-memory` upward unless the host cap is raised too.

## Minimal module

```rust
use guest_common as abi;

#[export_name = "on_enable"]
pub extern "C" fn on_enable() -> i32 {
    abi::log_info("my mod loaded");
    abi::STATUS_OK
}

#[export_name = "on_tick"]
pub extern "C" fn on_tick() -> i32 {
    let tick = abi::current_tick();
    if tick % 100 == 0 {
        abi::log_info(&format!("my mod at tick {}", tick));
    }
    abi::STATUS_OK
}

#[export_name = "on_shutdown"]
pub extern "C" fn on_shutdown() -> i32 {
    abi::log_info("my mod shutting down");
    abi::STATUS_OK
}
```

## Rules for guest code

- Export names and import signatures must match [docs/ABI.md](ABI.md)
  exactly; the host validates them at load.
- Never keep a string pointer from one call into the next: `scratch` is a
  single shared buffer, and guest memory is only valid for the duration of
  the call in which the host uses it.
- Do not allocate unbounded memory; the declared maximum (32 MiB) is a hard
  ceiling and growth past it traps.
- Keep tick work small. The per-call fuel budget (default 1,000,000
  instructions) is the only protection against a runaway module, and the
  server tick is 50 ms.
- Panicking is allowed (it traps and is reported), but prefer returning a
  status code: a guest that traps every tick spams the log.

## Host API quick reference

| Helper | What it does |
|---|---|
| `abi::log_debug(s)`, `abi::log_info(s)`, `abi::log_warn(s)`, `abi::log_error(s)` | Log through the game logger |
| `abi::current_tick()` | Current game tick |
| `abi::world_time()` | World time in game minutes, 0 when no world is loaded |
| `abi::get_setting_str(key, &mut out)` | Read a setting, `Option<String>` |
| `abi::send_chat_str(s)` | Send a global chat message, returns status |
| `abi::join_player_name(&mut out)` | Joining player's name, `Option<String>`; only valid inside `on_player_join` |

The raw imports (`abi::tick`, `abi::get_world_time`, ...) stay available for
guests that want them; the wrappers above are the safe path.

## Player join events

A Rust mod reacts to a player spawn by exporting `on_player_join` and
reading the name through `join_player_name` (valid only during the
callback):

```rust
#[export_name = "on_player_join"]
pub extern "C" fn on_player_join(entity_id: i32) -> i32 {
    let mut buf = [0u8; 128];
    if let Some(name) = abi::join_player_name(&mut buf) {
        abi::log_info(&format!("player {} joined (entity {})", name, entity_id));
    }
    abi::STATUS_OK
}
```

## Writing a guest in C (with zig)

C guests are compiled with the zig compiler (`zig cc`, no libc, no entry
point). The reference implementation is `samples/guest-boss/guest-boss.c`:

```bash
make boss
```

The module declares its host imports and guest exports with clang
attributes, and should declare a memory maximum: a module without one is
treated as declaring the wasm32 ceiling and only loads when the operator
raised the shared cap (docs/ABI.md). The Makefile passes
`--max-memory=33554432`, which fits the host default cap:

```c
__attribute__((import_module("hordeforge"), import_name("log")))
extern void hf_log(int level, int ptr, int len);

__attribute__((export_name("on_enable")))
int hf_mod_on_enable(void) { return 0; }
```

Event handlers follow the same shape as Rust guests: `on_player_join`
receives the entity id (i32); the guest fetches the player name into its
own buffer via the `get_join_player_name` import and compares it exactly.
`-nostdlib` keeps the module free of WASI libc imports; static strings in
the data section are readable by the host through `(pointer, length)`.

## Writing a guest in Zig

Zig guests are compiled with `zig build-exe` targeting `wasm32-wasi`
(reference: `samples/guest-boss-zig/src/main.zig`):

```bash
make boss-zig
```

Imports use `extern "hordeforge"` (the library string becomes the wasm
import module; the function name must match the ABI import name exactly),
and exports use `@export` with the exact ABI names. The build needs
`-fno-entry` (no main) plus `-rdynamic`, which keeps the `@export`'ed
symbols from being dead-code eliminated in release builds:

```zig
extern "hordeforge" fn log(level: i32, ptr: i32, len: i32) void;

comptime {
    @export(&modOnEnable, .{ .name = "on_enable" });
}
```

Config-driven behavior works like the other languages: the guest reads
`get_setting("boss_name")` and the operator retunes it in the mod's
`wasm-mod.toml [settings]` without rebuilding.

## Deployment

Copy the built `.wasm` into `<install>/Mods/Wasm/<id>/module.wasm` (the id
is the folder name) and run `wasm load` or `wasm reload <id>` on the server,
or restart the server. The id must be a plain folder name: no path
separators, no colons, no dot-only segments (`.` or `..`), no control
characters.
Folders with invalid names are skipped with a warning, and `wasm reload`
refuses them. An optional `wasm-mod.toml` manifest next to the
module tunes its limits and settings; see [docs/CONFIG.md](CONFIG.md). A
malformed manifest rejects the module with a warning in the server log.

## Settings

Settings follow the zdtd-server TOML conventions (docs/CONFIG.md). Shared
settings live in `<install>/Mods/Wasm/wasm.toml [settings]`; each mod's
own `[settings]` win over shared keys. Guests read them through the
`get_setting` host import, so the operator can retune behavior (for
example the boss watcher's `boss_name`) without rebuilding the module.
