// guest-boss-zig: the boss watcher written in Zig, compiled to wasm32-wasi.
//
// Prints "THE BOSS IS HERE" to the server console when a player named
// "maci" (configurable via the [settings] boss_name TOML key) spawns into
// the world. The name to watch is read through the host get_setting import,
// so the operator can retune it in wasm-mod.toml without rebuilding.
//
// Build (zig 0.16):
//   zig build-exe src/main.zig -target wasm32-wasi -O ReleaseSmall \
//     -fno-entry -fstrip -flink-arg=--max-memory=33554432 \
//     -o guest-boss-zig.wasm
//
// ABI (docs/ABI.md): imports hordeforge.log / get_join_player_name /
// get_setting; exports hordeforge:mod/on_enable, tick, on_player_join.

const std = @import("std");

// Host imports. On wasm targets the extern library name is the import
// module; the function name must match the ABI import name exactly
// (docs/ABI.md), so they are named log / get_join_player_name / get_setting.
extern "hordeforge" fn log(level: i32, ptr: i32, len: i32) void;
extern "hordeforge" fn get_join_player_name(out_ptr: i32, out_cap: i32) i32;
extern "hordeforge" fn get_setting(key_ptr: i32, key_len: i32, out_ptr: i32, out_cap: i32) i32;

const log_info: i32 = 1;
const status_ok: i32 = 0;
const setting_not_found: i32 = -1;

const boss_default_name = "maci";
const msg_boss = "THE BOSS IS HERE";
const msg_loaded = "boss-zig mod loaded";

var join_name: [64]u8 = undefined;
var boss_name: [64]u8 = undefined;

fn logLine(level: i32, text: []const u8) void {
    log(level, @intCast(@intFromPtr(text.ptr)), @intCast(text.len));
}

/// Reads the boss_name setting, falling back to the built-in default.
fn loadBossName() []const u8 {
    const key = "boss_name";
    const n = get_setting(@intCast(@intFromPtr(key.ptr)), @intCast(key.len), @intCast(@intFromPtr(&boss_name)), @intCast(boss_name.len));
    if (n < 0) return boss_default_name;
    return boss_name[0..@intCast(n)];
}

fn modOnEnable() callconv(.c) i32 {
    logLine(log_info, msg_loaded);
    return status_ok;
}

fn modOnTick() callconv(.c) i32 {
    return status_ok;
}

fn modOnPlayerJoin(entity_id: i32) callconv(.c) i32 {
    _ = entity_id;
    const name = loadBossName();
    const n = get_join_player_name(@intCast(@intFromPtr(&join_name)), @intCast(join_name.len));
    if (n != name.len) return status_ok;
    const got = join_name[0..@intCast(n)];
    if (std.mem.eql(u8, got, name)) {
        logLine(log_info, msg_boss);
    }
    return status_ok;
}

comptime {
    @export(&modOnEnable, .{ .name = "on_enable" });
    @export(&modOnTick, .{ .name = "on_tick" });
    @export(&modOnPlayerJoin, .{ .name = "on_player_join" });
}
