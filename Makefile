# PROSPECT task runner. `make` or `make help` lists the targets.
# Works with GNU Make 3.81 (the macOS default) and later.

.DEFAULT_GOAL := help

# ---------------------------------------------------------------------------------------------------------------------
# Settings (override on the command line: make run CONFIG=Release ARGS="--start local")
# ---------------------------------------------------------------------------------------------------------------------

CONFIG   ?= Debug
ARGS     ?=
V        ?=
# xUnit options, for example TEST_ARGS="-parallel none" for timing-sensitive integration checks.
TEST_ARGS ?=

SOLUTION := Prospect.slnx
CLIENT   := src/Prospect.Client
SERVER   := src/Prospect.Server
LISTING  := src/Prospect.Listing
TESTS    := tests/Prospect.Tests
LTESTS   := tests/Prospect.Listing.Tests
LISTING_PORT  ?= 8080
LISTING_IMAGE ?= prospect-listing
SERVER_IMAGE  ?= prospect-server
BIN      := artifacts/bin
TOOLS    := $(CURDIR)/.tools
# Build output folders are lower-case configuration names: artifacts/bin/<project>/<config>/.
config   := $(shell echo $(CONFIG) | tr A-Z a-z)

# V=1 echoes every command and lets dotnet talk.
ifeq ($(V),)
  Q     := @
  QUIET := --nologo -v q
else
  Q     :=
  QUIET := --nologo
endif

# ---------------------------------------------------------------------------------------------------------------------
# Colours (off with NO_COLOR=1 or TERM=dumb)
# ---------------------------------------------------------------------------------------------------------------------

