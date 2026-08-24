//! Test fixture: exercises every host API call on each tick and logs what it
//! saw, so the test can assert guest-to-host and host-to-guest string
//! round trips, tick/world-time reads, settings, and chat.

use guest_common as abi;

#[export_name = "on_enable"]
pub extern "C" fn on_enable() -> i32 {
    // Multi-byte UTF-8 on purpose: the host must decode it losslessly.
    abi::log_info("strings fixture init: héllo wörld 🧟");
    abi::STATUS_OK
}

#[export_name = "on_tick"]
pub extern "C" fn on_tick() -> i32 {
    let t = unsafe { abi::tick() };
    let w = unsafe { abi::get_world_time() };
    let mut out = [0u8; 256];
    let setting = abi::get_setting_str("welcome", &mut out).unwrap_or_default();
    abi::log_info(&format!("strings tick={} world={} setting='{}'", t, w, setting));

    let missing = abi::get_setting_str("no.such.key", &mut out);
    if missing.is_none() {
        abi::log_info("strings missing-key correctly reported");
    }

    let r = abi::send_chat_str(&format!("strings fixture chat at tick {}", t));
    if r == abi::CHAT_OK {
        abi::log_info("strings chat accepted");
    } else {
        abi::log_warn("strings chat rejected");
    }
    abi::STATUS_OK
}

#[export_name = "on_shutdown"]
pub extern "C" fn on_shutdown() -> i32 {
    abi::log_info("strings fixture shutdown");
    abi::STATUS_OK
}
