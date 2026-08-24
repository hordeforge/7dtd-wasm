# ADR 0001: Embed the Wasmtime.Dotnet runtime

## Status

Accepted (2026-08-24).

## Context

The project needs an embeddable WebAssembly runtime callable from .NET, for
hosting untrusted mods inside a 20 TPS game server. The choice set:

- **Wasmtime.Dotnet** (NuGet `Wasmtime`): official Bytecode Alliance .NET
  binding of the Wasmtime engine.
- **WasmerSharp**: stale (0.7.0), ~10K NuGet downloads.
- **Kraken / WACS**: pure-managed interpreters; Kraken is not published on
  NuGet at all; WACS is a niche personal project.
- **Writing a runtime**: out of scope by an order of magnitude.

Measured on 2026-08-24 via the NuGet search API: `Wasmtime` had 1,621,968
total downloads and an active release stream (44.0.0), versus 10,255 for
WasmerSharp. The binding targets netstandard2.0, netstandard2.1, net8.0, and
net9.0, and bundles native libraries per platform (win/linux/osx x64 and
arm64), which covers both the net48 in-game bridge and the net8 host tooling
from one package.

The deciding constraint: the game server needs sandbox primitives the
binding actually exposes. Wasmtime provides per-call fuel budgets, memory
and stack limits, WASI preview 1, and structured trap reporting through the
.NET API. No other maintained candidate exposes this set.

## Decision

Embed `Wasmtime` (the Wasmtime.Dotnet binding) version 44.0.0 as the
sandbox engine. The engine underneath is native Rust; the .NET side is the
official binding, not a managed reimplementation.

## Consequences

Easy: real sandbox limits with minimal code; WASI for guest stdio; the host
library stays small. Foreclosed: a pure-managed runtime (no native lib to
stage, simpler deployment) does not exist in a maintained form. Honest
downside: the in-game bridge must ship and load a native library
(`libwasmtime.so`) inside the Mono process, which adds deployment friction
and a native trust dependency; the host inherits Wasmtime's security model,
so the package must track upstream releases. Revisit if the managed runtime
landscape matures or if Wasmtime's maintenance stalls.
