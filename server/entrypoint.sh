#!/bin/sh
set -u
umask 077
dotnet /opt/benchforge/BenchForge.Cli.dll --config /job/config.json --reports /job/reports --events --cancel-file /job/cancel > /job/events.jsonl 2> /job/error.log
result=$?
printf '%s\n' "$result" > /job/exit-code.tmp
mv /job/exit-code.tmp /job/exit-code
exit "$result"
