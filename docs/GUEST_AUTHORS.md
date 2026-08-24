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

#[export_name = "hordeforge:mod/init"]
pub extern "C" fn init(_boot_ptr: i32, _boot_len: i32) -> i32 {
    abi::log_info("my mod loaded");
    abi::STATUS_OK
}

#[export_name = "hordeforge:mod/tick"]
pub extern "C" fn tick(tick: i64) -> i32 {
    if tick % 100 == 0 {
        abi::log_info(&format!("my mod at tick {}", tick));
    }
    abi::STATUS_OK
}

#[export_name = "hordeforge:mod/shutdown"]
pub extern "C" fn shutdown() -> i32 {
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
| `abi::log_info(s)`, `abi::log_warn(s)`, `abi::log_error(s)` | Log through the game logger |
| `abi::get_setting_str(key, &mut out)` | Read a shared setting, `Option<String>` |
| `abi::send_chat_str(s)` | Send a global chat message, returns status |
| `unsafe { abi::get_tick() }`, `unsafe { abi::get_world_time() }` | Read game time |

## Writing a guest in C (with zig)

C guests are compiled with the zig compiler (`zig cc`, no libc, no entry
point). The reference implementation is `samples/guest-boss/guest-boss.c`:

```bash
make boss
```

The module declares its host imports and guest exports with clang
attributes, and must declare a memory maximum (the host rejects modules
without one; the Makefile passes `--max-memory=33554432`):

```c
__attribute__((import_module("hordeforge"), import_name("log")))
extern void hf_log(int level, int ptr, int len);

__attribute__((export_name("hordeforge:mod/init")))
int hf_mod_init(int boot_ptr, int boot_len) { return 0; }
```

Event handlers follow the same shape as Rust guests: `on_player_join`
receives no arguments; the guest fetches the player name into its own
buffer via the `get_join_player_name` import and compares it exactly.
`-nostdlib` keeps the module free of WASI libc imports; static strings in
the data section are readable by the host through `(pointer, length)`.

## Deployment

Copy the built `.wasm` into `<install>/Mods/Wasm/<id>/module.wasm` (the id
is the folder name) and run `wasm load` or `wasm reload <id>` on the server,
or restart the server. An optional `wasm-mod.json` manifest next to the
module tunes its limits (fuel per call, memory ceiling); see
[docs/ABI.md](ABI.md). A malformed manifest rejects the module with a
warning in the server log.