ifeq ($(NO_COLOR)$(filter dumb,$(TERM)),)
  ESC    := $(shell printf '\033')
  BOLD   := $(ESC)[1m
  DIM    := $(ESC)[2m
  RED    := $(ESC)[31m
  GREEN  := $(ESC)[32m
  YELLOW := $(ESC)[33m
  BLUE   := $(ESC)[34m
  CYAN   := $(ESC)[36m
  RESET  := $(ESC)[0m
endif

# Suppress the whole launch argument list when it contains private text: quoted value boundaries cannot
# safely be recovered here. Run recipes remain silent even with V=1. Prefer PROSPECT_PASSWORD for passwords.
# As with any recipe, make -n and make --trace can still expose the actual command.
PRIVATE_ARGS = $(or $(findstring --password,$(ARGS)),$(findstring --say,$(ARGS)),$(findstring --party-say,$(ARGS)))
SHOWN_ARGS = $(if $(PRIVATE_ARGS),(arguments not shown: they contain private text),$(ARGS))

# Recipe helpers. `step` is a whole recipe line; `ok`, `warn`, `fail` and `note` are shell snippets.
step = @printf '%s\n' "$(BLUE)==>$(RESET) $(BOLD)$(1)$(RESET)"
ok   = printf '  %s %s\n' "$(GREEN)✓$(RESET)" "$(1)"
warn = printf '  %s %s\n' "$(YELLOW)!$(RESET)" "$(1)"
fail = printf '  %s %s\n' "$(RED)✗$(RESET)" "$(1)"
note = printf '    %s\n' "$(DIM)$(1)$(RESET)"

# ---------------------------------------------------------------------------------------------------------------------
# Platform
# ---------------------------------------------------------------------------------------------------------------------

ifeq ($(OS),Windows_NT)
  PLATFORM := windows
  ARCH     := $(if $(filter ARM64,$(PROCESSOR_ARCHITECTURE)),arm64,x86_64)
else
  UNAME_S  := $(shell uname -s)
  ARCH     := $(shell uname -m | sed -e 's/aarch64/arm64/' -e 's/amd64/x86_64/')
  ifeq ($(UNAME_S),Darwin)
    PLATFORM    := darwin
    BREW        := $(shell command -v brew 2>/dev/null)
    BREW_PREFIX := $(if $(BREW),$(shell $(BREW) --prefix))
  else ifeq ($(UNAME_S),Linux)
    PLATFORM := linux
    DISTRO   := $(shell . /etc/os-release 2>/dev/null && echo $$ID)
    PKG      := $(firstword $(foreach p,apt-get dnf pacman zypper,$(if $(shell command -v $(p) 2>/dev/null),$(p))))
  else
    PLATFORM := $(shell echo $(UNAME_S) | tr A-Z a-z)
  endif
endif
HOST := $(PLATFORM)-$(ARCH)

# BRUTAL ships native libraries for these; Apple Silicon builds for arm64 (Directory.Build.props) and gets Vulkan from
# MoltenVK. Elsewhere building, formatting and the tests without networking work, but the game cannot start.
SUPPORTED := linux-x86_64 windows-x86_64 darwin-arm64

SUDO := $(if $(filter 0,$(shell id -u 2>/dev/null)),,sudo)

# System packages per Linux package manager: curl for the installers, ICU for the SDK, the Vulkan loader, vulkaninfo
# and the validation layers. Arch splits ASP.NET Core (the listing server) out of its .NET packages. macOS gets the loader, MoltenVK, vulkaninfo and the validation layers from Homebrew.
BREW_PKGS       := vulkan-loader molten-vk vulkan-tools vulkan-validationlayers
PKGS_apt-get    := curl ca-certificates libicu-dev libvulkan1 vulkan-tools vulkan-validationlayers
PKGS_dnf        := curl ca-certificates libicu vulkan-loader vulkan-tools vulkan-validation-layers
PKGS_pacman     := curl ca-certificates icu vulkan-icd-loader vulkan-tools vulkan-validation-layers aspnet-runtime aspnet-targeting-pack
PKGS_zypper     := curl ca-certificates libicu-devel libvulkan1 vulkan-tools vulkan-validationlayers
INSTALL_apt-get := $(SUDO) apt-get update -qq && $(SUDO) apt-get install -y
INSTALL_dnf     := $(SUDO) dnf install -y
INSTALL_pacman  := $(SUDO) pacman -S --needed --noconfirm
INSTALL_zypper  := $(SUDO) zypper --non-interactive install

# ---------------------------------------------------------------------------------------------------------------------
# .NET SDK: the channel comes from global.json. `make setup` installs it into DOTNET_HOME without root.
# ---------------------------------------------------------------------------------------------------------------------

DOTNET_CHANNEL := $(shell sed -nE 's/.*"version" *: *"([0-9]+\.[0-9]+)\..*/\1/p' global.json)

DOTNET_HOME    ?= $(HOME)/.dotnet
DOTNET_SEARCH  := $(DOTNET_HOME)/dotnet $$(command -v dotnet)

# The first dotnet that has an SDK matching global.json, unless DOTNET is set.
ifeq ($(origin DOTNET),undefined)
  DOTNET := $(shell for d in $(DOTNET_SEARCH); do \
              [ -x "$$d" ] && "$$d" --version >/dev/null 2>&1 && { echo "$$d"; exit 0; }; done)
endif
ifneq ($(filter $(DOTNET_HOME)/%,$(DOTNET)),)
  export DOTNET_ROOT := $(DOTNET_HOME)
  export PATH        := $(DOTNET_HOME):$(PATH)
endif
export DOTNET_NOLOGO := 1

# The allocation tests read how much the current thread has allocated. A collection that falls inside a measured
# stretch counts the unused rest of that thread's allocation buffer against it: up to 8 KiB the test never took.
# The tests run side by side and collect often, so a full run failed that way about one time in two. A gen-0
# budget of 256 MiB keeps collections out of those stretches.
TEST_ENV := DOTNET_GCgen0size=0x10000000 DOTNET_GCgen0MaxBudget=0x10000000

# ---------------------------------------------------------------------------------------------------------------------
# Vulkan validation layers fetched by `make tools` (for machines without a system-wide install).
# ---------------------------------------------------------------------------------------------------------------------

# Homebrew's validation layer manifest names its library without a path, and dlopen does not search Homebrew's lib/.
# Set per command: macOS strips DYLD_* variables before they reach the recipe shell.
ifneq ($(BREW_PREFIX),)
  RUN_ENV := DYLD_FALLBACK_LIBRARY_PATH=$(BREW_PREFIX)/lib:/usr/local/lib:/usr/lib
endif

VVL := $(TOOLS)/vvl
ifneq ($(wildcard $(VVL)/usr/lib/libVkLayer_khronos_validation.so),)
  export VK_ADD_LAYER_PATH := $(VVL)/usr/share/vulkan/explicit_layer.d
  export LD_LIBRARY_PATH   := $(VVL)/usr/lib:$(LD_LIBRARY_PATH)
endif

.PHONY: help setup setup-system setup-dotnet restore doctor tools \
        build run server listing listing-image test bench fmt check clean need-dotnet

##@ Getting started

help: ## Show this help
	@printf '\n%s\n\n' "$(BOLD)PROSPECT$(RESET) $(DIM)— $(HOST), .NET $(DOTNET_CHANNEL), $(CONFIG)$(RESET)"
	@printf '%s\n' "Usage: $(CYAN)make$(RESET) $(BOLD)<target>$(RESET) $(DIM)[CONFIG=Debug|Release] [ARGS=\"...\"] [V=1]$(RESET)"
	@awk 'BEGIN { FS = ":.*## " } \
	     /^##@/ { printf "\n$(BOLD)%s$(RESET)\n", substr($$0, 5) } \
	     /^[a-z-]+:.*## / { printf "  $(CYAN)%-14s$(RESET) %s\n", $$1, $$2 }' $(MAKEFILE_LIST)
	@printf '\n'

setup: setup-system setup-dotnet ## Install everything needed to build and run (.NET SDK, Vulkan) and restore packages
	@$(MAKE) --no-print-directory restore
	@printf '\n%s\n' "$(GREEN)$(BOLD)Ready.$(RESET) Try $(CYAN)make run$(RESET), or $(CYAN)make doctor$(RESET) to check the setup."

setup-system: ## Install system packages for this OS (Vulkan loader and layers; MoltenVK on macOS)
	$(call step,System packages for $(HOST))
ifeq ($(PLATFORM),linux)
  ifeq ($(PKG),)
	@$(call warn,No supported package manager (apt-get / dnf / pacman / zypper) found on '$(DISTRO)'.)
	@$(call note,Install curl / ICU / the Vulkan loader and validation layers yourself.)
  else
	@$(call note,$(PKG): $(PKGS_$(PKG)))
	$(Q)$(INSTALL_$(PKG)) $(PKGS_$(PKG))
	@$(call ok,System packages installed)
  endif
else ifeq ($(PLATFORM),darwin)
	@xcode-select -p >/dev/null 2>&1 && $(call ok,Xcode command line tools) || xcode-select --install
  ifeq ($(BREW),)
	@$(call fail,Homebrew not found: install it from https://brew.sh and run make setup again) && exit 1
  else
	@$(call note,brew: $(BREW_PKGS))
	$(Q)$(BREW) install --quiet $(BREW_PKGS)
	@$(call ok,Vulkan (MoltenVK) installed)
  endif
else ifeq ($(PLATFORM),windows)
	@$(call ok,Nothing to install: Vulkan ships with the GPU driver)
else
	@$(call warn,Unknown platform '$(PLATFORM)'; skipping system packages)
endif
ifeq ($(filter $(HOST),$(SUPPORTED)),)
	@$(call warn,$(HOST) is not a runtime target: build / fmt and the tests without networking work.)
	@$(call note,The game and the server run on Linux x64 / Windows x64 / Apple Silicon.)
endif

setup-dotnet: ## Install the .NET SDK named in global.json (FORCE=1 to reinstall)
	$(call step,.NET $(DOTNET_CHANNEL) SDK)
ifeq ($(PLATFORM),windows)
	@if [ -n "$(DOTNET)" ] && [ -z "$(FORCE)" ]; then $(call ok,$$("$(DOTNET)" --version) at $(DOTNET)); \
	 else winget install --exact --id Microsoft.DotNet.SDK.$(firstword $(subst ., ,$(DOTNET_CHANNEL))); fi
else
	@if [ -n "$(DOTNET)" ] && [ -z "$(FORCE)" ]; then \
	   $(call ok,$$("$(DOTNET)" --version) at $(DOTNET)); \
	 else \
	   mkdir -p "$(TOOLS)" && \
	   curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$(TOOLS)/dotnet-install.sh" && \
	   bash "$(TOOLS)/dotnet-install.sh" --channel $(DOTNET_CHANNEL) --install-dir "$(DOTNET_HOME)" --no-path && \
	   $(call ok,$$("$(DOTNET_HOME)/dotnet" --version) installed in $(DOTNET_HOME)) && \
	   $(call note,The Makefile finds it there. For your shell and IDE add to your profile:) && \
	   $(call note,  export DOTNET_ROOT=$(DOTNET_HOME) PATH=$(DOTNET_HOME):\$$PATH); \
	 fi
endif

restore: need-dotnet ## Restore NuGet packages
	$(call step,Restoring packages)
	$(Q)"$(DOTNET)" restore $(SOLUTION) $(QUIET)
	@$(call ok,Packages restored)

doctor: ## Check the toolchain, GPU driver and validation layers
	$(call step,Environment)
	@$(call ok,Platform $(HOST)$(if $(DISTRO), ($(DISTRO))))
	@$(if $(filter $(HOST),$(SUPPORTED)),$(call ok,Runtime target),$(call warn,Not a runtime target: the game runs on Linux x64 / Windows x64 / Apple Silicon))
	@$(call ok,$(shell $(MAKE) --version | head -1))
	@if [ -n "$(DOTNET)" ]; then $(call ok,.NET SDK $$("$(DOTNET)" --version) at $(DOTNET)); \
	 else $(call fail,No .NET $(DOTNET_CHANNEL) SDK found); $(call note,Run: make setup-dotnet); exit 1; fi
	@if "$(DOTNET)" --list-runtimes | grep -q '^Microsoft.AspNetCore.App $(DOTNET_CHANNEL)'; then $(call ok,ASP.NET Core runtime (listing server)); \
	 else $(call fail,No ASP.NET Core $(DOTNET_CHANNEL) runtime: the listing server and its tests cannot build or run); \
	   $(call note,Run: make setup-system (Arch: aspnet-runtime aspnet-targeting-pack)); fi
ifneq ($(filter linux darwin,$(PLATFORM)),)
  ifeq ($(PLATFORM),linux)
	@if ldconfig -p 2>/dev/null | grep -q 'libvulkan.so.1'; then $(call ok,Vulkan loader); \
	 else $(call fail,Vulkan loader (libvulkan.so.1) missing); $(call note,Run: make setup-system); fi
  else
	@if [ -e "$(BREW_PREFIX)/lib/libvulkan.1.dylib" ]; then $(call ok,Vulkan loader); \
	 else $(call fail,Vulkan loader (libvulkan.1.dylib) missing); $(call note,Run: make setup-system); fi
	@if [ -e "$(BREW_PREFIX)/lib/libMoltenVK.dylib" ]; then $(call ok,MoltenVK); \
	 else $(call fail,MoltenVK missing); $(call note,Run: make setup-system); fi
  endif
	@if command -v vulkaninfo >/dev/null 2>&1; then \
	   gpu=$$(vulkaninfo --summary 2>/dev/null | sed -nE 's/^[[:space:]]*deviceName[[:space:]]*= //p' | head -1); \
	   api=$$(vulkaninfo --summary 2>/dev/null | sed -nE 's/^[[:space:]]*apiVersion[[:space:]]*= ([0-9.]+).*/\1/p' | head -1); \
	   if [ -n "$$gpu" ]; then $(call ok,GPU $$gpu (Vulkan $$api)); else $(call fail,No Vulkan device: check the GPU driver); fi; \
	 else $(call warn,vulkaninfo not installed; cannot check the GPU); fi
	@found=$$VK_ADD_LAYER_PATH; \
	 for d in /usr /usr/local $(BREW_PREFIX); do \
	   [ -e "$$d/share/vulkan/explicit_layer.d/VkLayer_khronos_validation.json" ] && found=1; done; \
	 if [ -n "$$found" ]; then \
	   $(call ok,Vulkan validation layers); \
	 else $(call warn,No Vulkan validation layers (optional; debug builds run unvalidated)); $(call note,Run: make setup-system or make tools); fi
endif

tools: ## Fetch Vulkan validation layers into .tools/ without root (x86-64 Linux)
ifneq ($(HOST),linux-x86_64)
	@$(call fail,make tools fetches x86-64 Linux binaries; on $(HOST) install the Vulkan SDK instead) && exit 1
else
	$(call step,Fetching Vulkan validation layers into $(VVL))
	$(Q)mkdir -p $(VVL)
	$(Q)curl -sfL -o $(VVL)/vvl.pkg.tar.zst https://archlinux.org/packages/extra/x86_64/vulkan-validation-layers/download/
	$(Q)tar --zstd -xf $(VVL)/vvl.pkg.tar.zst -C $(VVL) usr/lib usr/share/vulkan
	@$(call ok,Validation layers in $(VVL))
endif

##@ Develop

build: need-dotnet ## Build everything (CONFIG=Debug|Release)
	$(call step,Building $(SOLUTION) ($(CONFIG)))
	$(Q)"$(DOTNET)" build $(SOLUTION) -c $(CONFIG) $(QUIET)

run: need-dotnet ## Run the game client (ARGS="--start local")
	$(call step,Building the client ($(CONFIG)))
	$(Q)"$(DOTNET)" build $(CLIENT) -c $(CONFIG) $(QUIET)
	$(call step,Running prospect $(SHOWN_ARGS))
	@$(RUN_ENV) "$(DOTNET)" $(BIN)/Prospect.Client/$(config)/prospect.dll $(ARGS)

server: need-dotnet ## Run the headless dedicated server (ARGS="--port 27420")
	$(call step,Building the server ($(CONFIG)))
	$(Q)"$(DOTNET)" build $(SERVER) -c $(CONFIG) $(QUIET)
	$(call step,Running prospect-server $(SHOWN_ARGS))
	@$(RUN_ENV) "$(DOTNET)" $(BIN)/Prospect.Server/$(config)/prospect-server.dll $(ARGS)

listing: need-dotnet ## Run the listing server on 127.0.0.1:8080 (LISTING_PORT), private addresses allowed
	$(call step,Building the listing server ($(CONFIG)))
	$(Q)"$(DOTNET)" build $(LISTING) -c $(CONFIG) $(QUIET)
	$(call step,Running prospect-listing on http://127.0.0.1:$(LISTING_PORT) $(ARGS))
	$(Q)ASPNETCORE_URLS=http://127.0.0.1:$(LISTING_PORT) Listing__AllowPrivateAddresses=true \
	  "$(DOTNET)" $(BIN)/Prospect.Listing/$(config)/prospect-listing.dll $(ARGS)

listing-image: ## Build the listing server's container image (LISTING_IMAGE=prospect-listing)
	$(call step,Building container image $(LISTING_IMAGE))
	$(Q)docker build -f docker/listing.Dockerfile -t $(LISTING_IMAGE) .
	@$(call ok,Image $(LISTING_IMAGE) built)

server-image: ## Build the dedicated server's container image (SERVER_IMAGE=prospect-server)
	$(call step,Building container image $(SERVER_IMAGE))
	$(Q)docker build -f docker/server.Dockerfile -t $(SERVER_IMAGE) .
	@$(call ok,Image $(SERVER_IMAGE) built)

test: need-dotnet ## Run the unit tests
	$(call step,Building the tests ($(CONFIG)))
	$(Q)"$(DOTNET)" build $(TESTS) -c $(CONFIG) $(QUIET)
	$(Q)"$(DOTNET)" build $(LTESTS) -c $(CONFIG) $(QUIET)
	$(call step,Running the tests)
	$(Q)$(RUN_ENV) $(TEST_ENV) "$(DOTNET)" $(BIN)/Prospect.Tests/$(config)/Prospect.Tests.dll $(TEST_ARGS)
	$(call step,Running the listing server tests)
	$(Q)"$(DOTNET)" $(BIN)/Prospect.Listing.Tests/$(config)/Prospect.Listing.Tests.dll $(TEST_ARGS)

bench: need-dotnet ## Release benchmark: frame rate and per-frame allocation (fails below target)
	$(call step,Building the client (Release))
	$(Q)"$(DOTNET)" build $(CLIENT) -c Release $(QUIET)
	$(call step,Benchmarking an empty scene at 1920x1080)
	$(Q)$(RUN_ENV) "$(DOTNET)" $(BIN)/Prospect.Client/release/prospect.dll --bench 10 --size 1920x1080

##@ Quality

.PHONY: gas-shaders gas-overlay-tests
gas-shaders: ## Compile shaders and inspect the shield mask specialization offline (no GPU)
	$(call step,Checking gas shaders and shield mask specialization)
	$(Q)python3 tools/check_gas_shaders.py

gas-overlay-tests: ## Verify overlay capture evidence validation (no GPU)
	$(Q)python3 tools/test_gas_overlay.py

fmt: need-dotnet ## Format the code
	$(call step,Formatting)
	$(Q)"$(DOTNET)" format $(SOLUTION) $(FORMAT_ARGS)
	@$(call ok,Formatted)

check: need-dotnet ## Formatting, warnings-as-errors build and tests; must be clean before a milestone is done
	$(call step,Checking formatting)
	$(Q)"$(DOTNET)" format $(SOLUTION) --verify-no-changes
	$(call step,Building $(SOLUTION) ($(CONFIG)))
	$(Q)"$(DOTNET)" build $(SOLUTION) -c $(CONFIG) $(QUIET)
	$(call step,Running the tests)
	$(Q)$(RUN_ENV) $(TEST_ENV) "$(DOTNET)" $(BIN)/Prospect.Tests/$(config)/Prospect.Tests.dll $(TEST_ARGS)
	$(call step,Running the listing server tests)
	$(Q)"$(DOTNET)" $(BIN)/Prospect.Listing.Tests/$(config)/Prospect.Listing.Tests.dll $(TEST_ARGS)
	@printf '\n%s\n' "$(GREEN)$(BOLD)All checks passed.$(RESET)"

clean: ## Remove build output
	$(call step,Removing artifacts/)
	$(Q)rm -rf artifacts

# Stops a target early with a pointer to `make setup` when there is no usable SDK.
need-dotnet:
	@if [ -z "$(DOTNET)" ]; then \
	   $(call fail,No .NET $(DOTNET_CHANNEL) SDK found$(if $(DOTNET_ARCH), for $(DOTNET_ARCH))); \
	   $(call note,Run: make setup); exit 1; fi
