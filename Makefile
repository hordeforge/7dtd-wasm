# 7dtd-wasm: WebAssembly mod host for 7 Days to Die (experiment)

# Toolchains. The workspace uses net8.0 for tooling and net48 for in-game
# mod DLLs; guests are built with an in-project rustup toolchain so nothing
# is installed system-wide. The C guest is built with the zig compiler.
# Both non-.NET toolchains are pinned: the Rust channel in
# samples/rust-toolchain.toml (which RUST_TOOLCHAIN below reads) and the zig
# release in ZIG_VERSION (which require_zig checks before compiling).
# DOTNET prefers the workspace-local SDK under $(HOME)/.cache when present
# and falls back to PATH dotnet (which may be missing or SDK-less); it must
# never name a specific user.
DOTNET ?= $(shell test -x $(HOME)/.cache/dotnet-sdk/dotnet && echo $(HOME)/.cache/dotnet-sdk/dotnet || command -v dotnet 2>/dev/null || echo $(HOME)/.cache/dotnet-sdk/dotnet)
CARGO  ?= $(PWD)/.cargo/bin/cargo
ZIG    ?= $(shell command -v zig 2>/dev/null || echo zig)
# The tools gate is Python, and the interpreter is spelled differently per
# platform: Windows installs it as "python" and has no "python3" at all, so a
# hardcoded python3 breaks every target that runs tools/ (dist, check-ci).
PYTHON ?= $(shell command -v python3 2>/dev/null || command -v python 2>/dev/null || echo python3)
export RUSTUP_HOME := $(PWD)/.rustup
export CARGO_HOME := $(PWD)/.cargo

# Every dotnet target below builds with ContinuousIntegrationBuild on. The
# property is what makes the SDK normalize source paths (DeterministicSourcePaths)
# and honor SOURCE_DATE_EPOCH for embedded timestamps, so the same source
# produces the same assembly and the same .nupkg from /home/you and from the
# CI checkout. Directory.Build.props turns the same property on when the CI
# environment variable is set, which covers a GitHub Actions run; this line
# covers "make pack" on a developer machine, which otherwise ships the
# absolute checkout path inside the PDB and a wall-clock timestamp with it.
CIBUILD ?= -p:ContinuousIntegrationBuild=true

# The in-project toolchain is gitignored, so a fresh clone has no cargo at
# $(CARGO) and every guest target would fail with a bare "No such file or
# directory" from sh. These two guards turn that into a named instruction.
# The host suite runs on the committed fixtures without either tool, so the
# error says so rather than sending a new contributor looking for a tool
# they do not need yet.
define require_cargo
	@test -x "$(CARGO)" || { \
	  echo "make: the guest toolchain is missing ($(CARGO) not found)."; \
	  echo "  The Rust guests build with an in-project toolchain (.cargo/, .rustup/)"; \
	  echo "  so nothing is installed system-wide. Populate it with: make toolchain"; \
	  echo "  The host suite does not need it: 'make build' and 'make test' work as they are."; \
	  exit 1; }
endef
define require_zig
	@command -v $(ZIG) >/dev/null 2>&1 || { \
	  echo "make: zig not found on PATH ($(ZIG))."; \
	  echo "  The C and Zig guests are compiled with zig; install it from https://ziglang.org/download/"; \
	  echo "  or point the Makefile at it with: make ZIG=/path/to/zig <target>"; \
	  echo "  Every other target, including 'make test', runs without it."; \
	  exit 1; }
	@$(ZIG) version | grep -qx '$(ZIG_VERSION)' || { \
	  echo "make: zig $(ZIG_VERSION) builds the guests, but $(ZIG) reports $$( $(ZIG) version )."; \
	  echo "  Install the pinned release from https://ziglang.org/download/, or point the"; \
	  echo "  Makefile at another one with: make ZIG=/path/to/zig $(1)"; \
	  exit 1; }
