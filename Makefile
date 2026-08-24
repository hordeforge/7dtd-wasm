# 7dtd-wasm: WebAssembly mod host for 7 Days to Die (experiment)

# Toolchains. The workspace uses net8.0 for tooling and net48 for in-game
# mod DLLs; guests are built with an in-project rustup toolchain so nothing
# is installed system-wide.
DOTNET ?= $(shell command -v dotnet 2>/dev/null || echo /home/maci/.cache/dotnet-sdk/dotnet)
CARGO  ?= $(PWD)/.cargo/bin/cargo
export RUSTUP_HOME := $(PWD)/.rustup
export CARGO_HOME := $(PWD)/.cargo

# Dedicated server install used for the net48 bridge build and target check.
GAME_DIR ?= $(HOME)/.local/share/Steam/steamapps/common/7 Days to Die Dedicated Server

SLN = HordeForge.WasmHost.sln

.PHONY: help build test samples fixtures bridge bridge-check dist check clean

help:
	@echo "Targets:"
	@echo "  make build          build the host library and test suite (net8)"
	@echo "  make test           run the host test suite"
	@echo "  make samples        compile guest mods and fixtures (wasm32-wasip1)"
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

fixtures: samples
	mkdir -p tests/fixtures
	cp samples/target/wasm32-wasip1/release/guest_trap.wasm     tests/fixtures/trap.wasm
	cp samples/target/wasm32-wasip1/release/guest_fuel.wasm     tests/fixtures/fuel.wasm
	cp samples/target/wasm32-wasip1/release/guest_strings.wasm  tests/fixtures/strings.wasm
	cp samples/target/wasm32-wasip1/release/guest_bigmem.wasm   tests/fixtures/bigmem.wasm
	cp samples/target/wasm32-wasip1/release/guest_noexports.wasm tests/fixtures/noexports.wasm
	cp samples/target/wasm32-wasip1/release/guest_hello.wasm    tests/fixtures/hello.wasm

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
	# Sample guest mod + settings.
	cp samples/target/wasm32-wasip1/release/guest_hello.wasm dist/Mods/Wasm/hello/module.wasm
	cp samples/guest-hello/wasm-mod.json dist/Mods/Wasm/hello/
	cp samples/guest-hello/wasm-settings.txt.example dist/Mods/Wasm/wasm-settings.txt
	@echo "Dist staged under dist/ (copy dist/Mods into the dedicated server's Mods/ folder)"

check:
	python3 tools/doccheck.py
	$(MAKE) build
	$(MAKE) test
	$(MAKE) bridge
	$(MAKE) bridge-check

clean:
	rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj tools/targetcheck/bin tools/targetcheck/obj dist
