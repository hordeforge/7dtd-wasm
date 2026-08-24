using System;
using System.IO;
using System.Runtime.InteropServices;

namespace HordeForge.WasmHost.Runtime
{
    /// <summary>
    /// Helpers for shipping the Wasmtime native engine alongside the managed
    /// host. On .NET Core the binding resolves the native library from the
    /// package automatically; on .NET Framework / Mono (the in-game bridge)
    /// the native library must be staged next to the managed assembly or on
    /// the platform library path. See docs/ARCHITECTURE.md.
    /// </summary>
    public static class NativeAssets
    {
        /// <summary>Native library file names per platform.</summary>
        public static readonly string[] NativeFileNames =
        {
            "libwasmtime.so",
            "libwasmtime.dylib",
            "wasmtime.dll",
        };

        /// <summary>
        /// Returns the runtime identifier used by the Wasmtime package layout,
        /// for example "linux-x64". Used to locate the native library inside
        /// the NuGet cache when staging a modlet.
        /// </summary>
        public static string RuntimeIdentifier()
        {
            string os;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) os = "win";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) os = "osx";
            else os = "linux";

            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => "unknown",
            };
            return os + "-" + arch;
        }

        /// <summary>
        /// Copies the native Wasmtime library from the NuGet package cache
        /// into <paramref name="destinationDirectory"/> when one is not
        /// already present. Returns the copied file path or the existing one.
        /// Safe to call more than once.
        /// </summary>
        public static string StageNativeLibrary(string destinationDirectory)
        {
            if (destinationDirectory == null)
            {
                throw new ArgumentNullException(nameof(destinationDirectory));
            }
            Directory.CreateDirectory(destinationDirectory);

            string rid = RuntimeIdentifier();
            string packageNative = FindNewestPackageNativeDirectory(rid);

            string? source = null;
            foreach (string file in Directory.GetFiles(packageNative))
            {
                string name = Path.GetFileName(file);
                if (Array.IndexOf(NativeFileNames, name) >= 0)
                {
                    source = file;
                    break;
                }
            }
            if (source == null)
            {
                throw new FileNotFoundException("No Wasmtime native library found under " + packageNative);
            }

            string destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
            if (!File.Exists(destination))
            {
                File.Copy(source, destination, overwrite: false);
            }
            return destination;
        }

        /// <summary>
        /// Locates runtimes/&lt;rid&gt;/native under the highest installed
        /// Wasmtime NuGet version, so staging cannot drift from the resolved
        /// package version the way a hard-coded version string would.
        /// </summary>
        private static string FindNewestPackageNativeDirectory(string rid)
        {
            string packageRoot = Path.Combine(
                GetUserProfileDirectory(),
                ".nuget",
                "packages",
                "wasmtime");
            string? newest = null;
            Version? newestVersion = null;
            if (Directory.Exists(packageRoot))
            {
                foreach (string versionDir in Directory.GetDirectories(packageRoot))
                {
                    // Only semantic version directories; anything else in
                    // the cache layout is skipped.
                    if (!Version.TryParse(Path.GetFileName(versionDir), out Version? version))
                    {
                        continue;
                    }
                    string candidate = Path.Combine(versionDir, "runtimes", rid, "native");
                    if (!Directory.Exists(candidate))
                    {
                        continue;
                    }
                    if (newestVersion == null || version > newestVersion)
                    {
                        newestVersion = version;
                        newest = candidate;
                    }
                }
            }
            if (newest == null)
            {
                throw new DirectoryNotFoundException(
                    "Wasmtime native assets not found under " + packageRoot +
                    " for runtime id " + rid +
                    ". Restore the Wasmtime NuGet package first, or stage the native library manually.");
            }
            return newest;
        }

        private static string GetUserProfileDirectory()
        {
            string? home = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(home))
            {
                home = Environment.GetEnvironmentVariable("HOME");
            }
            if (string.IsNullOrEmpty(home))
            {
                throw new InvalidOperationException("Could not determine the user profile directory");
            }
            return home;
        }
    }
}
