#!/usr/bin/env bash
# One-time preparation inside a BenchForge Ubuntu template; never in a measured phase.
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y openjdk-21-jdk-headless maven python3 python3-pip python3-venv build-essential pkg-config libssl-dev curl ca-certificates xz-utils
tools=/opt/benchforge-toolchains
mkdir -p -- "$tools"
case "$(uname -m)" in x86_64) node_arch=x64; go_arch=amd64 ;; aarch64|arm64) node_arch=arm64; go_arch=arm64 ;; *) exit 1 ;; esac
node_version=v24.20.0
go_version=go1.27.4
if [[ ! -x "$tools/node/bin/node" ]]; then
  curl --fail --location --retry 3 "https://nodejs.org/dist/$node_version/node-$node_version-linux-$node_arch.tar.xz" -o "$tools/node.tar.xz"
  curl --fail --location --retry 3 "https://nodejs.org/dist/$node_version/SHASUMS256.txt" -o "$tools/node-shasums.txt"
  expected="$(awk -v file="node-$node_version-linux-$node_arch.tar.xz" '$2==file {print $1}' "$tools/node-shasums.txt")"
  [[ -n "$expected" ]] && echo "$expected  $tools/node.tar.xz" | sha256sum --check --status
  mkdir -p -- "$tools/node"
  tar -xJf "$tools/node.tar.xz" --strip-components=1 -C "$tools/node"
fi
if [[ ! -x "$tools/go/bin/go" ]]; then
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
if [[ ! -x "$tools/cargo/bin/cargo" ]]; then
  curl --fail --location --retry 3 --proto '=https' --tlsv1.2 https://sh.rustup.rs -o "$tools/rustup-init.sh"
  sh "$tools/rustup-init.sh" -y --profile minimal --no-modify-path --default-toolchain 1.100.0
fi
bash "$root/prepare.sh" java,python,rust,node,go
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
ln -sf "$tools/node/bin/node" /usr/local/bin/node
printf '%s\n' native-workers-v1 > /etc/benchforge-workers
sync
