# One-off acceptance image for 7dtd-wasm.
#
# The dedicated server on this machine crashes at boot (see README.md in
# this directory), so the acceptance run installs a fresh copy of the game
# via steamcmd inside a container, following the same base image and system
# libraries as the workspace's 7dtd-server-container project.
#
# Build:  docker build -t 7dtd-wasm-acceptance .
# Run:    see run_acceptance.sh

FROM docker.io/steamcmd/steamcmd:latest

USER root

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
        lib32gcc-s1 \
        libcurl4 \
        libsdl2-2.0-0 \
        libssl3 \
        tzdata \
 && rm -rf /var/lib/apt/lists/*

# Fresh install of the 7 Days to Die dedicated server (Steam app 294420).
# Transient Steam network errors are common, so retry like the workspace's
# 7dtd-server-container entrypoint does. The depot ships the stock
# 0_TFP_Harmony mod under /game/Mods.
RUN for i in $(seq 1 6); do \
      echo "steamcmd attempt $i of 6"; \
      if steamcmd +force_install_dir /game +login anonymous +app_update 294420 validate +quit; then \
        echo "steamcmd succeeded"; \
        break; \
      fi; \
      sleep $((i * 10)); \
    done \
 && ls /game/Mods

WORKDIR /game

# The base image runs steamcmd as its entrypoint; this image is a game
# server, so clear it. The command comes from the docker run invocation.
ENTRYPOINT []
