# A realm, in a container.
#
# This is the image a placement backend runs. One container per shard, labelled with the shard id,
# launched with a `--realm-spec` argument that says which map, which region, which version and which
# port — so nothing about which shard this is belongs in the image.
#
#   docker build -t my-realm -f VixenMmo1.Realm/Dockerfile .
#   docker run --rm -i -p 7777:7777/udp my-realm --realm-spec "shard=…;map=maps/queensdale;…"
#
# ⚠ `-i` is not optional. The realm's lifecycle travels on its standard streams: it writes
# `vixen-realm ready <endpoint>` when its map is loaded, and it reads `vixen-realm drain` when the
# launcher wants it emptied politely. A container with no stdin is a realm that can be killed and
# not drained.

# ---------------------------------------------------------------------------------------------
# Build. The SDK image, plus the Vixen tool the SDK's targets shell out to for the content build.
# ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

RUN dotnet tool install --global Vixen.Cli --version VIXEN_PACKAGE_VERSION
ENV PATH="$PATH:/root/.dotnet/tools"

# Restore against the project files alone, so editing a source file does not invalidate the restore
# layer. All four, because the realm's own restore walks its project references.
COPY VixenMmo1.Contracts/VixenMmo1.Contracts.csproj VixenMmo1.Contracts/
COPY VixenMmo1.Shared/VixenMmo1.Shared.csproj VixenMmo1.Shared/
COPY VixenMmo1.Realm/VixenMmo1.Realm.csproj VixenMmo1.Realm/
RUN dotnet restore VixenMmo1.Realm/VixenMmo1.Realm.csproj

COPY . .

# `VixenVariant=Server` is what makes this a realm rather than the same program with a window: the
# host picks the headless platform and the device that draws nothing, and the content build writes
# the server profile — no textures, no audio, no shader permutations. That profile is most of why a
# shard starts fast enough to be spawned under load.
RUN dotnet publish VixenMmo1.Realm/VixenMmo1.Realm.csproj --configuration Release --no-restore -p:VixenVariant=Server -p:VixenTarget=Linux --output /app

# ---------------------------------------------------------------------------------------------
# Run. Chiselled: no shell, no package manager, and a non-root `app` user already in the image.
# ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS runtime
WORKDIR /app

COPY --from=build --chown=$APP_UID:$APP_UID /app .

USER $APP_UID

# The default from the process placement backend's pool. A deployment that publishes a different one
# passes the port in the spec — the endpoint a client is told is data, not configuration, which is
# what lets a relay or a node's hostPort be substituted without anything above the transport
# noticing.
EXPOSE 7777/udp

ENTRYPOINT ["./VixenMmo1.Realm"]
