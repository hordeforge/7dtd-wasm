# 7dtd-wasm: WebAssembly mod host for 7 Days to Die (experiment)

# Toolchains. The workspace uses net8.0 for tooling and net48 for in-game
# mod DLLs; guests are built with an in-project rustup toolchain so nothing
# is installed system-wide. The C guest is built with the zig compiler.
# DOTNET prefers the workspace-local SDK under $(HOME)/.cache when present
# and falls back to PATH dotnet (which may be missing or SDK-less); it must
# never name a specific user.
DOTNET ?= $(shell test -x $(HOME)/.cache/dotnet-sdk/dotnet && echo $(HOME)/.cache/dotnet-sdk/dotnet || command -v dotnet 2>/dev/null || echo $(HOME)/.cache/dotnet-sdk/dotnet)
CARGO  ?= $(PWD)/.cargo/bin/cargo
ZIG    ?= $(shell command -v zig 2>/dev/null || echo zig)
export RUSTUP_HOME := $(PWD)/.rustup
export CARGO_HOME := $(PWD)/.cargo

# NuGet restore mode for the dotnet targets below. Plain builds stay
# unlocked so dependency bumps regenerate packages.lock.json; "make
# check" flips this to true so a manifest that drifts from its
# committed lock file fails loudly instead of re-resolving packages.
export RESTORE_LOCKED ?= false

# Wasmtime runtime id of THIS machine, used by "make dist" to stage the
# matching native engine (same mapping as NativeAssets.RuntimeIdentifier).
UNAME_S := $(shell uname -s 2>/dev/null || echo Windows_NT)
UNAME_M := $(shell uname -m 2>/dev/null)
ifeq ($(OS),Windows_NT)
  WASMTIME_OS := win
else ifeq ($(UNAME_S),Darwin)
  WASMTIME_OS := osx
else
  WASMTIME_OS := linux
endif
ifneq (,$(filter aarch64 arm64,$(UNAME_M)))
  WASMTIME_ARCH := arm64
else
  WASMTIME_ARCH := x64
endif
WASMTIME_RID := $(WASMTIME_OS)-$(WASMTIME_ARCH)
ifeq ($(WASMTIME_OS),win)
  WASMTIME_NATIVE := wasmtime.dll
else ifeq ($(WASMTIME_OS),osx)
  WASMTIME_NATIVE := libwasmtime.dylib
else
  WASMTIME_NATIVE := libwasmtime.so
endif

# Dedicated server install used for the net48 bridge build and target check.
GAME_DIR ?= $(HOME)/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server

SLN = HordeForge.WasmHost.sln

# Wasmtime NuGet version as resolved into the committed lock file, so the
# staged native engine always matches the managed binding (single source
# of truth; bumping the PackageReference updates dist automatically).
WASMTIME_VERSION := $(shell python3 -c "import json; d = json.load(open('src/HordeForge.WasmHost/packages.lock.json')); print(next(m['Wasmtime']['resolved'] for m in d['dependencies'].values() if 'Wasmtime' in m))")

.PHONY: help build test samples samples-check boss boss-zig fixtures bridge bridge-check dist check check-ci clean

help:
	@echo "Targets:"
	@echo "  make build          build the host library and test suite (net8)"
	@echo "  make test           run the host test suite"
	@echo "  make samples        compile guest mods and fixtures (wasm32-wasip1)"
	@echo "  make samples-check  guest lint gate (rustc + clippy denied)"
	@echo "  make boss           compile the C guest (samples/guest-boss) with zig"
	@echo "  make boss-zig       compile the Zig guest (samples/guest-boss-zig)"
	@echo "  make fixtures       rebuild fixtures and copy them into tests/fixtures"
	@echo "  make bridge         build the net48 in-game mod against GAME_DIR"
	@echo "  make bridge-check   validate game API targets against GAME_DIR"
	@echo "  make dist           assemble the modlet + sample guest under dist/"
	@echo "                      (also writes dist/SBOM.json from the lock files)"
	@echo "  make check          docs gate + sbom tests + tools lint + guest lint gate + build + test + bridge-check"
	@echo "  make check-ci       the half of check that needs no game install (CI entry point)"
	@echo "  GAME_DIR=...        point bridge and bridge-check at a server install"

build:
	$(DOTNET) build $(SLN) -c Release -p:RestoreLockedMode=$(RESTORE_LOCKED)

test:
	$(DOTNET) test tests/HordeForge.WasmHost.Tests -c Release -p:RestoreLockedMode=$(RESTORE_LOCKED)

# Compile guests from inside samples/ on purpose: cargo discovers
# config by walking up from the current directory, and the workspace
# [lints] in samples/Cargo.toml deny every default rustc warning.
samples:
	cd samples && $(CARGO) build --release --target wasm32-wasip1

# Guest lint gate: a plain build already fails on any default rustc
# warning (workspace [lints]); clippy then runs its default set at deny
# (workspace [lints] clippy all = "deny"). This keeps both gates in make
# check so guest code cannot regress silently between fixture rebuilds.
samples-check:
	cd samples && $(CARGO) build --release --target wasm32-wasip1
	cd samples && $(CARGO) clippy --release --target wasm32-wasip1

# The C guest (samples/guest-boss) is compiled with zig to wasm32-wasi
# (preview 1). -nostdlib keeps it free of WASI libc imports; --max-memory
# declares the 32 MiB maximum the host requires.
boss:
	mkdir -p samples/target
	$(ZIG) cc -target wasm32-wasi -O2 -nostdlib -Wl,--no-entry \
	  -Wl,--max-memory=33554432 -Wl,-z,stack-size=1048576 \
	  -o samples/target/guest-boss.wasm samples/guest-boss/guest-boss.c

