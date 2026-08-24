# 7dtd-wasm: WebAssembly mod host for 7 Days to Die (experiment)

# Toolchains. The workspace uses net8.0 for tooling and net48 for in-game
# mod DLLs; guests are built with an in-project rustup toolchain so nothing
# is installed system-wide. The C guest is built with the zig compiler.
DOTNET ?= $(shell command -v dotnet 2>/dev/null || echo /home/maci/.cache/dotnet-sdk/dotnet)
CARGO  ?= $(PWD)/.cargo/bin/cargo
ZIG    ?= $(shell command -v zig 2>/dev/null || echo zig)
export RUSTUP_HOME := $(PWD)/.rustup
export CARGO_HOME := $(PWD)/.cargo

# Dedicated server install used for the net48 bridge build and target check.
GAME_DIR ?= $(HOME)/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server

SLN = HordeForge.WasmHost.sln

.PHONY: help build test samples boss boss-zig fixtures bridge bridge-check dist check clean

help:
	@echo "Targets:"
	@echo "  make build          build the host library and test suite (net8)"
	@echo "  make test           run the host test suite"
	@echo "  make samples        compile guest mods and fixtures (wasm32-wasip1)"
	@echo "  make boss           compile the C guest (samples/guest-boss) with zig"
	@echo "  make boss-zig       compile the Zig guest (samples/guest-boss-zig)"
	@echo "  make fixtures       rebuild fixtures and copy them into tests/fixtures"
	@echo "  make bridge         build the net48 in-game mod against GAME_DIR"
	@echo "  make bridge-check   validate game API targets against GAME_DIR"
	@echo "  make dist           assemble the modlet + sample guest under dist/"
	@echo "  make check          docs gate + build + test (CI entry point)"
	@echo "  GAME_DIR=...        point bridge and bridge-check at a server install"

build:
	$(DOTNET) build $(SLN) -c Release

test:
	$(DOTNET) test tests/HordeForge.WasmHost.Tests -c Release

# Build guests from inside samples/ on purpose: cargo discovers
# .cargo/config.toml by walking up from the current directory, and
# samples/.cargo/config.toml pins --max-memory and the guest stack size.
samples:
	cd samples && $(CARGO) build --release --target wasm32-wasip1

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
	cd samples/guest-boss-zig && zig build-exe src/main.zig \
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

bridge:
	$(DOTNET) build src/GameBridge/GameBridge.csproj -c Release -p:GAME_DIR="$(GAME_DIR)"

bridge-check:
	$(DOTNET) run -c Release --project tools/targetcheck -- "$(GAME_DIR)"

dist: build fixtures bridge
	rm -rf dist && mkdir -p dist/Mods/1_HordeForge_WasmHost/Native dist/Mods/Wasm/hello
	# Modlet: the net48 bridge plus its full dependency closure
	# (Wasmtime.Dotnet.dll, HordeForge.WasmHost.dll, IndexRange, System.Memory).
	# Unsafe.dll is intentionally NOT shipped: the bridge was compiled against
	# 4.0.4.1 and the game already provides it in Managed.
	cp src/GameBridge/bin/Release/*.dll dist/Mods/1_HordeForge_WasmHost/
	rm -f dist/Mods/1_HordeForge_WasmHost/System.Runtime.CompilerServices.Unsafe.dll
	cp src/GameBridge/ModInfo.xml dist/Mods/1_HordeForge_WasmHost/
	# Native engine for this platform.
	cp "$(HOME)/.nuget/packages/wasmtime/44.0.0/runtimes/linux-x64/native/libwasmtime.so" dist/Mods/1_HordeForge_WasmHost/Native/
	# Sample guest mods + shared settings (zdtd-style TOML, docs/CONFIG.md).
	mkdir -p dist/Mods/Wasm/hello dist/Mods/Wasm/boss dist/Mods/Wasm/boss-zig
	cp samples/target/wasm32-wasip1/release/guest_hello.wasm dist/Mods/Wasm/hello/module.wasm
	cp samples/guest-hello/wasm-mod.toml dist/Mods/Wasm/hello/
	cp samples/target/guest-boss.wasm dist/Mods/Wasm/boss/module.wasm
	cp samples/guest-boss/wasm-mod.toml dist/Mods/Wasm/boss/
	cp samples/target/guest-boss-zig.wasm dist/Mods/Wasm/boss-zig/module.wasm
	cp samples/guest-boss-zig/wasm-mod.toml dist/Mods/Wasm/boss-zig/
	cp samples/wasm.toml.example dist/Mods/Wasm/wasm.toml
	@echo "Dist staged under dist/ (copy dist/Mods into the dedicated server's Mods/ folder)"

check:
	python3 tools/doccheck.py
	$(MAKE) build
	$(MAKE) test
	$(MAKE) bridge
	$(MAKE) bridge-check

clean:
	rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj tools/targetcheck/bin tools/targetcheck/obj dist