endef
# ruff is the tools lint and format gate, pinned by required-version in
# pyproject.toml so a local run cannot drift from CI. Missing or mismatched is
# a setup problem, not a code problem, and says so before the gate runs.
define require_ruff
	@command -v ruff >/dev/null 2>&1 || { \
	  echo "make: ruff not found on PATH."; \
	  echo "  The tools gate needs ruff $(RUFF_VERSION) (the version the CI step installs)."; \
	  echo "  Install it into a project-local environment, e.g. uv: uv tool install ruff==$(RUFF_VERSION)"; \
	  echo "  'make build' and 'make test' do not need it."; \
	  exit 1; }
	@ruff --version | grep -q '$(RUFF_VERSION)' || { \
	  echo "make: ruff $(ruff --version | cut -d' ' -f2) is installed, but this gate is pinned to $(RUFF_VERSION)"; \
	  echo "  (pyproject.toml required-version, the version the CI step installs)."; \
	  echo "  Install the pinned one: uv tool install ruff==$(RUFF_VERSION)"; \
	  exit 1; }
endef

# NuGet restore mode for the dotnet targets below. Plain builds stay
# unlocked so dependency bumps regenerate packages.lock.json; "make
# check" flips this to true so a manifest that drifts from its
# committed lock file fails loudly instead of re-resolving packages.
export RESTORE_LOCKED ?= false

# Wasmtime runtime id of THIS machine, used by "make dist" to stage the
# matching native engine.
UNAME_S := $(shell uname -s 2>/dev/null || echo Windows_NT)
UNAME_M := $(shell uname -m 2>/dev/null)
ifeq ($(OS),Windows_NT)
  WASMTIME_OS := win
  # uname is missing from a plain Windows shell, so the architecture comes from
  # the environment there. Both variables matter: a 32-bit process on x64
  # reports the emulated one through PROCESSOR_ARCHITEW6432.
  WIN_ARCH := $(if $(PROCESSOR_ARCHITEW6432),$(PROCESSOR_ARCHITEW6432),$(PROCESSOR_ARCHITECTURE))
  UNAME_M := $(if $(filter ARM64,$(WIN_ARCH)),arm64,x64)
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

# Steam's library root, per platform family: Windows keeps games under
# Program Files (x86), Linux under the user's home. Both are defaults; every
# target below takes GAME_DIR=... on the command line.
ifeq ($(OS),Windows_NT)
  # GNU make cannot reference an environment variable whose name contains
  # parentheses, and Steam keeps games under "Program Files (x86)", so the
  # conventional root is spelled out here. A Steam on another drive is a
  # GAME_DIR=... on the command line.
  STEAM_ROOT ?= C:/Program Files (x86)/Steam/steamapps/common
else
  STEAM_ROOT ?= $(HOME)/.local/share/Steam/steamapps/common
endif

# Dedicated server install used for the net48 bridge build and target check.
GAME_DIR ?= $(STEAM_ROOT)/7 Days to Die Dedicated Server

# Sibling checkout holding the unmodified zdtd plugins (fps_bot, parachute)
# that are committed under tests/fixtures and staged by "make dist". It is a
# separate repository, so a clone of this one alone cannot rebuild them.
ZDTD_SERVER ?= $(abspath $(CURDIR)/../zdtd-server)
RUSTUP ?= rustup

# The ruff the tools gate is pinned to, read from pyproject.toml so the
# declaration lives in one place: the same file ruff reads, the same version
# the preflight below checks for, and the same one the CI install step gets
# from "make ruff-version".
# Recursively expanded (=) like WASMTIME_VERSION, so the read happens only in
# the targets that gate on it. tools/pinned.py parses each declaring file in
# its own format and fails by name when a version is missing, rather than the
# Makefile carrying a quoted regex that silently prints nothing.
RUFF_VERSION = $(shell $(PYTHON) tools/pinned.py ruff)

SLN = HordeForge.WasmHost.sln

# Wasmtime NuGet version as resolved into the committed lock file, so the
# staged native engine always matches the managed binding (single source
# of truth; bumping the PackageReference updates dist automatically).
# Recursively expanded (=, not :=) so the lock file is read only by the
# targets that stage the native engine: an eagerly evaluated $(shell) starts
# a python interpreter on every "make", including "make clean", and fails
# those targets outright when the lock file has not been restored yet.
WASMTIME_VERSION = $(shell $(PYTHON) tools/pinned.py wasmtime)

