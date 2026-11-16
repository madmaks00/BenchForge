#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")"
dotnet build BenchForge.slnx -c Release --verbosity minimal
dotnet src/BenchForge.Desktop/bin/Release/net10.0/BenchForge.Desktop.dll
