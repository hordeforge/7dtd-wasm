namespace HordeForge.WasmHost.Config
{
    /// <summary>
    /// Hard limits and behavior flags for a <see cref="Core.WasmModHost"/>.
    /// The defaults are chosen so that an out-of-control guest can burn its
    /// budget quickly and the server main loop (20 TPS) is never blocked for
    /// long. Tune per deployment; shared overrides come from wasm.toml
    /// [limits] (docs/CONFIG.md). Values are validated when the host is
    /// constructed; a configuration the host cannot honor fails there.
    /// </summary>
    public sealed class WasmHostConfig
    {
        /// <summary>
        /// Instruction budget granted to a guest for a single call
        /// (on_enable, on_tick, on_player_join, or on_shutdown). Fuel is
        /// consumed by executed instructions; when it runs out the call
        /// stops with FuelExhausted and the host stays healthy. Default
        /// 1,000,000 instructions per call. At most
        /// <see cref="Registry.ModManifest.MaxFuelPerCall"/>: a call is the
        /// only thing bounding how long a guest can hold the loop it runs on,
        /// so a value above that is rejected at construction.
        /// </summary>
        public ulong FuelPerCall { get; set; } = 1_000_000UL;

        /// <summary>
        /// Engine-wide ceiling on the static memory (in bytes) any guest
        /// instance may use. Modules that declare a larger memory maximum are
        /// rejected at load time. Default 32 MiB, at most the wasm32 ceiling
        /// (4 GiB); a larger value bounds nothing, since no module can
        /// declare a memory maximum above it.
        /// </summary>
        public ulong StaticMemoryMaximumBytes { get; set; } = 32UL * 1024 * 1024;

        /// <summary>
        /// Hard cap on the size of a single .wasm module file accepted by
        /// LoadModule. Default 1 MiB.
        /// </summary>
        public int MaxModuleSizeBytes { get; set; } = 1024 * 1024;

        /// <summary>
        /// When true, guest writes to WASI stdout and stderr are inherited
        /// from the host process (the dedicated server console). Default
        /// false: the raw WASI path bypasses the bridge's per-module log rate
        /// cap entirely, so a hostile guest could flood the server console
        /// and logfile without bound. Guests should report through the
        /// <c>log</c> import, which is capped; an embedder that accepts the
        /// risk (for example while debugging a trusted guest) can enable this
        /// before constructing the host. The game bridge never sets it, and
        /// no config file carries it, so a staged server always discards
        /// guest standard streams; "wasm status" reports the value in force.
        /// When false, guest standard streams are discarded.
        /// </summary>
        public bool InheritGuestStandardStreams { get; set; } = false;

        /// <summary>
        /// Optional upper bound on the wasm caller stack, in bytes. Kept as a
        /// ceiling against guest recursion. Default 1 MiB. Above the engine's
        /// own 2 MiB limit the engine aborts the process from a panic rather
        /// than failing a call, so the host rejects a larger value at
        /// construction instead.
        /// </summary>
        public int MaximumStackBytes { get; set; } = 1024 * 1024;

        /// <summary>
        /// Source prefix used when forwarding guest log lines through
        /// IGameHostApi.Log, for example "wasm". Default "wasm".
        /// </summary>
        public string LogSourcePrefix { get; set; } = "wasm";
    }
}
