# PROSPECT task runner. `make help` lists the targets.

CONFIG ?= Debug
ARGS ?=
DOTNET ?= dotnet
SOLUTION := Prospect.slnx
CLIENT := src/Prospect.Client
SERVER := src/Prospect.Server
QUIET := --nologo -v q
# Build output folders are lower-case configuration names: artifacts/bin/<project>/<config>/.
config := $(shell echo $(CONFIG) | tr A-Z a-z)
BIN := artifacts/bin

# Vulkan validation layers fetched by `make tools` (for machines without a system-wide install).
VVL := $(CURDIR)/.tools/vvl
ifneq ($(wildcard $(VVL)/usr/lib/libVkLayer_khronos_validation.so),)
export VK_ADD_LAYER_PATH := $(VVL)/usr/share/vulkan/explicit_layer.d
export LD_LIBRARY_PATH := $(VVL)/usr/lib:$(LD_LIBRARY_PATH)
endif

.PHONY: help build run server test fmt check bench tools clean

help: ## List targets
	@grep -E '^[a-z-]+:.*##' $(MAKEFILE_LIST) | sed -E 's/^([a-z-]+):.*## (.*)/  \1\t\2/' | expand -t 12

build: ## Build everything (CONFIG=Debug|Release)
	$(DOTNET) build $(SOLUTION) -c $(CONFIG) $(QUIET)

run: ## Run the game client (ARGS="--start local")
	$(DOTNET) build $(CLIENT) -c $(CONFIG) $(QUIET)
	$(DOTNET) $(BIN)/Prospect.Client/$(config)/prospect.dll $(ARGS)

server: ## Run the headless dedicated server (ARGS="--port 27420")
	$(DOTNET) build $(SERVER) -c $(CONFIG) $(QUIET)
	$(DOTNET) $(BIN)/Prospect.Server/$(config)/prospect-server.dll $(ARGS)

test: ## Run the unit tests
	$(DOTNET) build tests/Prospect.Tests -c $(CONFIG) $(QUIET)
	$(DOTNET) $(BIN)/Prospect.Tests/$(config)/Prospect.Tests.dll

fmt: ## Format the code
	$(DOTNET) format $(SOLUTION)

check: ## Formatting, warnings-as-errors build and tests; must be clean before a milestone is done
	$(DOTNET) format $(SOLUTION) --verify-no-changes
	$(DOTNET) build $(SOLUTION) -c $(CONFIG) $(QUIET)
	$(DOTNET) $(BIN)/Prospect.Tests/$(config)/Prospect.Tests.dll

bench: ## Release benchmark: frame rate and per-frame allocation in an empty scene (fails below target)
	$(DOTNET) build $(CLIENT) -c Release $(QUIET)
	$(DOTNET) $(BIN)/Prospect.Client/release/prospect.dll --bench 10 --size 1920x1080

tools: ## Fetch Vulkan validation layers into .tools/ (x86-64 Linux; other systems: install the Vulkan SDK)
	mkdir -p $(VVL)
	curl -sfL -o $(VVL)/vvl.pkg.tar.zst https://archlinux.org/packages/extra/x86_64/vulkan-validation-layers/download/
	tar --zstd -xf $(VVL)/vvl.pkg.tar.zst -C $(VVL) usr/lib usr/share/vulkan

clean: ## Remove build output
	rm -rf artifacts
