//! Shared ABI helpers for HordeForge WASM guest mods.
//!
//! Mirrors `docs/ABI.md` and `src/HordeForge.WasmHost/Abi/AbiConstants.cs`.
//! Keep the constants in sync with the host: any drift is a silent ABI
//! break.

/// Module under which the host defines its game API functions.
pub const HOST_MODULE: &str = "hordeforge";

/// Import module the host defines for zdtd-server plugins (the sibling
/// fps_bot and its kin import this module with bare field names).
pub const ZDTD_HOST_MODULE: &str = "zdtd";

/// Guest export names. The host requires init and tick; the rest are
/// optional.
pub const EXPORT_INIT: &str = "on_enable";
pub const EXPORT_TICK: &str = "on_tick";
pub const EXPORT_SHUTDOWN: &str = "on_shutdown";
/// Optional: `(entity_id: i32) -> i32`, read the name via
/// [`join_player_name`].
pub const EXPORT_PLAYER_JOIN: &str = "on_player_join";
/// Optional: `(cmd_ptr, cmd_len, out_ptr, out_cap) -> i32` (zdtd surface).
pub const EXPORT_ADMIN_COMMAND: &str = "on_admin_command";

/// Status codes returned by guest exports. Zero always means ok.
pub const STATUS_OK: i32 = 0;
pub const STATUS_NOT_IMPLEMENTED: i32 = 1;
pub const STATUS_INTERNAL_ERROR: i32 = 2;

/// Log levels understood by the host log import.
pub const LOG_DEBUG: i32 = 0;
pub const LOG_INFO: i32 = 1;
pub const LOG_WARN: i32 = 2;
pub const LOG_ERROR: i32 = 3;

/// Status codes returned by the get_setting host import.
pub const SETTING_NOT_FOUND: i32 = -1;
pub const SETTING_BUFFER_TOO_SMALL: i32 = -2;

/// Status codes returned by the send_chat host import.
pub const CHAT_OK: i32 = 0;
pub const CHAT_REJECTED: i32 = -1;

/// Status codes returned by the zdtd queue host import.
pub const QUEUE_ACCEPTED: i32 = 0;
pub const QUEUE_REJECTED: i32 = -1;

/// Status codes returned by the zdtd query host import.
pub const QUERY_NO_ANSWER: i32 = -1;
pub const QUERY_BUFFER_TOO_SMALL: i32 = -2;

// Host imports. Strings are passed as (pointer, length) pairs into the
// guest's own linear memory; the host reads them and never touches guest
// memory outside the given range. Prefer the safe wrappers below over
// calling these directly.
#[link(wasm_import_module = "hordeforge")]
extern "C" {
    pub fn log(level: i32, ptr: i32, len: i32);
    pub fn tick() -> i64;
    pub fn get_world_time() -> i64;
    pub fn get_setting(key_ptr: i32, key_len: i32, out_ptr: i32, out_cap: i32) -> i32;
    pub fn send_chat(ptr: i32, len: i32) -> i32;
    pub fn get_join_player_name(out_ptr: i32, out_cap: i32) -> i32;
}

// zdtd-server compatibility imports. The host defines the same surface so
// plugins written against the sibling zdtd-server contract run unmodified
// (docs/ABI.md); a Rust guest may use these directly to drive bots, read the
// binary world snapshot, or read its own config.toml.
#[link(wasm_import_module = "zdtd")]
extern "C" {
    // log and tick exist in both import modules with identical signatures
    // (docs/ABI.md), and Rust has one value namespace, so the zdtd copies are
    // spelled with an explicit link_name.
    #[link_name = "log"]
    pub fn zdtd_log(level: i32, ptr: i32, len: i32);
    #[link_name = "tick"]
    pub fn zdtd_tick() -> i64;
    pub fn queue(ptr: i32, len: i32) -> i32;
    pub fn sense(ptr: i32, len: i32, token: i32) -> i32;
    pub fn query(req_ptr: i32, req_len: i32, out_ptr: i32, out_cap: i32) -> i32;
    pub fn config(out_ptr: i32, out_cap: i32) -> i32;
}

/// Guest-side scratch buffer for strings passed to the host. Mods are
/// single-threaded per the host contract, so one buffer is enough.
pub const SCRATCH_LEN: usize = 4096;
static mut SCRATCH: [u8; SCRATCH_LEN] = [0; SCRATCH_LEN];

/// Copies a string into the scratch buffer and returns (pointer, length) for
/// a host call. Panics when the string does not fit.
pub fn scratch(s: &str) -> (i32, i32) {
    let bytes = s.as_bytes();
    let len = bytes.len();
    assert!(len <= SCRATCH_LEN, "scratch buffer overflow");
    // SAFETY: the guest is single-threaded and the copy happens before the
    // host call returns. Raw-pointer access (no references into the static)
    // keeps `static_mut_refs` from ever applying here.
    unsafe {
        core::ptr::copy_nonoverlapping(
            bytes.as_ptr(),
            core::ptr::addr_of_mut!(SCRATCH).cast::<u8>(),
            len,
        );
        (core::ptr::addr_of!(SCRATCH) as *const u8 as i32, len as i32)
    }
}

