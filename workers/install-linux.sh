#!/usr/bin/env bash
# One-time preparation inside a BenchForge Ubuntu template; never in a measured phase.
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
selected="${1:-java,python,rust,node,go}"
has() { [[ ",$selected," == *",$1,"* ]]; }
IFS=',' read -r -a choices <<< "$selected"
for choice in "${choices[@]}"; do case "$choice" in csharp|java|python|rust|node|go) ;; *) echo 'Unknown engine' >&2; exit 1 ;; esac; done
export DEBIAN_FRONTEND=noninteractive
apt-get update
packages=(python3 curl ca-certificates xz-utils)
if has java; then packages+=(openjdk-21-jdk-headless maven); fi
if has python; then packages+=(python3-pip python3-venv); fi
if has rust; then packages+=(build-essential pkg-config libssl-dev); fi
apt-get install -y "${packages[@]}"
tools=/opt/benchforge-toolchains
mkdir -p -- "$tools" "$tools/node"
case "$(uname -m)" in x86_64) node_arch=x64; go_arch=amd64 ;; aarch64|arm64) node_arch=arm64; go_arch=arm64 ;; *) exit 1 ;; esac
node_version=v24.20.0
go_version=go1.27.4
if has node && [[ ! -x "$tools/node/bin/node" ]]; then
  curl --fail --location --retry 3 "https://nodejs.org/dist/$node_version/node-$node_version-linux-$node_arch.tar.xz" -o "$tools/node.tar.xz"
  curl --fail --location --retry 3 "https://nodejs.org/dist/$node_version/SHASUMS256.txt" -o "$tools/node-shasums.txt"
  expected="$(awk -v file="node-$node_version-linux-$node_arch.tar.xz" '$2==file {print $1}' "$tools/node-shasums.txt")"
  [[ -n "$expected" ]] && echo "$expected  $tools/node.tar.xz" | sha256sum --check --status
  mkdir -p -- "$tools/node"
  tar -xJf "$tools/node.tar.xz" --strip-components=1 -C "$tools/node"
fi
if has go && [[ ! -x "$tools/go/bin/go" ]]; then
  curl --fail --location --retry 3 "https://go.dev/dl/$go_version.linux-$go_arch.tar.gz" -o "$tools/go.tar.gz"
  curl --fail --location --retry 3 'https://go.dev/dl/?mode=json&include=all' -o "$tools/go-releases.json"
  expected="$(python3 - "$tools/go-releases.json" "$go_version.linux-$go_arch.tar.gz" <<'PY'
import json,sys
print(next(f['sha256'] for r in json.load(open(sys.argv[1])) for f in r['files'] if f['filename']==sys.argv[2]))
PY
)"
  [[ -n "$expected" ]] && echo "$expected  $tools/go.tar.gz" | sha256sum --check --status
  tar -xzf "$tools/go.tar.gz" -C "$tools"
fi
export BENCHFORGE_TOOLCHAIN_HOME="$tools" RUSTUP_HOME="$tools/rustup" CARGO_HOME="$tools/cargo"
export PATH="$tools/node/bin:$tools/go/bin:$tools/cargo/bin:$PATH"
if has rust && [[ ! -x "$tools/cargo/bin/cargo" ]]; then
  curl --fail --location --retry 3 --proto '=https' --tlsv1.2 https://sh.rustup.rs -o "$tools/rustup-init.sh"
  sh "$tools/rustup-init.sh" -y --profile minimal --no-modify-path --default-toolchain 1.100.0
fi
export CGO_ENABLED=0
bash "$root/prepare.sh" "$selected"
# Keep compiler caches out of every subsequent VM clone.
python3 - "$tools" <<'PY'
import pathlib, shutil, sys
root = pathlib.Path(sys.argv[1]).resolve()
assert root == pathlib.Path('/opt/benchforge-toolchains')
for name in ['rust-build', 'go-cache', 'go-build-cache', 'maven-cache']:
    path = (root / name).resolve()
    assert path.parent == root
    if path.exists(): shutil.rmtree(path)
for name in ['node.tar.xz', 'go.tar.gz']:
    (root / name).unlink(missing_ok=True)
PY
# The guest coordinator invokes these runtimes directly.
if has node; then ln -sf "$tools/node/bin/node" /usr/local/bin/node; fi
printf '%s\n' native-workers-v1 > /etc/benchforge-workers
sync
