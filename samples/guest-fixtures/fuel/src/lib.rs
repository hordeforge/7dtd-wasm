//! Test fixture: burns instructions forever on every tick. Used to verify
//! that the host fuel budget stops the guest mid-call, reports
//! FuelExhausted, and keeps running subsequent calls.

use guest_common as abi;

#[export_name = "on_enable"]
pub extern "C" fn on_enable() -> i32 {
    abi::log_info("fuel fixture init");
    abi::STATUS_OK
}

#[export_name = "on_tick"]
pub extern "C" fn on_tick() -> i32 {
    let mut x: u64 = 0;
    loop {
        x = x.wrapping_add(1);
        // black_box defeats dead-code elimination: the loop must run.
        std::hint::black_box(x);
    }
}
