//! Shared ABI helpers for HordeForge WASM guest mods.
//!
//! Mirrors `docs/ABI.md` and `src/HordeForge.WasmHost/Abi/AbiConstants.cs`.
//! Keep the constants in sync with the host: any drift is a silent ABI
//! break.

/// Module under which the host defines its game API functions.
pub const HOST_MODULE: &str = "hordeforge";

/// Guest export names. The host requires init and tick; shutdown is optional.
pub const EXPORT_INIT: &str = "on_enable";
pub const EXPORT_TICK: &str = "on_tick";
pub const EXPORT_SHUTDOWN: &str = "on_shutdown";

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

// Host imports. Strings are passed as (pointer, length) pairs into the
// guest's own linear memory; the host reads them and never touches guest
// memory outside the given range.
#[link(wasm_import_module = "hordeforge")]
extern "C" {
    pub fn log(level: i32, ptr: i32, len: i32);
    pub fn tick() -> i64;
    pub fn get_world_time() -> i64;
    pub fn get_setting(key_ptr: i32, key_len: i32, out_ptr: i32, out_cap: i32) -> i32;
    pub fn send_chat(ptr: i32, len: i32) -> i32;
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
