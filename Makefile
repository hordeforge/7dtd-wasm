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
# CURDIR is the make builtin, computed by make itself from the directory it
# was started in. PWD is only a shell convention that a shell which does not
# export it leaves empty, which would silently move the toolchain lookups and
# the cargo home to /.
DOTNET ?= $(shell test -x "$(HOME)/.cache/dotnet-sdk/dotnet" && echo "$(HOME)/.cache/dotnet-sdk/dotnet" || command -v dotnet 2>/dev/null || echo "$(HOME)/.cache/dotnet-sdk/dotnet")
CARGO  ?= $(CURDIR)/.cargo/bin/cargo
ZIG    ?= $(shell command -v zig 2>/dev/null || echo zig)
# The tools gate is Python, and the interpreter is spelled differently per
# platform: Windows installs it as "python" and has no "python3" at all, so a
# hardcoded python3 breaks every target that runs tools/ (dist, check-ci).
PYTHON ?= $(shell command -v python3 2>/dev/null || command -v python 2>/dev/null || echo python3)
export RUSTUP_HOME := $(CURDIR)/.rustup
export CARGO_HOME := $(CURDIR)/.cargo
# An ambient RUSTFLAGS in the environment silently replaces the
# [target.wasm32-wasip1] rustflags in samples/.cargo/config.toml (cargo
# prefers the environment over config, and CARGO_ENCODED_RUSTFLAGS over
# both). That config is where the guest memory maximum and stack size are
# declared, so a machine exporting either variable builds modules the host
# refuses to load at the default cap, and two machines produce different
# wasm from the same source. Unexporting both leaves the checked-in config
# the one declaration, in a checkout and on a CI runner alike.
unexport RUSTFLAGS
unexport CARGO_ENCODED_RUSTFLAGS

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
# Program Files (x86), Linux under the user's home, macOS under Application
# Support. All are defaults; every target below takes GAME_DIR=... on the
# command line.
ifeq ($(OS),Windows_NT)
  # GNU make cannot reference an environment variable whose name contains
  # parentheses, and Steam keeps games under "Program Files (x86)", so the
  # conventional root is spelled out here. A Steam on another drive is a
  # GAME_DIR=... on the command line.
  STEAM_ROOT ?= C:/Program Files (x86)/Steam/steamapps/common
else ifeq ($(UNAME_S),Darwin)
  # macOS has no .local/share; Steam keeps its libraries under Application
  # Support, so a Linux default here points at a path that never exists.
  STEAM_ROOT ?= $(HOME)/Library/Application Support/Steam/steamapps/common
else
  STEAM_ROOT ?= $(HOME)/.local/share/Steam/steamapps/common
endif

# Global packages folder the Wasmtime native engine is restored into. "make
# dist" stages that engine by path, and the folder is not always $(HOME)/.nuget:
# NUGET_PACKAGES moves it, and the workspace-local SDK this Makefile prefers
# has its own. Asking the SDK that ran the build is the one source that cannot
# disagree with where restore actually put the package. Lazily expanded, so the
# query costs nothing on the targets that never stage the engine.
NUGET_GLOBAL_PACKAGES = $(shell $(DOTNET) nuget locals global-packages --list 2>/dev/null | sed 's/^[^:]*:[[:space:]]*//')

# Dedicated server install used for the net48 bridge build and target check.
GAME_DIR ?= $(STEAM_ROOT)/7 Days to Die Dedicated Server

# Where the NuGet global packages folder is, for the one target that reads a
# restored package out of it ("make dist" stages the native engine from the
# Wasmtime package). NUGET_PACKAGES is the supported override and is set by
# CI systems, shared build caches, and anyone who keeps packages off the
# home disk, so hardcoding the default would break those restores silently:
# the copy would look for a file dotnet never wrote there.
NUGET_PACKAGES ?= $(HOME)/.nuget/packages

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

.PHONY: help build test test-list test-tools tools-check toolchain ruff-version samples samples-check boss boss-zig fixtures bridge bridge-check dist pack locks check check-ci clean