/// Reads a string the host wrote into a guest buffer (for example after a
/// get_setting round trip).
pub fn read_host_string(ptr: i32, len: i32) -> String {
    if len <= 0 {
        return String::new();
    }
    // SAFETY: ptr and len come from a guest-owned buffer the guest itself
    // passed to the host; the host wrote at most len bytes there.
    let slice = unsafe { core::slice::from_raw_parts(ptr as *const u8, len as usize) };
    String::from_utf8_lossy(slice).into_owned()
}

/// Logs a debug line through the host logger.
pub fn log_debug(msg: &str) {
    let (p, l) = scratch(msg);
    // SAFETY: scratch holds the full message for the duration of the call.
    unsafe { log(LOG_DEBUG, p, l) }
}

/// Logs an info line through the host logger.
pub fn log_info(msg: &str) {
    let (p, l) = scratch(msg);
    // SAFETY: scratch holds the full message for the duration of the call.
    unsafe { log(LOG_INFO, p, l) }
}

/// Logs a warning line through the host logger.
pub fn log_warn(msg: &str) {
    let (p, l) = scratch(msg);
    // SAFETY: scratch holds the full message for the duration of the call.
    unsafe { log(LOG_WARN, p, l) }
}

/// Logs an error line through the host logger.
pub fn log_error(msg: &str) {
    let (p, l) = scratch(msg);
    // SAFETY: scratch holds the full message for the duration of the call.
    unsafe { log(LOG_ERROR, p, l) }
}

/// Sends a global chat message through the host. Returns CHAT_OK on success.
pub fn send_chat_str(msg: &str) -> i32 {
    let (p, l) = scratch(msg);
    // SAFETY: scratch holds the full message for the duration of the call.
    unsafe { send_chat(p, l) }
}

/// Reads a server or mod setting by key. Returns None when the key is
/// unknown or the value does not fit in `out`.
pub fn get_setting_str(key: &str, out: &mut [u8]) -> Option<String> {
    let (kp, kl) = scratch(key);
    let written = unsafe { get_setting(kp, kl, out.as_mut_ptr() as i32, out.len() as i32) };
    if written < 0 {
        return None;
    }
    Some(read_host_string(out.as_ptr() as i32, written))
}

/// Current game tick (safe wrapper over the raw `tick` import).
pub fn current_tick() -> i64 {
    // SAFETY: the import reads no guest memory and cannot violate safety.
    unsafe { tick() }
}

/// World time in game minutes, or 0 when no world is loaded (safe wrapper
/// over the raw `get_world_time` import).
pub fn world_time() -> i64 {
    // SAFETY: the import reads no guest memory and cannot violate safety.
    unsafe { get_world_time() }
}

/// Name of the player that most recently spawned. Only valid inside an
/// `on_player_join` call; outside one, the host reports "no event" and this
/// returns None. The name is read into `out`; None also means it did not fit.
pub fn join_player_name(out: &mut [u8]) -> Option<String> {
    let written = unsafe { get_join_player_name(out.as_mut_ptr() as i32, out.len() as i32) };
    if written < 0 {
        return None;
    }
    Some(read_host_string(out.as_ptr() as i32, written))
}

/// Queues a text SimCommand through the zdtd import ("bot move 1 2 0",
/// "glide <net_id> 1", ...). Returns true when the host accepted it.
pub fn queue_command(command: &str) -> bool {
    let (p, l) = scratch(command);
    // SAFETY: scratch holds the full command for the duration of the call.
    unsafe { queue(p, l) == QUEUE_ACCEPTED }
}

/// Fills `out` with the binary world snapshot ('ZBS4', see docs/ABI.md) and
/// returns the bytes written, or 0 when there is no world data to report.
pub fn sense_snapshot(out: &mut [u8]) -> usize {
    // token 0 asks the host for a full snapshot with no delta base.
    // SAFETY: the host writes at most out.len() bytes into the buffer.
    let written = unsafe { sense(out.as_mut_ptr() as i32, out.len() as i32, 0) };
    if written <= 0 {
        return 0;
    }
    written as usize
}

/// Asks the host a text query ("cover x z tx tz", "path x z tx tz") and
/// returns the answer, or None when the host has none or the response did
/// not fit in `out`.
pub fn query_text(request: &str, out: &mut [u8]) -> Option<String> {
    let (rp, rl) = scratch(request);
    // SAFETY: scratch holds the request; the host writes at most
    // out.len() bytes into out.
    let written = unsafe { query(rp, rl, out.as_mut_ptr() as i32, out.len() as i32) };
    if written < 0 {
        return None;
    }
    Some(read_host_string(out.as_ptr() as i32, written))
}

/// Own config.toml verbatim as UTF-8, read into `out`. The host never parses
/// it: each guest owns its format. Returns the bytes read, or 0 when the mod
/// ships no config file (the guest keeps its built-in defaults) or the buffer
/// is too small to hold the first character.
pub fn config_text(out: &mut [u8]) -> usize {
    if out.is_empty() {
        return 0;
    }
    // SAFETY: the host writes at most out.len() bytes into out, cutting only
    // at a UTF-8 character boundary.
    let written = unsafe { config(out.as_mut_ptr() as i32, out.len() as i32) };
    if written <= 0 {
        return 0;
    }
    written as usize
}
