# Writing a guest mod

Guests are Rust cdylibs compiled to `wasm32-wasip1`. The reference
implementation is `samples/guest-hello`; the shared helpers live in
`samples/guest-common`.

## Setup

```bash
make toolchain  # once per clone: fills .cargo/ and .rustup/ with the pinned rust
make samples    # builds the Rust guests (the cargo workspace under samples/)
                # with the in-project rustup toolchain; the C and Zig guests
                # are `make boss` and `make boss-zig`, and `make fixtures`
                # builds all three
```

`make toolchain` needs rustup on your PATH and installs the toolchain itself
into the checkout, so nothing lands system-wide. CI runs the same target. The
channel comes from `samples/rust-toolchain.toml`, the file rustup itself
resolves, so a guest built locally and one built in CI come off the same
release.

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

A crate under `samples/` must also be listed in `members` in
`samples/Cargo.toml`, or cargo ignores it and `make samples` does not build
it. That listing is also what gives it the workspace `[lints]` (every rustc
warning and the clippy default set are denied) and `panic = "abort"` in
release.

The tracked `samples/.cargo/config.toml` already pins
`--max-memory=33554432` and a 1 MiB stack, so the module fits the host caps
by construction. A guest without a declared maximum is treated as
declaring the 4 GiB wasm32 ceiling and only loads when the operator raised
the shared cap, so do not drop the flag. Do not override `--max-memory`
upward unless the host cap is raised too.

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
  instructions, raisable per module to at most 50,000,000 through
  `wasm-mod.toml [limits] fuel_per_call`) is the only bound on how long one
  guest call can run, and the server tick is 50 ms. Host-side work the guest
  triggers is capped separately, see [ABI.md](ABI.md).
- Panicking is allowed (the profile aborts, so it surfaces as a trap and is
  reported as a failed call), but prefer returning a status code. Failure
  lines are capped at 10 per second per module, so a module that traps every
  tick reports a handful of lines a second and the rest shows only as a
  running total in `wasm status`.

## Host API quick reference

| Helper | What it does |
|---|---|
| `abi::log_debug(s)`, `abi::log_info(s)`, `abi::log_warn(s)`, `abi::log_error(s)` | Log through the game logger. Capped at 10 lines per second per module; excess lines are dropped and counted for `wasm status` |
| `abi::current_tick()` | The tick number the bridge dispatches with: its own monotonic counter starting at 1 (the game's own tick reads 0 on the dedicated server, see [GAME_HOOKS.md](GAME_HOOKS.md)) |
| `abi::world_time()` | World time in game minutes, 0 when no world is loaded |
| `abi::get_setting_str(key, &mut out)` | Read a setting, `Option<String>` |
| `abi::send_chat_str(s)` | Send a global chat message, returns status |
| `abi::join_player_name(&mut out)` | Joining player's name, `Option<String>`; only valid inside `on_player_join` |
| `abi::queue_command(s)` | Queue a zdtd text SimCommand ("bot move ...", "glide ..."); true when accepted |
| `abi::sense_snapshot(&mut out)` | Fill the binary 'ZBS4' world snapshot, returns bytes written (0 when no world data) |
| `abi::query_text(req, &mut out)` | Ask a text query ("cover ...", "path ..."), `Option<String>` |
| `abi::config_text(&mut out)` | Own `config.toml` (the file next to `module.wasm` in the mod folder) verbatim, returns bytes read (0 when the mod ships none or the buffer is too small) |

The raw imports (`abi::tick`, `abi::get_world_time`, ...) stay available for
guests that want them; the wrappers above are the safe path.

The last four helpers drive the `zdtd` import module, the compatibility
surface the host defines so sibling zdtd-server plugins run unmodified (see
[ABI.md](ABI.md)). The export name constants (`abi::EXPORT_INIT`,
`abi::EXPORT_TICK`, `abi::EXPORT_SHUTDOWN`, `abi::EXPORT_PLAYER_JOIN`,
`abi::EXPORT_ADMIN_COMMAND`) name the same strings the host resolves, for
comparison or for building strings at runtime. `#[export_name]` takes a
literal, so a guest still writes the string in the attribute.

## Player join events

A Rust mod reacts to a player spawn by exporting `on_player_join` and
reading the name through `join_player_name` (valid only during the
callback). The handler also fires on respawns, not only on the first join,
so a mod that counts joins must track state across calls:

```rust
#[export_name = "on_player_join"]
pub extern "C" fn on_player_join(entity_id: i32) -> i32 {
    let mut buf = [0u8; 128];
    if abi::join_player_name(&mut buf).is_some() {
        abi::log_info(&format!("a player joined (entity {})", entity_id));
    }
    abi::STATUS_OK
}
```

The name is the only personal data a guest can read, and the server log
outlives the session: it is kept with the server data folder and pasted into
bug reports. The host logs the entity id and never the name (see "Player join
events" in [GAME_HOOKS.md](GAME_HOOKS.md)), so a guest must not put the name
into `log_*` either. Match on it inside the callback, as
`samples/guest-boss` does, and report the entity id if the mod needs to say
something. A guest that has no use for names should not export the handler
at all.

`on_admin_command(cmd_ptr, cmd_len, out_ptr, out_cap)` is the fifth export
and the third optional one. The host resolves and signature-checks it, but
no console command dispatches to it yet, so a guest that exports it is never
called; wait for the stage 3 wiring (docs/ABI.md) before relying on it.

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
is the folder name), or into `Wasm/<id>/module.wasm` inside another staged
modlet. `Mods/Wasm` is scanned first and wins per id. Then run `wasm load` or
`wasm reload <id>` on the server, or restart the server. The id must be a
plain folder name: no path
separators, no colons, no dot-only segments (`.` or `..`), no control
characters. A name ending in a space or a period, a Windows device name
(`CON`, `PRN`, `AUX`, `NUL`, `COM1`-`COM9`, `LPT1`-`LPT9`, before the first
period), a Unicode format character, a variation selector, U+2028/U+2029, or
U+FFFD is rejected as well, so the name on disk and the id must be identical.
Folders with invalid names are skipped with a warning, and `wasm reload`
refuses them. The id is matched with the exact on-disk spelling on every
platform, so a wrong-case id is a "not found" rather than a second registry
entry reading the same module. An optional `wasm-mod.toml` manifest next to
the module tunes its limits and settings; see [docs/CONFIG.md](CONFIG.md). A
malformed manifest rejects the module with a warning in the server log.

## Settings

Settings follow the zdtd-server TOML conventions (docs/CONFIG.md). Shared
settings live in `<install>/Mods/Wasm/wasm.toml [settings]`; each mod's
own `[settings]` win over shared keys. Guests read them through the
`get_setting` host import, so the operator can retune behavior (for
example the boss watcher's `boss_name`) without rebuilding the module.