help:
	@echo "Targets:"
	@echo "  make build          build the host library and test suite (net8)"
	@echo "  make test           run the host test suite"
	@echo "  make test TEST_FILTER=<expr>"
	@echo "                      run only the tests matching a VSTest filter, e.g."
	@echo "                      TEST_FILTER='FullyQualifiedName~WasmModHostTests'"
	@echo "                      (a filter that matches nothing fails, it does not pass)"
	@echo "  make test-list      print every test name a TEST_FILTER can match"
	@echo "  make test-tools     run the Python tools unit tests (fast: no .NET, no Rust)"
	@echo "  make test-tools TOOLS_PATTERN='test_doccheck.py'"
	@echo "                      run only the tools test file(s) matching a glob"
	@echo "  make tools-check    the tools half of check-ci: the four Python gates,"
	@echo "                      the tools unit tests, and the ruff lint and format gates"
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
	@echo "  make locks          regenerate every packages.lock.json after a dependency bump"
	@echo "  make check          everything check-ci runs, plus bridge and bridge-check"
	@echo "  make check-ci       the half of check that needs no game install (CI entry point)"
	@echo "  make clean          remove build output, samples/target, dist/ and artifacts/"
	@echo "  make ruff-version   print the pinned ruff release on stdout (CI installs it)"
	@echo "  GAME_DIR=...        point bridge and bridge-check at a server install"
	@echo "  ZDTD_SERVER=...     zdtd-server checkout holding the plugins fixtures and dist copy"
	@echo
	@echo "The host library needs only a .NET 8 SDK and Python 3. The Rust guests"
	@echo "need 'make toolchain', the C and Zig guests need zig, and the fixtures"
	@echo "and dist targets need the ZDTD_SERVER checkout. 'make test' runs on the"
	@echo "committed fixtures and needs none of the three."

build:
	$(DOTNET) build $(SLN) -c Release -p:RestoreLockedMode=$(RESTORE_LOCKED) $(CIBUILD)

# Where the test run's output is staged so the no-match check below can read
# it. .scratch/ is gitignored; "make clean" removes the log, not the rest of
# the directory, so a developer's other scratch survives a rebuild.
TEST_LOG ?= $(CURDIR)/.scratch/test.log

# The edit-test loop. TEST_FILTER is a VSTest filter expression over
# DisplayName (for xunit, the fully qualified test name), so a single test or
# class runs without touching anything else:
#   make test TEST_FILTER='FullyQualifiedName~WasmModHostTests'
#   make test TEST_FILTER='DisplayName~FuelBudget'
#   make test-list     every test name, for building a filter
# A filter that matches nothing is a typo, not a pass: vstest prints "No test
# matches the given testcase filter" and still exits 0, so a mistyped filter
# would report green for a run that tested nothing. The check below turns that
# into a named failure.
test:
	@mkdir -p "$(dir $(TEST_LOG))"
	@$(DOTNET) test tests/HordeForge.WasmHost.Tests -c Release -p:RestoreLockedMode=$(RESTORE_LOCKED) $(CIBUILD) $(if $(TEST_FILTER),--filter "$(TEST_FILTER)",) > "$(TEST_LOG)" 2>&1; \
	  rc=$$?; cat "$(TEST_LOG)"; \
	  if grep -q 'No test matches the given testcase filter' "$(TEST_LOG)"; then \
	    echo "make: TEST_FILTER='$(TEST_FILTER)' matched no test."; \
	    echo "  A VSTest filter that matches nothing still exits 0, so this would"; \
	    echo "  otherwise read as a passing run. Every test name: make test-list"; \
	    exit 2; \
	  fi; \
	  exit $$rc

# The names a TEST_FILTER can match, so the filter above is not guesswork.
test-list:
	$(DOTNET) test tests/HordeForge.WasmHost.Tests -c Release -p:RestoreLockedMode=$(RESTORE_LOCKED) $(CIBUILD) --list-tests

# The tools unit tests, and the fast loop for a change under tools/. The C#
# suite is the slow one here; this runs in under a second and needs no .NET
# SDK, no Rust toolchain and no game install, so it is the gate to run while
# editing a tools/*.py file. TOOLS_PATTERN is the same glob unittest's
# discover takes, so one file is one invocation:
#   make test-tools
#   make test-tools TOOLS_PATTERN='test_doccheck.py'
# A pattern that matches no file runs zero tests, which unittest reports as
# "NO TESTS RAN" and exits 5, so a mistyped glob fails instead of passing.
TOOLS_PATTERN ?= 'test_*.py'

