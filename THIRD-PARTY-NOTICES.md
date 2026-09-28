# Third-party notices

This project is MIT licensed (see [LICENSE](LICENSE)). That covers only the
code in this repository. The modlet staged by `make dist` redistributes
third-party binaries alongside our own, and the licenses below apply to them.

`make dist` copies this file and `LICENSE` into
`dist/Mods/1_HordeForge_WasmHost/` next to the binaries they cover, so the
link above resolves in the shipped modlet. The NuGet package
(`HordeForge.WasmHost`) carries this file as well.

Every NuGet component below is pinned in a committed `packages.lock.json`
with a SHA-512 content hash, and `make dist` writes a CycloneDX inventory to
`dist/SBOM.json` from those same lock files. `tools/sbom.py` holds the
machine-readable license table and fails the build if a new package appears
in a lock file without a recorded license, so this file and the SBOM cannot
drift apart silently. The guest modules in the second table are not NuGet
packages and carry no such hash; `tools/sbom.py` inventories them as
`pkg:generic` components read from the manifests under `samples/`.

## Redistributed in the modlet

| Component | Version | License | What ships |
|---|---|---|---|
| [Wasmtime](https://github.com/bytecodealliance/wasmtime-dotnet) | 44.0.0 | Apache-2.0 WITH LLVM-exception | `Wasmtime.Dotnet.dll` and the native engine (`Native/libwasmtime.so`, `.dylib`, or `.dll`) |
| [IndexRange](https://github.com/bgrainger/IndexRange) | 1.0.2 | MIT | `IndexRange.dll` (netstandard2.0 closure of Wasmtime) |
| [System.Memory](https://www.nuget.org/packages/System.Memory) | 4.5.5 | MIT | `System.Memory.dll`, .NET Foundation |
| [System.Buffers](https://www.nuget.org/packages/System.Buffers) | 4.5.1 | MIT | `System.Buffers.dll`, .NET Foundation |
| [System.Numerics.Vectors](https://www.nuget.org/packages/System.Numerics.Vectors) | 4.5.0 (modlet), 4.4.0 (netstandard2.0 host build) | MIT | `System.Numerics.Vectors.dll`, .NET Foundation |
| [System.Runtime.CompilerServices.Unsafe](https://www.nuget.org/packages/System.Runtime.CompilerServices.Unsafe) | 4.5.3 | MIT | `System.Runtime.CompilerServices.Unsafe.dll`, .NET Foundation |

`System.Runtime.CompilerServices.Unsafe.dll` is compiled against but not
staged: the bridge binds to 4.0.4.1 and the game already provides that
assembly in `Managed`.

## Redistributed guest modules

`make dist` also stages two guest modules the modlet ships as examples of
real-world compatibility. They are built by the sibling
[hordeforge/zdtd-server](https://github.com/hordeforge/zdtd-server)
project and are copied in unmodified, straight from that checkout's
`mods/` folder, as `dist/Mods/Wasm/fps-bot/module.wasm` and
`dist/Mods/Wasm/parachute/module.wasm`.

| Component | Version | License | What ships |
|---|---|---|---|
| zdtd `fps_bot`, staged as `fps-bot` | 2.5.0 | asserted by hordeforge/zdtd-server for its own build | `dist/Mods/Wasm/fps-bot/module.wasm` |
| zdtd `parachute` | 0.1.0 | asserted by hordeforge/zdtd-server for its own build | `dist/Mods/Wasm/parachute/module.wasm`, `config.toml` |

This repository does not vendor those sources, so it cannot reproduce their
license text; the SBOM records both components with the SPDX value
`NOASSERTION` rather than a license this file cannot back up. Anyone
redistributing a modlet built from this repository is responsible for
carrying the terms hordeforge/zdtd-server declares for these two modules.

### Wasmtime attribution

Wasmtime is developed by the Bytecode Alliance and contributors, and is
licensed under the Apache License, Version 2.0 with the LLVM Exceptions. The
full license text is at
<https://github.com/bytecodealliance/wasmtime/blob/main/LICENSE>. LLVM itself
is a registered trademark of LLVM Foundation; no affiliation is claimed.

Upstream that this project builds on is not modified: the NuGet package is
used as published.

## Build and test only

These resolve during a build but are not in the modlet, so the modlet's
license obligations do not reach them. They are listed because the SBOM
covers every committed lock file, not just the shipped set.

| Component | Version | License |
|---|---|---|
| [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk) | 18.9.0 | MIT |
| [Microsoft.CodeCoverage](https://www.nuget.org/packages/Microsoft.CodeCoverage) | 18.9.0 | MIT |
| [Microsoft.TestPlatform.ObjectModel](https://www.nuget.org/packages/Microsoft.TestPlatform.ObjectModel) | 18.9.0 | MIT |
| [Microsoft.TestPlatform.TestHost](https://www.nuget.org/packages/Microsoft.TestPlatform.TestHost) | 18.9.0 | MIT |
| [xunit](https://github.com/xunit/xunit) | 2.9.3 | Apache-2.0 |
| xunit.abstractions (2.0.3), xunit.analyzers (1.18.0) | as noted | Apache-2.0 |
| xunit.assert, xunit.core, xunit.extensibility.core, xunit.extensibility.execution | 2.9.3 | Apache-2.0 |
| [xunit.runner.visualstudio](https://github.com/xunit/visualstudio.xunit) | 4.0.0 | Apache-2.0 |
| [Microsoft.NETFramework.ReferenceAssemblies](https://www.nuget.org/packages/Microsoft.NETFramework.ReferenceAssemblies) | 1.0.3 | MIT |
| Microsoft.NETFramework.ReferenceAssemblies.net48 | 1.0.3 | MIT |
| [NETStandard.Library](https://www.nuget.org/packages/NETStandard.Library) | 2.0.3 | MIT |
| [Microsoft.NETCore.Platforms](https://www.nuget.org/packages/Microsoft.NETCore.Platforms) | 1.1.0 | MIT |

The guest workspace under `samples/` has no third-party crates: every
member depends on `guest-common` by path only, so `samples/Cargo.lock`
contributes no external components to the SBOM.

## Game assemblies

`make dist` does not copy any game assembly. The bridge binds against the
dedicated server's own `Managed` and `Mods` folders at build time and
references them with `<Private>false</Private>`, so nothing from
7 Days to Die is redistributed here.
