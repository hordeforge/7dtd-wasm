//! Test fixture: declares a memory maximum of 128 MiB, well above the host
//! cap (default 32 MiB). Built with an extra --max-memory link flag, so the
//! host must reject it at load time.
//!
//! The module itself is a valid, harmless mod; only its memory declaration
//! makes it unloadable.

use guest_common as abi;

#[export_name = "on_enable"]
pub extern "C" fn on_enable() -> i32 {
    abi::STATUS_OK
}

#[export_name = "on_tick"]
pub extern "C" fn on_tick() -> i32 {
    abi::STATUS_OK
}
