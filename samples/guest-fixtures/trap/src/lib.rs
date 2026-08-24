//! Test fixture: traps unconditionally on every tick. Used to verify that
//! the host reports a structured trap result and stays healthy.

use guest_common as abi;

#[export_name = "on_enable"]
pub extern "C" fn on_enable() -> i32 {
    abi::log_info("trap fixture init");
    abi::STATUS_OK
}

#[export_name = "on_tick"]
pub extern "C" fn on_tick() -> i32 {
    abi::log_info("trap fixture about to trap");
    // This is the fixture's whole purpose.
    core::arch::wasm32::unreachable()
}