# The Rust channel the guests are built and linted with, read from
# samples/rust-toolchain.toml: the file rustup itself resolves when cargo runs
# inside samples/, so the pin and the install are the same declaration. It
# used to be the floating "stable", which lets a guest binary depend on the day
# the machine last synced its toolchain. Lazily expanded for the same reason
# as WASMTIME_VERSION above. Bump it by editing that file.
RUST_TOOLCHAIN = $(shell $(PYTHON) tools/pinned.py rust)

# The zig release the C and Zig guests compile against, as declared by
# require_zig below. The Zig guest is source that only builds on the pinned
# release, so a different zig has to fail by name instead of producing a
# module the host then rejects at load.
ZIG_VERSION := 0.16.0

.PHONY: help build test toolchain ruff-version samples samples-check boss boss-zig fixtures bridge bridge-check dist pack check check-ci clean

help:
	@echo "Targets:"
	@echo "  make build          build the host library and test suite (net8)"
	@echo "  make test           run the host test suite"
	@echo "  make test TEST_FILTER=<expr>"
	@echo "                      run only the tests matching a VSTest filter, e.g."
	@echo "                      TEST_FILTER='FullyQualifiedName~WasmModHostTests'"
	@echo "  make toolchain      populate the in-project rustup toolchain (.cargo/, .rustup/)"
	@echo "  make samples        compile guest mods and fixtures (wasm32-wasip1)"
	@echo "  make samples-check  guest lint gate (rustc + clippy denied)"
	@echo "  make boss           compile the C guest (samples/guest-boss) with zig"
	@echo "  make boss-zig       compile the Zig guest (samples/guest-boss-zig)"
	@echo "  make fixtures       rebuild fixtures and copy them into tests/fixtures"
	@echo "  make bridge         build the net48 in-game mod against GAME_DIR"
	@echo "  make bridge-check   validate game API targets against GAME_DIR"
	@echo "  make dist           assemble the modlet + sample guest under dist/"
	@echo "                      (also writes dist/SBOM.json from the lock files)"
	@echo "  make pack           pack the host library as a NuGet package under artifacts/packages/"
	@echo "  make check          everything check-ci runs, plus bridge and bridge-check"
	@echo "  make check-ci       the half of check that needs no game install (CI entry point)"
	@echo "  make clean          remove build output, samples/target, dist/ and artifacts/"
	@echo "  GAME_DIR=...        point bridge and bridge-check at a server install"
	@echo "  ZDTD_SERVER=...     zdtd-server checkout holding the plugins fixtures and dist copy"
	@echo
	@echo "The host library needs only a .NET 8 SDK and Python 3. The Rust guests"
	@echo "need 'make toolchain', the C and Zig guests need zig, and the fixtures"
	@echo "and dist targets need the ZDTD_SERVER checkout. 'make test' runs on the"
	@echo "committed fixtures and needs none of the three."

build:
	$(DOTNET) build $(SLN) -c Release -p:RestoreLockedMode=$(RESTORE_LOCKED) $(CIBUILD)

# The edit-test loop. TEST_FILTER is a VSTest filter expression, so a single
# test or class runs without touching anything else:
#   make test TEST_FILTER='FullyQualifiedName~WasmModHostTests'
#   make test TEST_FILTER='Name~FuelExhausted'
test:
	$(DOTNET) test tests/HordeForge.WasmHost.Tests -c Release -p:RestoreLockedMode=$(RESTORE_LOCKED) $(CIBUILD) $(if $(TEST_FILTER),--filter "$(TEST_FILTER)",)