# Zig guest (zig 0.16): -rdynamic keeps the @export'ed symbols in the
# -fno-entry module (without it everything is dead-code eliminated).
boss-zig:
	mkdir -p samples/target
	cd samples/guest-boss-zig && $(ZIG) build-exe src/main.zig \
	  -target wasm32-wasi -O ReleaseSmall -fno-entry -fstrip -rdynamic \
	  --max-memory=33554432 -femit-bin=guest-boss-zig.wasm
	cp samples/guest-boss-zig/guest-boss-zig.wasm samples/target/

fixtures: samples boss boss-zig
	mkdir -p tests/fixtures
	cp samples/target/wasm32-wasip1/release/guest_trap.wasm     tests/fixtures/trap.wasm
	cp samples/target/wasm32-wasip1/release/guest_fuel.wasm     tests/fixtures/fuel.wasm
	cp samples/target/wasm32-wasip1/release/guest_strings.wasm  tests/fixtures/strings.wasm
	cp samples/target/wasm32-wasip1/release/guest_bigmem.wasm   tests/fixtures/bigmem.wasm
	cp samples/target/wasm32-wasip1/release/guest_noexports.wasm tests/fixtures/noexports.wasm
	cp samples/target/wasm32-wasip1/release/guest_hello.wasm    tests/fixtures/hello.wasm
	cp samples/target/guest-boss.wasm                           tests/fixtures/boss.wasm
	cp samples/target/guest-boss-zig.wasm                       tests/fixtures/boss-zig.wasm
	# The unmodified zdtd fps_bot plugin (workspace sibling), committed as a
	# fixture so the compatibility surface is tested against the real module.
	cp ../zdtd-server/mods/fps_bot/fps_bot.wasm                 tests/fixtures/fps-bot.wasm

bridge:
	$(DOTNET) build src/GameBridge/GameBridge.csproj -c Release -p:GAME_DIR="$(GAME_DIR)" -p:RestoreLockedMode=$(RESTORE_LOCKED)

bridge-check:
	$(DOTNET) run -c Release --project tools/targetcheck -p:RestoreLockedMode=$(RESTORE_LOCKED) -- "$(GAME_DIR)"

dist: build fixtures bridge
	rm -rf dist && mkdir -p dist/Mods/1_HordeForge_WasmHost/Native dist/Mods/Wasm/hello
	# Modlet: the net48 bridge plus its full dependency closure
	# (Wasmtime.Dotnet.dll, HordeForge.WasmHost.dll, IndexRange, System.Memory).
	# Unsafe.dll is intentionally NOT shipped: the bridge was compiled against
	# 4.0.4.1 and the game already provides it in Managed.
	cp src/GameBridge/bin/Release/*.dll dist/Mods/1_HordeForge_WasmHost/
	rm -f dist/Mods/1_HordeForge_WasmHost/System.Runtime.CompilerServices.Unsafe.dll
	cp src/GameBridge/ModInfo.xml dist/Mods/1_HordeForge_WasmHost/
	# Native engine for this platform ($(WASMTIME_RID), see header).
	cp "$(HOME)/.nuget/packages/wasmtime/$(WASMTIME_VERSION)/runtimes/$(WASMTIME_RID)/native/$(WASMTIME_NATIVE)" dist/Mods/1_HordeForge_WasmHost/Native/
	# Sample guest mods + shared settings (zdtd-style TOML, docs/CONFIG.md).
	mkdir -p dist/Mods/Wasm/hello dist/Mods/Wasm/boss dist/Mods/Wasm/boss-zig dist/Mods/Wasm/fps-bot
	cp samples/target/wasm32-wasip1/release/guest_hello.wasm dist/Mods/Wasm/hello/module.wasm
	cp samples/guest-hello/wasm-mod.toml dist/Mods/Wasm/hello/
	cp samples/target/guest-boss.wasm dist/Mods/Wasm/boss/module.wasm
	cp samples/guest-boss/wasm-mod.toml dist/Mods/Wasm/boss/
	cp samples/target/guest-boss-zig.wasm dist/Mods/Wasm/boss-zig/module.wasm
	cp samples/guest-boss-zig/wasm-mod.toml dist/Mods/Wasm/boss-zig/
	# The unmodified zdtd fps_bot plugin (workspace sibling); the shared
	# wasm.toml must raise limits.max_memory_bytes for it to load.
	cp ../zdtd-server/mods/fps_bot/fps_bot.wasm dist/Mods/Wasm/fps-bot/module.wasm
	cp samples/zdtd-fps-bot/wasm-mod.toml dist/Mods/Wasm/fps-bot/
	cp samples/wasm.toml.example dist/Mods/Wasm/wasm.toml
	# SBOM: CycloneDX inventory built from the committed lock files, so
	# consumers and vuln scanners know exactly what shipped.
	python3 tools/sbom.py --root . -o dist/SBOM.json
	@echo "Dist staged under dist/ (copy dist/Mods into the dedicated server's Mods/ folder)"

check: export RESTORE_LOCKED := true
check: check-ci
	$(MAKE) bridge
	$(MAKE) bridge-check

# The game-free half of check, and the only entry point CI can use: a hosted
# runner has no dedicated server install, so bridge and bridge-check (which
# need GAME_DIR) stay out. Everything here fails loudly rather than skipping,
# because a skipped gate reads like a passed one.
check-ci: export RESTORE_LOCKED := true
check-ci:
	python3 tools/doccheck.py
	python3 -m unittest discover -s tools
	ruff check tools
	$(MAKE) samples-check
	$(MAKE) build
	$(MAKE) test

clean:
	rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj tools/targetcheck/bin tools/targetcheck/obj dist
