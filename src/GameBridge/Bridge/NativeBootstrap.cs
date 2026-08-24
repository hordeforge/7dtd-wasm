using System;
using System.IO;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Makes the Wasmtime native library findable by the Mono runtime.
    /// The modlet ships the native library under &lt;modlet&gt;/Native/
    /// (staged by "make dist"); on Linux/Mono the library path is appended
    /// to LD_LIBRARY_PATH before the first Engine is created, on Windows the
    /// modlet directory is prepended to PATH. Must run before any Wasmtime
    /// type is touched.
    /// </summary>
    public static class NativeBootstrap
    {
        public static void Prepare(string modletDirectory)
        {
            string nativeDir = Path.Combine(modletDirectory, "Native");
            if (!Directory.Exists(nativeDir))
            {
                Log.Warning("[WasmHost] Native/ directory missing under " + modletDirectory + "; wasmtime native library not found");
                return;
            }

            if (IsWindows)
            {
                PrependToPathVariable("PATH", nativeDir);
            }
            else
            {
                PrependToPathVariable("LD_LIBRARY_PATH", nativeDir);
            }
        }

        private static bool IsWindows
        {
            get
            {
                int p = (int)Environment.OSVersion.Platform;
                return p != 4 && p != 6 && p != 128;
            }
        }

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
