using System;
using System.IO;
using System.Runtime.InteropServices;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Makes the Wasmtime native library findable by the Mono runtime.
    /// The modlet ships the native library under &lt;modlet&gt;/Native/
    /// (staged by "make dist").
    ///
    /// Platform realities (verified against Mono and glibc):
    ///  - Windows: LoadLibrary consults PATH on every call, so prepending
    ///    the staged directory to PATH here is enough.
    ///  - ELF platforms (Linux): the dynamic loader captures LD_LIBRARY_PATH
    ///    at process start; changing it after start has no effect on later
    ///    lookups. The engine must be resolvable when the server process
    ///    starts, so Prepare probes resolution and tells the operator how
    ///    to start the server when it is not. Acceptance runs used exactly
    ///    that process-start LD_LIBRARY_PATH (docs/ACCEPTANCE.md).
    ///
    /// Must run before any Wasmtime type is touched.
    /// </summary>
    public static class NativeBootstrap
    {
        private const int RtldNow = 2;

        public static void Prepare(string modletDirectory)
        {
            string nativeDir = Path.Combine(modletDirectory, "Native");
            bool haveStaged = Directory.Exists(nativeDir);
            if (!haveStaged)
            {
                Log.Warning("[WasmHost] Native/ directory missing under " + modletDirectory + "; wasmtime native library not found");
            }

            if (IsWindows)
            {
                if (haveStaged)
                {
                    PrependToPathVariable("PATH", nativeDir);
                    Log.Out("[WasmHost] prepended " + nativeDir + " to PATH for native engine lookup");
                }
                return;
            }

            // Probe the actual capability, not the OS name: dlopen by plain
            // name uses the same startup-captured search path a later
            // DllImport("wasmtime") will see, so this answers exactly the
            // question "will the binding resolve libwasmtime.so?".
            if (ProbeNativeResolvable())
            {
                Log.Out("[WasmHost] wasmtime native engine resolves on the loader path");
                return;
            }

            Log.Warning(
                "[WasmHost] libwasmtime.so does not resolve yet; start the server with it on the loader path, " +
                "for example LD_LIBRARY_PATH=\"" + nativeDir + ":$LD_LIBRARY_PATH\". Setting the variable after " +
                "process start has no effect.");
        }

        private static bool IsWindows
        {
            get
            {
                // PlatformID.Unix / MacOSX plus 128, the pre-.NET value Mono
                // reported for Unix on old runtimes still in game servers.
                int p = (int)Environment.OSVersion.Platform;
                return p != (int)PlatformID.Unix && p != (int)PlatformID.MacOSX && p != 128;
            }
        }

        /// <summary>
        /// Returns true when libwasmtime.so resolves through the loader's
        /// startup search path. If the probe itself is unavailable on this
        /// platform, returns true so the binding reports its own error.
        /// </summary>
        private static bool ProbeNativeResolvable()
        {
            try
            {
                return dlopen("libwasmtime.so", RtldNow) != IntPtr.Zero;
            }
            catch (Exception)
            {
                return true;
            }
        }

        [DllImport("libc", EntryPoint = "dlopen")]
        private static extern IntPtr dlopen(string path, int mode);

        private static void PrependToPathVariable(string variable, string directory)
        {
            string current = Environment.GetEnvironmentVariable(variable);
            if (current != null && current.IndexOf(directory, StringComparison.Ordinal) >= 0)
            {
                return;
            }
            char sep = Path.PathSeparator;
            Environment.SetEnvironmentVariable(variable, directory + (string.IsNullOrEmpty(current) ? string.Empty : sep + current));
        }
    }
}
