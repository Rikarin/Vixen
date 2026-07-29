# The dedicated server, in a container.
#
# docs/plan/17 § Q5c: a Dockerfile ships in the template rather than the engine growing container
# tooling. There is no registry integration and no compose file here on purpose — those are choices
# about *your* infrastructure, and a game engine that made them for you would be one you had to
# argue with.
#
#   docker build -t my-server .
#   docker run --rm -p 7777:7777/udp my-server
#
# The client and the server are the same project (doc 17 § Q5a). What makes this a server is
# `VixenVariant=Server`, which the SDK stamps onto the assembly so the host picks the headless
# platform at boot instead of asking for a window.

# ---------------------------------------------------------------------------------------------
# Build. The SDK image, plus the Vixen tool the SDK's targets shell out to for the content build.
# ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# ⚠ Version-locked to the packages the project restores. The SDK and the CLI are one thing split
# across two packages — an `import` written by one version and a bundle read by another is the kind
# of mismatch that shows up as a missing asset on a running server.
RUN dotnet tool install --global Vixen.Cli --version VIXEN_PACKAGE_VERSION
ENV PATH="$PATH:/root/.dotnet/tools"

# Restore against the project file alone, so editing a source file does not invalidate the
# restore layer. Assets/ is deliberately not here: it is content, and content is built below.
COPY VixenGame1.csproj ./
RUN dotnet restore VixenGame1.csproj

COPY . .

# One command, because `dotnet publish` is where the SDK's import and content-build steps hang.
# `vixen build` would do the same thing and would then need this image to keep the tool on the PATH
# of a process it started; inside a container there is nothing to gain from the extra hop.
RUN dotnet publish VixenGame1.csproj --configuration Release --no-restore -p:VixenVariant=Server -p:VixenTarget=Linux --output /app

# ---------------------------------------------------------------------------------------------
# Run. Chiselled: no shell, no package manager, and a non-root `app` user already in the image.
# ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS runtime
WORKDIR /app

COPY --from=build --chown=$APP_UID:$APP_UID /app .

USER $APP_UID

# The port the game's transport binds. Change it here and in the server's own configuration
# together — Docker publishes what it is told, and a mismatch is a server nobody can reach.
EXPOSE 7777/udp

ENTRYPOINT ["./VixenGame1"]
