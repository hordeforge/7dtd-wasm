//! Test fixture: traps unconditionally on the first tick. Used to verify
//! that the host reports a structured trap result and stays healthy.

use guest_common as abi;

#[export_name = "hordeforge:mod/init"]
pub extern "C" fn init(_boot_ptr: i32, _boot_len: i32) -> i32 {
    abi::log_info("trap fixture init");
    abi::STATUS_OK
}

#[export_name = "hordeforge:mod/tick"]
pub extern "C" fn tick(_tick: i64) -> i32 {
    abi::log_info("trap fixture about to trap");
    // This is the fixture's whole purpose.
    core::arch::wasm32::unreachable()
}