test-tools:
	$(PYTHON) -m unittest discover -s tools -p $(TOOLS_PATTERN)

# Everything check-ci runs against tools/ and nothing else: the four Python
# gates, their unit tests, and the ruff lint and format checks. check-ci calls
# this target, so the two cannot drift apart.
tools-check:
	$(PYTHON) tools/doccheck.py
	$(PYTHON) tools/versioncheck.py
	$(PYTHON) tools/packcheck.py
	$(PYTHON) tools/apicheck.py
	$(MAKE) test-tools
	$(call require_ruff)
	ruff check tools
	ruff format --check tools

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
#
# --locked on every cargo call, for the same reason the dotnet targets
# pass RestoreLockedMode: samples/Cargo.lock is committed, and without the
# flag cargo treats it as a cache it is free to rewrite. A Cargo.toml that
# gains a dependency without a matching lock entry then fails the build by
# name, instead of cargo silently re-resolving and rewriting the lock file
# under the tree, which is how a guest build starts depending on whatever
# the registry offered that day. The workspace has no third-party crates
# today, so the lock currently pins path edges only, and --locked turns the
# first external dependency into a committed-and-reviewed decision instead
# of a silent re-resolution.
samples:
	$(call require_cargo)
	cd samples && $(CARGO) build --locked --release --target wasm32-wasip1

# Guest lint gate: a plain build already fails on any default rustc
# warning (workspace [lints]); clippy then runs its default set at deny
# (workspace [lints] clippy all = "deny"). This keeps both gates in make
# check so guest code cannot regress silently between fixture rebuilds.
samples-check:
	$(call require_cargo)
	cd samples && $(CARGO) build --locked --release --target wasm32-wasip1
	cd samples && $(CARGO) clippy --locked --release --target wasm32-wasip1

# The C guest (samples/guest-boss) is compiled with zig to wasm32-wasi
# (preview 1). -nostdlib keeps it free of WASI libc imports; --max-memory
# declares the 32 MiB maximum the host requires.
#
# The warning set is the guest's lint gate, and it is the same posture the
# other two languages get: rustc warnings and clippy are denied in
# samples/Cargo.toml, and every diagnostic is an error here. Without it a
# C guest compiled with no warning flags at all is a source of bugs no
# other tool in this repository would report. -Wmissing-prototypes is
# deliberately absent: the exported on_* functions have no declaration to
# be missing, since the wasm export is their only entry point.
boss: C_WARNINGS = -Wall -Wextra -Wpedantic -Werror -Wshadow -Wundef -Wcast-qual -Wstrict-prototypes
boss:
	$(call require_zig,boss)
	mkdir -p samples/target
	$(ZIG) cc -target wasm32-wasi -O2 -nostdlib $(C_WARNINGS) -Wl,--no-entry \
	  -Wl,--max-memory=33554432 -Wl,-z,stack-size=1048576 \
	  -o samples/target/guest-boss.wasm samples/guest-boss/guest-boss.c

# Zig guest (zig 0.16): -rdynamic keeps the @export'ed symbols in the
# -fno-entry module (without it everything is dead-code eliminated).
# Like the C guest, the module is emitted straight into samples/target/
# so no build artifact lands inside a guest source directory.
#
# "zig fmt --check" is the Zig half of the tree's format gate: ruff format
# runs for tools/ and rustfmt is the guest default, so a Zig guest was the
# one source directory whose formatting nothing checked. The pinned zig
# ships the formatter, so this adds no second tool to install.
boss-zig:
	$(call require_zig,boss-zig)
	$(ZIG) fmt --check samples/guest-boss-zig/
	mkdir -p samples/target
	cd samples/guest-boss-zig && $(ZIG) build-exe src/main.zig \
	  -target wasm32-wasi -O ReleaseSmall -fno-entry -fstrip -rdynamic \
	  --max-memory=33554432 -femit-bin=../target/guest-boss-zig.wasm

