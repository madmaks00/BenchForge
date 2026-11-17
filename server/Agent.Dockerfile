# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BENCHFORGE_ENGINES=csharp
WORKDIR /src
COPY . .
RUN mkdir -p /opt/benchforge-toolchains/node \
    && native="$(printf '%s' "$BENCHFORGE_ENGINES" | tr ',' '\n' | grep -v '^csharp$' | paste -sd, -)" \
    && if [ -n "$native" ]; then bash workers/install-linux.sh "$native"; fi
RUN dotnet publish src/BenchForge.Cli/BenchForge.Cli.csproj -c Release --self-contained false -o /out/agent \
    && dotnet publish src/BenchForge.Worker.CSharp/BenchForge.Worker.CSharp.csproj -c Release --self-contained false -o /out/agent/worker \
    && mkdir -p workers/dist

FROM docker:29-cli AS dockercli
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble
ARG BENCHFORGE_ENGINES=csharp
ARG BENCHFORGE_SOURCE=unknown
LABEL benchforge.agent=server-v1 benchforge.source=$BENCHFORGE_SOURCE benchforge.engines=$BENCHFORGE_ENGINES
RUN set -eu; packages='ca-certificates libstdc++6'; \
    case ",$BENCHFORGE_ENGINES," in *,java,*) packages="$packages openjdk-21-jre-headless" ;; esac; \
    case ",$BENCHFORGE_ENGINES," in *,python,*) packages="$packages python3" ;; esac; \
    apt-get update && apt-get install -y --no-install-recommends $packages && rm -rf /var/lib/apt/lists/*
COPY --from=dockercli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=build /out/agent /opt/benchforge
COPY --from=build /src/workers/dist /opt/benchforge-workers/dist
COPY --from=build /opt/benchforge-toolchains/node /opt/benchforge-toolchains/node
COPY server/entrypoint.sh /opt/benchforge/entrypoint.sh
RUN chmod 755 /opt/benchforge/entrypoint.sh
ENV BENCHFORGE_WORKERS=/opt/benchforge-workers PATH=/opt/benchforge-toolchains/node/bin:$PATH HOME=/tmp
WORKDIR /opt/benchforge
ENTRYPOINT ["/bin/sh", "/opt/benchforge/entrypoint.sh"]
