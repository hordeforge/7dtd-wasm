//! Test fixture: declares a memory maximum of 128 MiB, well above the host
//! cap (default 32 MiB). Built with an extra --max-memory link flag, so the
//! host must reject it at load time.
//!
//! The module itself is a valid, harmless mod; only its memory declaration
//! makes it unloadable.

use guest_common as abi;

#[export_name = "hordeforge:mod/init"]
pub extern "C" fn init(_boot_ptr: i32, _boot_len: i32) -> i32 {
    abi::STATUS_OK
}

#[export_name = "hordeforge:mod/tick"]
pub extern "C" fn tick(_tick: i64) -> i32 {
    abi::STATUS_OK
}
