#!/bin/sh
# BenchForge owns only its application directory and containers with an exact job label.
set -eu
export DOCKER_HOST=unix:///var/run/docker.sock
unset DOCKER_CONTEXT
action=${1:?action}; root=${2:?root}; shift 2
fail() { printf '%s\n' "$*" >&2; exit 1; }
[ "$root" = "$HOME/.local/share/benchforge" ] || fail 'Unexpected agent directory'
umask 077
mkdir -p "$root/jobs"
valid_job() { [ ${#1} -eq 32 ] && case "$1" in *[!0-9a-f]*) return 1 ;; *) return 0 ;; esac; }
job_dir() { valid_job "$1" || fail 'Invalid job ID'; dir="$root/jobs/$1"; name="benchforge-job-$1"; }
owned() {
    label=$(docker inspect --format '{{index .Config.Labels "benchforge.job"}}' "$name" 2>/dev/null) || return 1
    [ "$label" = "$job" ] && [ "$(docker inspect --format '{{index .Config.Labels "benchforge.agent"}}' "$name")" = server-v1 ] || fail 'Container ownership mismatch'
}
exclusive() { exec 9>"$root/operation.lock"; flock -n 9 || fail 'Another BenchForge operation is running; wait for it to finish'; }
idle() {
    [ -z "$(docker ps -q --filter label=benchforge.agent=server-v1)" ] || fail 'Another BenchForge test is running on this server'
    [ -z "$(docker ps -aq --filter label=benchforge.job --filter label=dblab.case)" ] || fail 'A previous BenchForge job needs cleanup; resume its observation first'
}
state() {
    if [ -f "$dir/exit-code" ]; then
        case "$(cat "$dir/exit-code")" in 0) echo completed ;; 2) echo cancelled ;; *) echo failed ;; esac
    elif owned && [ "$(docker inspect --format '{{.State.Running}}' "$name")" = true ]; then echo running
    elif [ -d "$dir" ]; then echo failed
    else echo absent; fi
}
case "$action" in
  prepare)
    image=${1:?image}; source_hash=${2:?source hash}; engines=${3:?engines}
    [ ${#source_hash} -eq 64 ] || fail 'Invalid source digest'
    case "$source_hash" in *[!0-9a-f]*) fail 'Invalid source digest' ;; esac
    source="$root/packages/$source_hash"
    [ -f "$source/server/Agent.Dockerfile" ] || fail 'Agent sources not uploaded'
    exclusive; idle
    docker build --build-arg "BENCHFORGE_ENGINES=$engines" --build-arg "BENCHFORGE_SOURCE=$source_hash" \
      -f "$source/server/Agent.Dockerfile" -t "$image" "$source"
    ;;
  check-image)
    image=${1:?image}; source_hash=${2:?source hash}; engines=${3:?engines}
    exclusive; idle
    [ "$(docker image inspect --format '{{index .Config.Labels "benchforge.agent"}}' "$image")" = server-v1 ] || fail 'Prepare the server agent'
    [ "$(docker image inspect --format '{{index .Config.Labels "benchforge.source"}}' "$image")" = "$source_hash" ] || fail 'Agent source version differs'
    [ "$(docker image inspect --format '{{index .Config.Labels "benchforge.engines"}}' "$image")" = "$engines" ] || fail 'Prepare selected engines'
    docker run --rm --entrypoint dotnet "$image" /opt/benchforge/BenchForge.Cli.dll --verify-workers --languages "$engines"
    ;;
  start)
    job=${1:?job}; image=${2:?image}; job_dir "$job"
    exclusive
    if owned; then printf '%s\n' "$job"; exit 0; fi
    [ ! -f "$dir/exit-code" ] || fail 'This job has already finished'
    idle
    [ -f "$dir/config.json" ] || fail 'Job configuration not uploaded'
    mkdir -p "$dir/reports"; : > "$dir/events.jsonl"; : > "$dir/error.log"
    printf '%s\n' "$job" > "$root/active"
    socket=/var/run/docker.sock
    [ -S "$socket" ] || fail 'A local Docker Engine socket is required'
    image_id=$(docker image inspect --format '{{.Id}}' "$image")
    docker run --detach --name "$name" --label "benchforge.job=$job" --label benchforge.agent=server-v1 \
      --network host --hostname "$(hostname)" --user "$(id -u):$(id -g)" --group-add "$(stat -c %g "$socket")" \
      --ulimit nofile=262144:262144 -e "BENCHFORGE_REMOTE_JOB=$job" -e "BENCHFORGE_AGENT_IMAGE_ID=$image_id" \
      -e DOCKER_HOST=unix:///var/run/docker.sock -v "$socket:$socket" -v "$dir:/job" "$image" >/dev/null
    printf '%s\n' "$job"
    ;;
  state)
    job=${1:?job}; job_dir "$job"; state
    ;;
  follow)
    job=${1:?job}; offset=${2:?offset}; job_dir "$job"
    case "$offset" in ''|*[!0-9]*) fail 'Invalid event offset' ;; esac
    [ -f "$dir/events.jsonl" ] || exit 0
    if [ "$(state)" = running ]; then
        pid=$(docker inspect --format '{{.State.Pid}}' "$name")
        tail -c "+$((offset + 1))" -f --pid="$pid" -s 1 "$dir/events.jsonl"
    else tail -c "+$((offset + 1))" "$dir/events.jsonl"; fi
    ;;
  cancel)
    job=${1:?job}; job_dir "$job"; [ -d "$dir" ] || fail 'Unknown job'
    : > "$dir/cancel"
    ;;
  error)
    job=${1:?job}; job_dir "$job"
    if [ -f "$dir/error.log" ]; then tail -c 4000 "$dir/error.log"; fi
    ;;
  collect)
    job=${1:?job}; job_dir "$job"; [ "$(state)" != running ] || fail 'Job is still running'
    report=$(find "$dir/reports" -mindepth 2 -maxdepth 2 -type f -name report.json -print)
    if [ -z "$report" ]; then printf '%s\n' NO_REPORT; exit 0; fi
    count=$(printf '%s\n' "$report" | wc -l)
    [ "$count" -eq 1 ] || fail 'Ambiguous report directory'
    report_dir=$(dirname "$report")
    (cd "$report_dir"; set -- report.json; for file in results.csv summary.json operations.csv resources-*.json; do [ ! -f "$file" ] || set -- "$@" "$file"; done; tar -czf "$dir/report.tar.gz" -- "$@")
    sha256sum "$dir/report.tar.gz"
    ;;
  cleanup)
    job=${1:?job}; job_dir "$job"
    if owned; then
        [ "$(docker inspect --format '{{.State.Running}}' "$name")" != true ] || [ -f "$dir/exit-code" ] || fail 'The agent is still running'
        docker rm --force "$name" >/dev/null
    fi
    for container in $(docker ps -aq --filter "label=benchforge.job=$job" --filter label=dblab.case); do
        label=$(docker inspect --format '{{index .Config.Labels "benchforge.job"}}' "$container")
        case_id=$(docker inspect --format '{{index .Config.Labels "dblab.case"}}' "$container")
        [ "$label" = "$job" ] && valid_job "$case_id" || fail 'Database ownership mismatch'
        docker rm --force --volumes "$container" >/dev/null
    done
    if [ -f "$root/active" ] && [ "$(cat "$root/active")" = "$job" ]; then rm -f -- "$root/active"; fi
    rm -rf -- "$dir/inputs"
    rm -f -- "$dir/inputs.tar.gz"
    ;;
  *) fail 'Unknown agent action' ;;
esac
