/*
 * guest-boss: a C guest mod for the HordeForge WasmHost.
 *
 * When a player named "maci" spawns into the world, this module prints
 * "THE BOSS IS HERE" to the server console via the host log import.
 *
 * Built with the zig compiler (see "make boss" in the Makefile):
 *   zig cc -target wasm32-wasi -O2 -nostdlib -Wl,--no-entry \
 *          -Wl,--max-memory=33554432 -Wl,-z,stack-size=1048576 \
 *          -o guest-boss.wasm guest-boss.c
 *
 * ABI (see docs/ABI.md):
 *   imports  hordeforge.log(level, ptr, len)
 *            hordeforge.get_join_player_name(out_ptr, out_cap) -> i32
 *   exports  on_enable() -> i32                    (required)
 *            on_tick() -> i32                      (required)
 *            on_player_join(entity_id) -> i32      (optional)
 */

typedef unsigned int u32;
typedef int i32;
typedef long long i64;

/* Host imports (module "hordeforge"). */
__attribute__((import_module("hordeforge"), import_name("log")))
extern void hf_log(i32 level, i32 ptr, i32 len);

__attribute__((import_module("hordeforge"), import_name("get_join_player_name")))
extern i32 hf_get_join_player_name(i32 out_ptr, i32 out_cap);

/* Log levels, mirrored from the host ABI constants. */
#define LOG_INFO 1

/* Static strings live in the guest's linear memory; the host reads them
 * by (pointer, length) and never holds the pointer across calls. */
static const char MSG_BOSS[] = "THE BOSS IS HERE";
static const char MSG_LOADED[] = "boss mod loaded";

/* Buffer the host writes the joining player's name into. */
static char JOIN_NAME[64];

static i32 hf_strlen(const char *s)
{
    i32 n = 0;
    while (s[n] != '\0')
        n++;
    return n;
}

static void hf_log_info(const char *s)
{
    hf_log(LOG_INFO, (i32)(long)s, hf_strlen(s));
}

/* Required export: called once when the module is loaded. */
__attribute__((export_name("on_enable")))
i32 hf_mod_on_enable(void)
{
    hf_log_info(MSG_LOADED);
    return 0; /* status ok */
}

/* Required export: called once per game tick. Nothing to do here. */
__attribute__((export_name("on_tick")))
i32 hf_mod_on_tick(void)
{
    return 0; /* status ok */
}

/* Optional export: called when a player spawns into the world, with the
 * entity id (zdtd passes slot and entity id; we have no ECS slot). The name
 * is fetched through the host import into this module's own buffer, then
 * compared exactly ("maci" is case-sensitive). */
__attribute__((export_name("on_player_join")))
i32 hf_mod_on_player_join(i32 entity_id)
{
    (void)entity_id;
    i32 n = hf_get_join_player_name((i32)(long)JOIN_NAME, (i32)sizeof(JOIN_NAME));
    if (n != 4)
        return 0;
    if (JOIN_NAME[0] == 'm' && JOIN_NAME[1] == 'a' &&
        JOIN_NAME[2] == 'c' && JOIN_NAME[3] == 'i')
    {
        hf_log_info(MSG_BOSS);
    }
    return 0; /* status ok */
}