# Populate the in-project rustup toolchain. RUSTUP_HOME and CARGO_HOME are
# exported at the top of this file, so this installs nothing system-wide and
# leaves no state outside the checkout. The commands are the ones CI runs
# against its own checkout, so a guest built locally and a guest built in CI
# come off the same toolchain with the same target and clippy. The channel is
# RUST_TOOLCHAIN, read from samples/rust-toolchain.toml, which is the same file
# rustup resolves when cargo runs inside samples/: installing a different
# release than the one cargo would pick is not possible.
toolchain:
	@command -v rustup >/dev/null 2>&1 || { \
	  echo "make: rustup not found on PATH."; \
	  echo "  Install it from https://rustup.rs (the toolchain itself lands in ./.rustup),"; \
	  echo "  then run 'make toolchain' again. Point at an existing one with RUSTUP=/path/to/rustup."; \
	  exit 1; }
	$(RUSTUP) toolchain install $(RUST_TOOLCHAIN) --profile minimal --target wasm32-wasip1 --component clippy
	$(RUSTUP) default $(RUST_TOOLCHAIN)
	@echo "Guest toolchain ready in $(CARGO_HOME) (nothing installed system-wide)."

# The pinned ruff version on stdout, for whoever has to install it. The CI
# step pipes this into its pip install, so the pin in pyproject.toml is the
# only place the version is written and a bump cannot miss an installer.
.PHONY: ruff-version
ruff-version:
	@echo $(RUFF_VERSION)

# Compile guests from inside samples/ on purpose: cargo discovers
# config by walking up from the current directory, and the workspace
# [lints] in samples/Cargo.toml deny every default rustc warning.
samples:
	$(call require_cargo)
	cd samples && $(CARGO) build --release --target wasm32-wasip1

# Guest lint gate: a plain build already fails on any default rustc
# warning (workspace [lints]); clippy then runs its default set at deny
# (workspace [lints] clippy all = "deny"). This keeps both gates in make
# check so guest code cannot regress silently between fixture rebuilds.
samples-check:
	$(call require_cargo)
	cd samples && $(CARGO) build --release --target wasm32-wasip1
	cd samples && $(CARGO) clippy --release --target wasm32-wasip1

# The C guest (samples/guest-boss) is compiled with zig to wasm32-wasi
# (preview 1). -nostdlib keeps it free of WASI libc imports; --max-memory
# declares the 32 MiB maximum the host requires.
boss:
	$(call require_zig,boss)
	mkdir -p samples/target
	$(ZIG) cc -target wasm32-wasi -O2 -nostdlib -Wl,--no-entry \
	  -Wl,--max-memory=33554432 -Wl,-z,stack-size=1048576 \
	  -o samples/target/guest-boss.wasm samples/guest-boss/guest-boss.c

# Zig guest (zig 0.16): -rdynamic keeps the @export'ed symbols in the
# -fno-entry module (without it everything is dead-code eliminated).
# Like the C guest, the module is emitted straight into samples/target/
# so no build artifact lands inside a guest source directory.
boss-zig:
	$(call require_zig,boss-zig)
	mkdir -p samples/target
	cd samples/guest-boss-zig && $(ZIG) build-exe src/main.zig \
	  -target wasm32-wasi -O ReleaseSmall -fno-entry -fstrip -rdynamic \
	  --max-memory=33554432 -femit-bin=../target/guest-boss-zig.wasm

fixtures: samples boss boss-zig
	@test -f "$(ZDTD_SERVER)/mods/fps_bot/fps_bot.wasm" -a -f "$(ZDTD_SERVER)/mods/parachute/parachute.wasm" || { \
	  echo "make: the unmodified zdtd plugins are missing under $(ZDTD_SERVER)/mods."; \
	  echo "  'make fixtures' and 'make dist' copy them in as real-world fixtures,"; \
	  echo "  so they need the zdtd-server checkout as a sibling of this repository."; \
	  echo "  Clone it next to this one, or point at it: make ZDTD_SERVER=/path/to/zdtd-server fixtures"; \
	  echo "  The committed fixtures under tests/fixtures are enough to run 'make test'."; \
	  exit 1; }
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
	cp $(ZDTD_SERVER)/mods/fps_bot/fps_bot.wasm                 tests/fixtures/fps-bot.wasm
	# The unmodified zdtd parachute mod (sense v4 + glide + config); its
	# config.toml is staged so the zdtd.config import test serves the real file.
	cp $(ZDTD_SERVER)/mods/parachute/parachute.wasm             tests/fixtures/parachute.wasm
	cp $(ZDTD_SERVER)/mods/parachute/config.toml                tests/fixtures/parachute-config.toml

