//! Demo guest mod: logs on init, reports a line every 100 ticks, and sends a
//! global chat message every 1000 ticks using a server setting as greeting.
//! This is the reference implementation for writing a guest mod; see
//! docs/GUEST_AUTHORS.md.

use guest_common as abi;

#[export_name = "hordeforge:mod/init"]
pub extern "C" fn init(_boot_ptr: i32, _boot_len: i32) -> i32 {
    abi::log_info("hello mod loaded");
    abi::STATUS_OK
}

#[export_name = "hordeforge:mod/tick"]
pub extern "C" fn tick(tick: i64) -> i32 {
    if tick % 100 == 0 {
        let world = unsafe { abi::get_world_time() };
        abi::log_info(&format!("hello mod alive at tick {} (world {})", tick, world));
    }
    if tick % 1000 == 0 {
        let mut out = [0u8; 256];
        let greeting = abi::get_setting_str("wasm.greeting", &mut out)
            .unwrap_or_else(|| "hello survivor".to_string());
        let msg = format!("{} from a wasm mod at tick {}", greeting, tick);
        abi::send_chat_str(&msg);
    }
    abi::STATUS_OK
}

#[export_name = "hordeforge:mod/shutdown"]
pub extern "C" fn shutdown() -> i32 {
    abi::log_info("hello mod shutting down");
    abi::STATUS_OK
}
