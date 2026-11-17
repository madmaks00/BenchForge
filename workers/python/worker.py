#!/usr/bin/env python3
import concurrent.futures
import json
import math
import os
from pathlib import Path
import sys
import threading
import time

sys.path.insert(0, str(Path(__file__).resolve().parent / "vendor"))
from portable import GENERATOR, Histogram, Random, row, vectors

SCENARIOS = [
    "PointRead",
    "RangeRead",
    "Aggregate",
    "AnalyticalScan",
    "Update",
    "Mixed",
    "Insert",
    "BatchInsert",
    "Delete",
    "FullTextSearch",
]
CAPABILITIES = {
    name: [s for s in SCENARIOS if s not in ("AnalyticalScan", "FullTextSearch")]
    for name in ["sqlite", "postgreSql", "mySql", "mariaDb", "mongoDb"]
}
CAPABILITIES.update(
    redis=["PointRead", "Update", "Mixed", "Insert", "BatchInsert", "Delete"],
    valkey=["PointRead", "Update", "Mixed", "Insert", "BatchInsert", "Delete"],
    clickHouse=["RangeRead", "Aggregate", "AnalyticalScan", "BatchInsert"],
)
for target in ("sqlServer", "oracle", "cockroachDb"):
    CAPABILITIES[target] = [
        s for s in SCENARIOS if s not in ("AnalyticalScan", "FullTextSearch")
    ]
CAPABILITIES["duckDb"] = [s for s in SCENARIOS if s != "FullTextSearch"]
for target in ("elasticsearch", "openSearch"):
    CAPABILITIES[target] = [s for s in SCENARIOS if s != "AnalyticalScan"]
cancel = threading.Event()
output_lock = threading.Lock()
run = {}


def emit(kind, data):
    with output_lock:
        print(
            json.dumps(
                dict(
                    protocolVersion=1, type=kind, runId=run.get("runId", ""), data=data
                ),
                separators=(",", ":"),
                ensure_ascii=True,
            ),
            flush=True,
        )


def check():
    if cancel.is_set():
        raise InterruptedError("Cancelled")


def base(phase):
    return dict(
        engine="python",
        target=run["target"],
        datasetRows=run["rows"],
        clients=run["clients"],
        repeat=run["repeat"],
        phase=phase,
    )