bridge:
	$(DOTNET) build src/GameBridge/GameBridge.csproj -c Release -p:GAME_DIR="$(GAME_DIR)" -p:RestoreLockedMode=$(RESTORE_LOCKED) $(CIBUILD)

bridge-check:
	$(DOTNET) run -c Release --project tools/targetcheck -p:RestoreLockedMode=$(RESTORE_LOCKED) $(CIBUILD) -- "$(GAME_DIR)"

dist: build fixtures bridge
	@test -n "$(WASMTIME_VERSION)" || { \
	  echo "make: the pinned Wasmtime version is empty, so the native engine to stage is unknown."; \
	  echo "  Run: $(PYTHON) tools/pinned.py wasmtime   (it names the file it could not read)"; \
	  exit 1; }
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
	# Apache-2.0 redistribution requires the license and attribution to travel
	# with the binaries they cover (Wasmtime and the .NET Foundation closure).
	cp THIRD-PARTY-NOTICES.md dist/Mods/1_HordeForge_WasmHost/
	# Our own terms travel with them too: the modlet is MIT and the notices
	# file links to LICENSE, which is not in the tree once dist/ is zipped.
	cp LICENSE dist/Mods/1_HordeForge_WasmHost/
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
	cp $(ZDTD_SERVER)/mods/fps_bot/fps_bot.wasm dist/Mods/Wasm/fps-bot/module.wasm
	cp samples/zdtd-fps-bot/wasm-mod.toml dist/Mods/Wasm/fps-bot/
	# The unmodified zdtd parachute mod: module + its own config.toml (served
	# to the guest verbatim via the zdtd.config import). Needs the same raised
	# memory cap; deploy tuning lives in config.toml, not the manifest.
	mkdir -p dist/Mods/Wasm/parachute
	cp $(ZDTD_SERVER)/mods/parachute/parachute.wasm dist/Mods/Wasm/parachute/module.wasm
	cp $(ZDTD_SERVER)/mods/parachute/config.toml dist/Mods/Wasm/parachute/
	cp samples/parachute/wasm-mod.toml dist/Mods/Wasm/parachute/
	cp samples/wasm.toml.example dist/Mods/Wasm/wasm.toml
	# SBOM: CycloneDX inventory built from the committed lock files, so
	# consumers and vuln scanners know exactly what shipped.
	$(PYTHON) tools/sbom.py --root . -o dist/SBOM.json
	@echo "Dist staged under dist/ (copy dist/Mods into the dedicated server's Mods/ folder)"

# The publishable library package, the artifact a consumer installs with a
# package manager. Built here rather than only on release so a manifest that
# no longer packs (a readme that moved, a package path that no longer
# resolves) fails on the same gate as everything else. "make dist" assembles
# the game modlet instead; it needs a server install, this does not.
#
# ContinuousIntegrationBuild makes the payload reproducible: two packs of the
# same source produce byte-identical DLLs, XML docs, README and THIRD-PARTY
# NOTICES. The archive around them is not, and NuGet gives no switch for it:
# package/services/metadata/core-properties carries a build-time GUID in its
# part name and a random relationship id, and the OPC parts carry the current
# time as their zip entry date. Compare the extracted package, not its hash.
PACKAGE_OUT := artifacts/packages

pack:
	@$(PYTHON) tools/packcheck.py
	$(DOTNET) pack src/HordeForge.WasmHost/HordeForge.WasmHost.csproj -c Release -p:RestoreLockedMode=$(RESTORE_LOCKED) $(CIBUILD) -o $(PACKAGE_OUT)
	@echo "NuGet package staged under $(PACKAGE_OUT)/"

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
	$(PYTHON) tools/doccheck.py
	$(PYTHON) tools/versioncheck.py
	$(PYTHON) tools/packcheck.py
	$(PYTHON) tools/apicheck.py
	$(PYTHON) -m unittest discover -s tools
	$(call require_ruff)
	ruff check tools
	ruff format --check tools
	$(MAKE) samples-check
	$(MAKE) build
	$(MAKE) test
	$(MAKE) pack

clean:
	rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj tools/targetcheck/bin tools/targetcheck/obj samples/target dist artifacts
