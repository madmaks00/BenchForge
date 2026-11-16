#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
selected="${1:-java,python,rust,node,go}"
case "$(uname -s)" in Linux) os=linux ;; Darwin) os=osx ;; *) echo 'Use prepare.ps1 on Windows.' >&2; exit 1 ;; esac
case "$(uname -m)" in x86_64) arch=x64 ;; aarch64|arm64) arch=arm64 ;; *) echo 'Unsupported architecture.' >&2; exit 1 ;; esac
tools="${BENCHFORGE_TOOLCHAIN_HOME:-${XDG_CACHE_HOME:-$HOME/.cache}/benchforge-tools}"
mkdir -p -- "$tools" "$root/dist/$os-$arch"
dist="$root/dist/$os-$arch"
staging="$(mktemp -d "$dist/.prepare-XXXXXXXX")"
cleanup() { case "$staging" in "$dist"/.prepare-*) rm -rf -- "$staging" ;; *) exit 1 ;; esac; }
trap cleanup EXIT
export GOPATH="$tools/go-cache" GOCACHE="$tools/go-build-cache" CARGO_TARGET_DIR="$tools/rust-build"
if [[ -d "$tools/rustup" ]]; then export RUSTUP_HOME="$tools/rustup" CARGO_HOME="$tools/cargo"; fi
export PATH="$tools/go/bin:$tools/cargo/bin:$tools/node/bin:$PATH"
IFS=',' read -r -a engines <<< "$selected"
for engine in "${engines[@]}"; do
  case "$engine" in csharp|'') continue ;; java|python|rust|node|go) ;; *) echo "Unknown engine: $engine" >&2; exit 1 ;; esac
  echo "Preparing $engine ..."
  target="$staging/$engine"
  mkdir -p -- "$target"
  case "$engine" in
    python)
      cp -- "$root/python/worker.py" "$root/python/portable.py" "$root/python/adapters.py" "$target/"
      python3 -m pip install --disable-pip-version-check --only-binary=:all: --target "$target/vendor" -r "$root/python/requirements.lock.txt"
      python3 "$target/worker.py" --self-test
      ;;
    node)
      cp -- "$root/node/worker.mjs" "$root/node/portable.mjs" "$root/node/adapters.mjs" "$root/node/package.json" "$root/node/package-lock.json" "$target/"
      npm ci --prefix "$target" --ignore-scripts --no-audit --no-fund
      node "$target/worker.mjs" --self-test
      ;;
    java)
      mvn -f "$root/java/pom.xml" "-Dmaven.repo.local=$tools/maven-cache" -q -DskipTests package dependency:copy-dependencies "-DoutputDirectory=$target/lib"
      cp -- "$root/java/target/worker-java-0.2.0.jar" "$target/worker.jar"
      java -cp "$target/worker.jar:$target/lib/*" dev.benchforge.Worker --self-test
      ;;
    go)
      go -C "$root/go" build -p 2 -mod=readonly -trimpath -ldflags '-s -w' -o "$target/benchforge-go" .
      "$target/benchforge-go" --self-test
      ;;
    rust)
      cargo build --manifest-path "$root/rust/Cargo.toml" --locked --release -j 2
      cp -- "$CARGO_TARGET_DIR/release/benchforge-worker-rust" "$target/benchforge-rust"
      "$target/benchforge-rust" --self-test
      ;;
  esac
  python3 - "$dist" "$target" "$engine" <<'PY'
import json, os, pathlib, shutil, sys, uuid
dist,target,engine=pathlib.Path(sys.argv[1]).resolve(),pathlib.Path(sys.argv[2]).resolve(),sys.argv[3]
assert target.parent.parent==dist and target.name==engine
manifest=dist/'engines.json'
entries=json.loads(manifest.read_text()) if manifest.exists() else []
launch={
 'python':('python',['python/worker.py']),
 'node':('node',['node/worker.mjs']),
 'java':('java',['-cp','java/worker.jar:java/lib/*','dev.benchforge.Worker']),
 'go':('./go/benchforge-go',[]),
 'rust':('./rust/benchforge-rust',[])
}
destination=dist/engine
backup=dist/('.old-'+engine+'-'+uuid.uuid4().hex)
assert destination.parent==dist and backup.parent==dist
if destination.exists(): destination.rename(backup)
try: target.rename(destination)
except BaseException:
 if backup.exists(): backup.rename(destination)
 raise
executable,arguments=launch[engine]
entries=[entry for entry in entries if entry['engine']!=engine]+[dict(engine=engine,executable=executable,arguments=arguments)]
temporary=dist/'engines.json.tmp'
temporary.write_text(json.dumps(entries,indent=2)+'\n')
os.replace(temporary,manifest)
if backup.exists(): shutil.rmtree(backup)
PY
  echo "$engine ready."
done
echo 'Worker bundles prepared.'