fixtures: samples boss boss-zig
	@test -f "$(ZDTD_SERVER)/mods/fps_bot/fps_bot.wasm" && test -f "$(ZDTD_SERVER)/mods/parachute/parachute.wasm" || { \
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
	cp "$(ZDTD_SERVER)/mods/fps_bot/fps_bot.wasm"                 tests/fixtures/fps-bot.wasm
	# The unmodified zdtd parachute mod (sense v4 + glide + config); its
	# config.toml is staged so the zdtd.config import test serves the real file.
	cp "$(ZDTD_SERVER)/mods/parachute/parachute.wasm"             tests/fixtures/parachute.wasm
	cp "$(ZDTD_SERVER)/mods/parachute/config.toml"                tests/fixtures/parachute-config.toml

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
	# Native engine for this platform ($(WASMTIME_RID), see header), from the
	# global packages folder the SDK that ran the build actually used. An
	# unresolvable folder would otherwise surface as a bare "cp: cannot stat"
	# naming a relative path, so it is named here instead.
	@test -n "$(NUGET_GLOBAL_PACKAGES)" && test -d "$(NUGET_GLOBAL_PACKAGES)/wasmtime/$(WASMTIME_VERSION)" || { \
	  echo "make: the Wasmtime package is not under the SDK's global packages folder"; \
	  echo "  ($(NUGET_GLOBAL_PACKAGES), queried from $(DOTNET))."; \
	  echo "  Run 'make build' first so restore places it, and check NUGET_PACKAGES"; \
	  echo "  if it points somewhere this build does not use."; \
	  exit 1; }
	cp "$(NUGET_GLOBAL_PACKAGES)/wasmtime/$(WASMTIME_VERSION)/runtimes/$(WASMTIME_RID)/native/$(WASMTIME_NATIVE)" dist/Mods/1_HordeForge_WasmHost/Native/
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
	cp "$(ZDTD_SERVER)/mods/fps_bot/fps_bot.wasm" dist/Mods/Wasm/fps-bot/module.wasm
	cp samples/zdtd-fps-bot/wasm-mod.toml dist/Mods/Wasm/fps-bot/
	# The unmodified zdtd parachute mod: module + its own config.toml (served
	# to the guest verbatim via the zdtd.config import). Needs the same raised
	# memory cap; deploy tuning lives in config.toml, not the manifest.
	mkdir -p dist/Mods/Wasm/parachute
	cp "$(ZDTD_SERVER)/mods/parachute/parachute.wasm" dist/Mods/Wasm/parachute/module.wasm
	cp "$(ZDTD_SERVER)/mods/parachute/config.toml" dist/Mods/Wasm/parachute/
	cp samples/parachute/wasm-mod.toml dist/Mods/Wasm/parachute/
	cp samples/wasm.toml.example dist/Mods/Wasm/wasm.toml
	# SBOM: CycloneDX inventory built from the committed lock files, so
	# consumers and vuln scanners know exactly what shipped.
	$(PYTHON) tools/sbom.py --root . --output dist/SBOM.json
	@echo "Dist staged under dist/ (copy dist/Mods into the dedicated server's Mods/ folder)"
	@echo "  Copying over an installed Mods/ replaces the files the tree carries,"
	@echo "  Mods/Wasm/<id>/config.toml among them. Keep operator edits elsewhere first."

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

# Regenerate every committed packages.lock.json after a dependency change.
# "make check" restores locked, so a manifest that moved without its lock file
# fails there; this is the other half. It restores each project individually
# rather than through $(SLN), because the solution covers only the host library
# and its tests: GameBridge and targetcheck have committed lock files too, and
# a solution-level restore silently leaves both stale.
LOCK_PROJECTS = \
  src/HordeForge.WasmHost/HordeForge.WasmHost.csproj \
  tests/HordeForge.WasmHost.Tests/HordeForge.WasmHost.Tests.csproj \
  tools/targetcheck/targetcheck.csproj \
  src/GameBridge/GameBridge.csproj

locks:
	@for project in $(LOCK_PROJECTS); do \
	  echo "restoring $$project"; \
	  $(DOTNET) restore "$$project" --force-evaluate || exit 1; \
	done
	@echo "Lock files refreshed. Commit the diff alongside the manifest change."

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
	$(MAKE) tools-check
	$(call require_ruff)
	ruff check evidence
	ruff format --check evidence
	$(MAKE) samples-check
	$(MAKE) build
	$(MAKE) test
	$(MAKE) pack

clean:
	rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj tools/targetcheck/bin tools/targetcheck/obj samples/target dist artifacts .scratch/test.log