def measure(
    sessions,
    sequences,
    deleted,
    operation,
    seconds,
    clients,
    rate,
    stage,
    progress,
    config,
):
    import psutil

    process = psutil.Process()
    counters = [
        dict(
            success=0,
            rows=0,
            errors=0,
            dropped=0,
            service=Histogram(),
            end=Histogram(),
            failed=Histogram(),
            examples={},
        )
        for _ in range(clients)
    ]
    gate = threading.Barrier(clients + 1)
    start = [0.0]
    planned = math.floor(seconds * (rate or 0))
    delete_lock = threading.Lock()
    batch_size = min(
        config["batchSize"], max(1, 8 * 1024 * 1024 // (config["payloadBytes"] + 128))
    )

    def client(index):
        c = counters[index]
        rng = Random(
            config["seed"]
            ^ ((index + 1) * 104729)
            ^ run["repeat"]
            ^ ((stage["number"] if stage else 0) * 32452843)
        )
        sequence, slot = sequences[index], index
        gate.wait()
        deadline = start[0] + seconds
        while not cancel.is_set() and time.perf_counter() < deadline:
            scheduled = time.perf_counter()
            if rate:
                if slot >= planned:
                    break
                scheduled = start[0] + slot / rate
                lag = time.perf_counter() - scheduled
                if lag > config["maxStartLagMs"] / 1000:
                    skipped = min(
                        max(
                            1,
                            math.floor(
                                (lag - config["maxStartLagMs"] / 1000) * rate / clients
                            )
                            + 1,
                        ),
                        (planned - 1 - slot) // clients + 1,
                    )
                    c["dropped"] += skipped
                    slot += skipped * clients
                    continue
                while time.perf_counter() < scheduled and not cancel.is_set():
                    time.sleep(0.001)
                if cancel.is_set() or time.perf_counter() >= deadline:
                    break
                if time.perf_counter() - scheduled > config["maxStartLagMs"] / 1000:
                    c["dropped"] += 1
                    slot += clients
                    continue
            selected = (
                ("PointRead" if rng.next() % 100 < 80 else "Update")
                if operation == "Mixed"
                else operation
            )
            key = rng.key(run["rows"], config["hotspot"])
            one = batch = None
            if selected in ("Insert", "BatchInsert"):
                size = batch_size if selected == "BatchInsert" else 1
                first = (
                    (SCENARIOS.index(operation) + 1) * 1_000_000_000_000
                    + (sequence * run["clients"] + index) * size
                    + 1
                )
                if selected == "Insert":
                    one = row(first, config["seed"], config["payloadBytes"])
                else:
                    batch = [
                        row(first + i, config["seed"], config["payloadBytes"])
                        for i in range(size)
                    ]
            if selected == "Delete":
                if stage:
                    with delete_lock:
                        deleted[0] += 1
                        key = deleted[0]
                else:
                    key = sequence * clients + index + 1
                if key > run["rows"]:
                    break
            before = time.perf_counter()
            try:
                if hasattr(sessions[index], "stats"): sessions[index].scheduled = scheduled if rate else before
                affected = sessions[index].execute(selected, key, one, batch)
                finish = time.perf_counter()
                c["success"] += 1
                c["rows"] += affected
                c["service"].add(max(1, (finish - before) * 1e6))
                c["end"].add(max(1, (finish - (scheduled if rate else before)) * 1e6))
            except Exception as error:
                c["errors"] += 1
                c["failed"].add(max(1, (time.perf_counter() - before) * 1e6))
                message = (type(error).__name__ + ": " + str(error)).replace(
                    run["endpoint"].get("password", "") or "\0", "[redacted]"
                )[:250]
                c["examples"][message] = c["examples"].get(message, 0) + 1
            sequence += 1
            slot += clients
            if c["errors"] >= 50:
                break
        if rate and slot < planned and not cancel.is_set():
            c["dropped"] += (planned - 1 - slot) // clients + 1
        sequences[index] = sequence
        c["finished"] = time.perf_counter()

    with concurrent.futures.ThreadPoolExecutor(max_workers=clients) as executor:
        tasks = [executor.submit(client, index) for index in range(clients)]
        cpu = time.process_time()
        rss = process.memory_info().rss / 1048576
        start[0] = time.perf_counter()
        gate.wait()
        report_at = start[0] + 1
        sample_at = start[0] + 0.25
        while not all(task.done() for task in tasks):
            time.sleep(0.05)
            if time.perf_counter() >= sample_at:
                rss = max(rss, process.memory_info().rss / 1048576)
                sample_at = time.perf_counter() + 0.25
            if progress and time.perf_counter() >= report_at:
                emit(
                    "progress",
                    dict(
                        phase=operation,
                        stage=stage["name"] if stage else None,
                        clients=clients,
                        targetSeconds=seconds,
                        successful=sum(c["success"] for c in counters),
                        errors=sum(c["errors"] for c in counters),
                        elapsedSeconds=time.perf_counter() - start[0],
                    ),
                )
                report_at = time.perf_counter() + 1
        for task in tasks:
            task.result()
    elapsed = max(1e-6, max(c["finished"] for c in counters) - start[0])
    service, end, failed = Histogram(), Histogram(), Histogram()
    examples = {}
    for c in counters:
        service.merge(c["service"])
        end.merge(c["end"])
        failed.merge(c["failed"])
        for key, value in c["examples"].items():
            examples[key] = examples.get(key, 0) + value
    success, errors, dropped, affected = (
        sum(c[key] for c in counters)
        for key in ("success", "errors", "dropped", "rows")
    )
    result = base(operation)
    result.update(
        stage=stage,
        clients=clients,
        unit="rows/s" if operation == "BatchInsert" else "ops/s",
        elapsedSeconds=elapsed,
        successfulOperations=success,
        successfulRows=affected,
        errors=errors,
        dropped=dropped,
        plannedOperations=planned
        if rate and not cancel.is_set()
        else success + errors + dropped,
        operationsPerSecond=success / elapsed,
        rowsPerSecond=affected / elapsed
        if operation in ("Insert", "BatchInsert", "Update", "Delete")
        else None,
        serviceLatency=service.snapshot(),
        endToEndLatency=end.snapshot(),
        errorLatency=failed.snapshot(),
        client=dict(
            cpuPercent=(time.process_time() - cpu) / elapsed * 100,
            rssEndMb=process.memory_info().rss / 1048576,
            rssSampledMaxMb=rss,
            allocatedBytes=None,
            gen0Collections=None,
            gen1Collections=None,
            gen2Collections=None,
            gcPauseMs=None,
        ),
        errorExamples=dict(sorted(examples.items(), key=lambda item: -item[1])[:8]),
        cancelled=cancel.is_set(),
    )
    return result


def benchmark():
    from adapters import Database

    config = dict(
        durationSeconds=5,
        warmupSeconds=2,
        payloadBytes=256,
        batchSize=1000,
        seed=42,
        hotspot=False,
        loadModel="closedLoop",
        targetOpsPerSecond=1000,
        maxStartLagMs=20,
        scenarios=[],
    )
    config.update(run["config"])
    if config.get("customWorkload"):
        from custom import benchmark as custom_benchmark
        return custom_benchmark(run, config, measure, base, emit, check, cancel)
    target = run["target"]
    until = time.monotonic() + 90
    while True:
        check()
        try:
            db = Database(run)
            break
        except Exception:
            if target == "sqlite" or time.monotonic() >= until:
                raise
            cancel.wait(0.5)
    try:
        metadata = db.metadata()
        metadata["settings"]["effective_batch_size"] = str(
            min(
                config["batchSize"],
                max(1, 8 * 1024 * 1024 // (config["payloadBytes"] + 128)),
            )
        )
        emit("metadata", metadata)
        db.reset()
        emit("phase_start", dict(phase="bulk_load", targetSeconds=0))
        started = time.perf_counter()
        generation = 0
        amount = 0
        batch_size = int(metadata["settings"]["effective_batch_size"])
        report_at = started + 1
        for first in range(1, run["rows"] + 1, batch_size):
            check()
            before = time.perf_counter()
            batch = [
                row(identifier, config["seed"], config["payloadBytes"])
                for identifier in range(first, min(first + batch_size, run["rows"] + 1))
            ]
            generation += time.perf_counter() - before
            db.batch(batch)
            amount += sum(value["amountCents"] for value in batch)
            if time.perf_counter() >= report_at:
                emit(
                    "progress",
                    dict(
                        phase="bulk_load",
                        loaded=min(first + batch_size - 1, run["rows"]),
                        total=run["rows"],
                    ),
                )
                report_at = time.perf_counter() + 1
        elapsed = max(1e-6, time.perf_counter() - started)
        check()
        if db.digest() != (run["rows"], run["rows"] * (run["rows"] + 1) // 2, amount):
            raise ValueError("Dataset digest mismatch")
        result = base("bulk_load")
        result.update(
            unit="rows/s",
            elapsedSeconds=elapsed,
            successfulRows=run["rows"],
            successfulOperations=math.ceil(run["rows"] / batch_size),
            rowsPerSecond=run["rows"] / elapsed,
            generationSeconds=generation,
            verified=True,
        )
        emit("result", result)
        if target not in ("redis", "valkey", "clickHouse"):
            check()
            emit("phase_start", dict(phase="index_build", targetSeconds=0))
            before = time.perf_counter()
            db.prepare()
            result = base("index_build")
            result.update(
                unit="seconds",
                elapsedSeconds=time.perf_counter() - before,
                successfulOperations=1,
                verified=True,
            )
            emit("result", result)
        requested = [name[0].upper() + name[1:] for name in config.get("scenarios", [])]
        for scenario in requested:
            if scenario not in CAPABILITIES[target]:
                emit(
                    "unsupported",
                    dict(
                        scenario=scenario,
                        message="Not supported by this database profile",
                    ),
                )
        operations = [
            name
            for name in SCENARIOS
            if name in CAPABILITIES[target]
            and (
                name in requested
                if requested
                else not config.get("plan")
                or name
                == ("PointRead" if "PointRead" in CAPABILITIES[target] else "RangeRead")
            )
        ]
        expected = run["rows"]

        def prepare_sessions(count):
            with concurrent.futures.ThreadPoolExecutor(
                max_workers=min(32, count)
            ) as executor:
                return list(executor.map(lambda _: Database(run), range(count)))

        if config["warmupSeconds"] > 0:
            count = (
                config["plan"]["stages"][0]["clients"]
                if config.get("plan")
                else run["clients"]
            )
            sessions = prepare_sessions(count)
            try:
                warmup = measure(
                    sessions,
                    [0] * count,
                    [0],
                    "PointRead" if "PointRead" in CAPABILITIES[target] else "RangeRead",
                    config["warmupSeconds"],
                    count,
                    config["targetOpsPerSecond"]
                    if not config.get("plan") and config["loadModel"] == "constantRate"
                    else None,
                    None,
                    False,
                    config,
                )
                if warmup["errors"]:
                    raise ValueError("Warmup errors")
            finally:
                for session in sessions:
                    session.close()
        operations.sort(key=lambda name: name == "Delete")
        for operation in operations:
            check()
            sessions = []
            sequences = [0] * run["clients"]
            deleted = [0]
            try:
                stages = (
                    config["plan"]["stages"]
                    if config.get("plan")
                    else [
                        dict(
                            name="",
                            clients=run["clients"],
                            seconds=config["durationSeconds"],
                            operationsPerSecond=config["targetOpsPerSecond"]
                            if config["loadModel"] == "constantRate"
                            else None,
                        )
                    ]
                )
                for index, stage in enumerate(stages):
                    check()
                    if len(sessions) < stage["clients"]:
                        sessions.extend(
                            prepare_sessions(stage["clients"] - len(sessions))
                        )
                    stamp = (
                        dict(
                            number=index + 1,
                            name=stage["name"],
                            targetSeconds=stage["seconds"],
                            operationsPerSecond=stage.get("operationsPerSecond"),
                        )
                        if config.get("plan")
                        else None
                    )
                    emit(
                        "phase_start",
                        dict(
                            phase=operation,
                            targetSeconds=stage["seconds"],
                            stage=stamp,
                            clients=stage["clients"],
                        ),
                    )
                    result = measure(
                        sessions,
                        sequences,
                        deleted,
                        operation,
                        stage["seconds"],
                        stage["clients"],
                        stage.get("operationsPerSecond"),
                        stamp,
                        True,
                        config,
                    )
                    emit("phase_end", {})
                    if operation in ("Insert", "BatchInsert"):
                        expected += result["successfulRows"]
                    if operation == "Delete":
                        expected -= result["successfulRows"]
                    actual = db.count()
                    result["verified"] = (
                        actual == expected
                        and result["successfulOperations"] > 0
                        and not cancel.is_set()
                    )
                    result["cancelled"] = cancel.is_set()
                    emit("result", result)
                    if actual != expected:
                        raise ValueError(f"Count mismatch: {actual} != {expected}")
                    if (
                        not config.get("plan")
                        and not result["successfulOperations"]
                        and not cancel.is_set()
                    ):
                        raise ValueError("No successful operations")
            finally:
                for session in sessions:
                    session.close()
    finally:
        db.close()


def main():
    global run
    if "--self-test" in sys.argv:
        print(json.dumps(vectors(), separators=(",", ":")))
        return 0
    emit(
        "hello",
        dict(
            engine="python",
            runtime=sys.version.splitlines()[0],
            generator=GENERATOR,
            histogram="binary128-us-v1",
            targets=list(CAPABILITIES),
        ),
    )
    try:
        limit = 64 * 1024 * 1024
        line = sys.stdin.readline(limit + 1)
        if not line or len(line) > limit:
            raise ValueError("Invalid command length")
        command = json.loads(line.lstrip("\ufeff"))
        if command.get("protocolVersion") != 1 or command.get("type") != "run":
            raise ValueError("Expected protocol v1 run")
        run = command["run"]
        if run.get("engine", "python") != "python" or run["target"] not in CAPABILITIES:
            raise ValueError("Unsupported engine or target")
        if run["rows"] < 100 or run["clients"] < 1:
            raise ValueError("Invalid run dimensions")

        def control():
            try:
                for line in sys.stdin:
                    message = json.loads(line)
                    if (
                        message.get("protocolVersion") == 1
                        and message.get("type") == "cancel"
                    ):
                        cancel.set()
                        return
                cancel.set()
            except Exception:
                cancel.set()

        threading.Thread(target=control, daemon=True).start()
        benchmark()
        emit("completed", dict(cancelled=cancel.is_set()))
        return 2 if cancel.is_set() else 0
    except InterruptedError:
        emit("completed", dict(cancelled=True))
        return 2
    except Exception as error:
        message = str(error).replace(
            run.get("endpoint", {}).get("password", "") or "\0", "[redacted]"
        )
        emit("failed", dict(message=message))
        return 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stdin.reconfigure(encoding="utf-8")
    sys.exit(main())
