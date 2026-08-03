# vixenengine.org, in a container.
#
# The site is prerendered static files and nothing else (ADR-017) — no server-side runtime, no SSR,
# no cold start — so the image is a web server and a directory, and the deploy is a pull.
#
# ⚠ **The build output has to exist before this runs.** Producing it needs the .NET graph
# (`./build.sh Docs --configuration Release`, in Release for the reason docs/plan/25 § 3.2 gives)
# and a prerender of ~4 250 routes, so a multi-stage Dockerfile that did it here would drag the
# entire engine and both toolchains into an image whose job is to serve HTML.
#
#   pnpm build && pnpm bundle      # → dist/vixen-docs/browser
#   docker build -t vixen-docs .
#   docker run --rm -p 8080:8080 vixen-docs
#
# On a cluster there are three things worth knowing and nothing else: it listens on **8080** as a
# non-root user, it answers **/healthz** for liveness and readiness, and it holds no state — every
# replica serves the same immutable copy, and a new version of the site is a new tag.
#
# Published to ghcr.io/rikarin/vixen-docs by .github/workflows/docs.yml.

# Unprivileged rather than the stock `nginx` image: that one starts as root to bind port 80 and
# writes its caches into the root filesystem, which is two arguments with a hardened pod spec that
# this image does not have. It listens on 8080 and runs as uid 101 out of the box.
FROM nginxinc/nginx-unprivileged:1.29-alpine

# What links the package to this repository on GHCR, and what makes it inherit the repository's
# visibility instead of arriving as an orphan nobody can trace back to a commit.
LABEL org.opencontainers.image.source="https://github.com/Rikarin/Vixen" \
      org.opencontainers.image.description="vixenengine.org — the Vixen documentation site" \
      org.opencontainers.image.licenses="Apache-2.0"

COPY nginx.conf /etc/nginx/conf.d/default.conf

# `pnpm bundle` has already copied the prerendered 404 to the root of this directory, which is where
# nginx.conf's `error_page` looks for it.
COPY dist/vixen-docs/browser /usr/share/nginx/html

EXPOSE 8080
