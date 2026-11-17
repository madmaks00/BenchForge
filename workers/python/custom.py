"""Native custom SQL execution. Shared input contains SQL dialect compilation, never query results."""

import datetime as dt
import decimal
import json
import threading
import time
import uuid
from portable import Random, Histogram, ALPHABET, MASK
from adapters import Database

D = decimal.Decimal
decimal.getcontext().prec = 64


def seed(value, name, row=0):
    h = 14695981039346656037
    for b in name.encode():
        h = ((h ^ b) * 1099511628211) & MASK
    return int(value) ^ h ^ ((row * 0xD6E8FEB86659FD93) & MASK)


def sample_key(v):
    return (
        f"{v.get('reference')}/{v.get('sampleColumn','id')}/{v.get('sampleSize',10000)}"
    )


def text(value, kind, target):
    if value is None:
        return None
    if hasattr(value, "read"):
        value = value.read()
    if kind in ("int64", "int32"):
        return str(int(D(str(value)).quantize(D(1))))
    if kind == "decimal":
        return str(D(str(value)))
    if kind == "double":
        return format(float(value), ".17g")
    if kind == "boolean":
        normalized = str(value).lower()
        if normalized in ("1", "true"):
            return "1"
        if normalized in ("0", "false"):
            return "0"
        raise ValueError("Invalid boolean")
    if kind == "dateTime":
        if not isinstance(value, dt.datetime):
            value = dt.datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        if value.tzinfo is not None:
            value = value.astimezone(dt.timezone.utc)
        return value.isoformat(sep=" ", timespec="microseconds")[:26]
    if kind == "guid":
        if target == "oracle" and isinstance(value, bytes):
            return value.hex()
        if target == "oracle" and isinstance(value, str) and len(value) == 32:
            return value
        g = uuid.UUID(str(value))
        return g.bytes_le.hex() if target == "oracle" else str(g)
    return str(value)


def generate(v, context, rng, state):
    if v.get("nullPercent", 0) and rng.next() % 1_000_000 < v["nullPercent"] * 10000:
        return None
    kind = v.get("generator", "sequence")
    lo, hi = D(str(v.get("min", 1))), D(str(v.get("max", 1000)))
    if kind == "sequence":
        raw = int(lo) + context["sequence"] - 1
    elif kind in ("integer", "timestamp"):
        raw = int(lo) + rng.next() % (int(hi) - int(lo) + 1)
    elif kind == "decimal":
        raw = (lo + (hi - lo) * D(rng.next() >> 11) / D(9007199254740992)).quantize(
            D(1).scaleb(-v.get("scale", 2))
        )
    elif kind == "text":
        raw = v.get("prefix", "")
        for key, val in context.items():
            raw = raw.replace("{" + key + "}", str(val))
        raw += "".join(ALPHABET[rng.next() % 62] for _ in range(v.get("length", 32)))
    elif kind == "choice":
        raw = v["choices"][rng.next() % len(v["choices"])]
    elif kind == "reference":
        raw = 1 + rng.next() % state.rows[v["reference"]]
    elif kind == "guid":
        raw = uuid.UUID(
            bytes_le=rng.next().to_bytes(8, "little") + rng.next().to_bytes(8, "little")
        )
    elif kind == "boolean":
        raw = rng.next() % 2 == 0
    elif kind == "constant":
        raw = v.get("constant")
    elif kind == "key":
        raw = context["key"]
    elif kind == "client":
        raw = context["client"]
    elif kind == "sample":
        values = state.samples[sample_key(v)]["values"]
        raw = values[rng.next() % len(values)]
    else:
        raise ValueError("Unknown generator " + kind)
    if kind == "timestamp":
        raw = dt.datetime(1970, 1, 1) + dt.timedelta(seconds=raw)
    return text(raw, v.get("type", "int64"), state.run["target"])


class SqlIO:
    def __init__(self, run):
        self.db = Database(run)
        self.c = self.db.connection
        self.t = run["target"]
        self.prepare = run["config"]["customWorkload"].get("prepareCommands", True)
        if self.t == "oracle":
            self.c.call_timeout = (
                run["config"]["customWorkload"].get("commandTimeoutSeconds", 10) * 1000
            )
        if self.t == "sqlite":
            self.c.execute("PRAGMA foreign_keys=ON")

    def query(self, sql, values=(), result="nonQuery"):
        cursor = self.c if self.t == "duckDb" else self.c.cursor()
        try:
            if self.t == "oracle" and values:
                import oracledb

                cursor.setinputsizes(
                    *[
                        (
                            oracledb.DB_TYPE_CLOB
                            if isinstance(v, str) and len(v) > 2000
                            else None
                        )
                        for v in values
                    ]
                )
            if self.t in ("postgreSql", "cockroachDb"):
                cursor.execute(sql, values or None, prepare=self.prepare)
            elif values:
                cursor.execute(sql, values)
            else:
                cursor.execute(sql)
            if result != "nonQuery":
                rows = cursor.fetchall()
                if result == "scalar":
                    return (rows[0][0] if rows else None), (
                        1 if rows and rows[0][0] is not None else 0
                    )
                return None, len(rows)
            if self.t == "duckDb" and cursor.description:
                rows = cursor.fetchall()
                return None, (
                    int(rows[0][0]) if rows and isinstance(rows[0][0], int) else 0
                )
            return None, max(0, cursor.rowcount)
        finally:
            if self.t != "duckDb":
                cursor.close()

    def begin(self, isolation="default"):
        if self.t in ("sqlite", "duckDb"):
            if isolation not in ("default", "serializable"):
                raise ValueError("Unsupported embedded isolation " + isolation)
            self.query("BEGIN TRANSACTION")
            return
        levels = {
            "readCommitted": "READ COMMITTED",
            "repeatableRead": "REPEATABLE READ",
            "serializable": "SERIALIZABLE",
        }
        if self.t in ("mySql", "mariaDb"):
            if isolation != "default":
                self.query("SET TRANSACTION ISOLATION LEVEL " + levels[isolation])
            self.c.autocommit(False)
        else:
            self.c.autocommit = False
            if isolation != "default":
                self.query("SET TRANSACTION ISOLATION LEVEL " + levels[isolation])

    def end(self, commit):
        if self.t in ("sqlite", "duckDb"):
            self.query("COMMIT" if commit else "ROLLBACK")
        else:
            self.c.commit() if commit else self.c.rollback()
            if self.t in ("mySql", "mariaDb"):
                self.c.autocommit(True)
            else:
                self.c.autocommit = True

    def close(self):
        self.db.close()


class State:
    def __init__(self, run):
        self.run = run
        self.p = run["sqlProgram"]
        self.w = run["config"]["customWorkload"]
        self.lock = threading.Lock()
        self.reset()
        self.rows = {t["definition"]["name"]: t["rows"] for t in self.p["datasets"]}

    def reset(self):
        self.sequence = 0
        self.completed = 0
        self.by_operation = {}
        self.samples = {}

    def next(self):
        with self.lock:
            self.sequence += 1
            return self.sequence

    def builtin(self, name, seq=0, client=0, key=0):
        n = name.lower()
        values = {
            "key": key,
            "sequence": self.run["rows"] + seq,
            "client": client,
            "baseRows".lower(): self.run["rows"],
            "seed": int(self.run["config"].get("seed", 42)),
            "completed": self.completed,
        }
        if n in values:
            return str(values[n])
        if n.startswith("rows_"):
            return str(next(v for k, v in self.rows.items() if k.lower() == n[5:]))
        if n.startswith("completed_"):
            return str(self.by_operation.get(n[10:], 0))
        raise ValueError("Unknown builtin " + name)

    def checks(self, io, before):
        for item in self.p["checks"]:
            check = item["definition"]
            if not check.get("before" if before else "after", True):
                continue
            cmd = item["command"]
            value, _ = io.query(
                cmd["sql"], [self.builtin(n) for n in cmd["names"]], "scalar"
            )
            expected = check.get("expected", "0")
            expected = (
                self.run["rows"]
                if expected == "baseRows"
                else (
                    self.rows[expected[5:]]
                    if expected.startswith("rows:")
                    else expected
                )
            )
            a, b = D(str(value)), D(str(expected))
            comparison = check.get("comparison", "equal")
            if not (
                a == b
                if comparison == "equal"
                else a >= b if comparison == "atLeast" else a <= b
            ):
                raise ValueError(check["name"] + ": integrity check failed")


class Session:
    def __init__(self, state, client, selected, warmup=False):
        self.s = state
        self.io = SqlIO(state.run)
        self.client = client
        self.selected = selected
        self.ops = [
            op
            for op in state.p["operations"]
            if (selected is None or op["definition"]["id"] == selected)
            and (not warmup or op["definition"].get("readOnly", True))
        ]
        self.weight = sum(op["definition"].get("weight", 1) for op in self.ops)
        self.rng = Random(
            seed(
                int(state.run["config"].get("seed", 42)) ^ state.run["repeat"],
                "client",
                client,
            )
        )
        self.stats = {}

    def execute(self, operation, key, row=None, batch=None):
        before = time.perf_counter()
        op = self.ops[0]
        if self.selected is None:
            pick = self.rng.next() % self.weight
            for candidate in self.ops:
                pick -= candidate["definition"].get("weight", 1)
                if pick < 0:
                    op = candidate
                    break
        definition = op["definition"]
        ident = definition["id"]
        self.last_operation = ident
        stat = self.stats.setdefault(
            ident,
            dict(
                id=ident,
                name=definition["name"],
                successfulOperations=0,
                successfulRows=0,
                errors=0,
                service=Histogram(),
                end=Histogram(),
            ),
        )
        seq = self.s.next()
        ctx = dict(row=seq, sequence=seq, client=self.client, key=key)
        transaction = False
        try:
            values = {
                v["name"].lower(): generate(v, ctx, self.rng, self.s)
                for v in definition.get("parameters", [])
            }
            if definition.get("transaction", False):
                self.io.begin(definition.get("isolation", "default"))
                transaction = True
            affected = 0
            for step, cmd in zip(definition["steps"], op["commands"]):
                args = [
                    (
                        values[n.lower()]
                        if n.lower() in values
                        else self.s.builtin(n, seq, self.client, key)
                    )
                    for n in cmd["names"]
                ]
                scalar, count = self.io.query(
                    cmd["sql"], args, step.get("result", "rows")
                )
                if step.get("expectedScalar") is not None:
                    expected = step["expectedScalar"]
                    actual = str(scalar)
                    try:
                        equal = D(actual) == D(expected)
                    except decimal.InvalidOperation:
                        equal = actual == expected
                    if not equal:
                        raise ValueError("Unexpected scalar result")
                if step.get("captureAs") is not None:
                    values[step["captureAs"].lower()] = text(
                        scalar, step.get("captureType", "int64"), self.s.run["target"]
                    )
                if (
                    step.get("minRows") is not None
                    and count < step["minRows"]
                    or step.get("maxRows") is not None
                    and count > step["maxRows"]
                ):
                    raise ValueError("Unexpected SQL row count")
                affected += max(0, count)
            if transaction:
                self.io.end(True)
                transaction = False
            finish = time.perf_counter()
            stat["successfulOperations"] += 1
            stat["successfulRows"] += affected
            stat["service"].add(max(1, (finish - before) * 1e6))
            stat["end"].add(max(1, (finish - getattr(self, "scheduled", before)) * 1e6))
            return affected
        except Exception:
            stat["errors"] += 1
            if transaction:
                try:
                    self.io.end(False)
                except Exception:
                    pass
            raise

    def close(self):
        self.io.close()


def breakdown(sessions):
    result = {}
    for session in sessions:
        for ident, stats in session.stats.items():
            if ident not in result:
                result[ident] = dict(
                    id=ident,
                    name=stats["name"],
                    successfulOperations=0,
                    successfulRows=0,
                    errors=0,
                    service=Histogram(),
                    end=Histogram(),
                )
            out = result[ident]
            for key in ("successfulOperations", "successfulRows", "errors"):
                out[key] += stats[key]
            out["service"].merge(stats["service"])
            out["end"].merge(stats["end"])
    return [
        dict(
            id=s["id"],
            name=s["name"],
            successfulOperations=s["successfulOperations"],
            successfulRows=s["successfulRows"],
            errors=s["errors"],
            serviceLatency=s["service"].snapshot(),
            endToEndLatency=s["end"].snapshot(),
        )
        for s in result.values()
    ]


def benchmark(run, config, measure, base, emit, check, cancel):
    if not run.get("sqlProgram"):
        raise ValueError("Missing compiled SQL program; update coordinator")
    state = State(run)
    until = time.monotonic() + 90
    while True:
        check()
        try:
            admin = SqlIO(run)
            break
        except Exception:
            if run["target"] in ("sqlite", "duckDb") or time.monotonic() >= until:
                raise
            cancel.wait(0.5)

    def setup(phase, action, load=False):
        emit("phase_start", dict(phase=phase, targetSeconds=0))
        start = time.perf_counter()
        try:
            count = action()
            elapsed = max(0.000001, time.perf_counter() - start)
            emit("phase_end", {})
            result = base(phase)
            result.update(
                unit="rows/s" if load else "seconds",
                elapsedSeconds=elapsed,
                successfulOperations=1,
                successfulRows=count or 0,
                rowsPerSecond=count / elapsed if load else None,
                verified=True,
            )
            emit("result", result)
        except Exception as error:
            emit("phase_end", {})
            result = base(phase)
            result.update(
                unit="seconds",
                errors=1,
                verified=False,
                errorExamples={str(error)[:500]: 1},
            )
            emit("result", result)
            raise

    def schema():
        state.reset()
        if run["target"] == "sqlite":
            admin.query("PRAGMA foreign_keys=OFF")
        try:
            for sql in state.p["drop"] + state.p["schema"]:
                check()
                admin.query(sql)
        finally:
            if run["target"] == "sqlite":
                admin.query("PRAGMA foreign_keys=ON")

    def load():
        count = 0
        for table in state.p["datasets"]:
            definition = table["definition"]
            name = definition["name"]
            cmd = table["insert"]
            columns = definition["columns"]
            reservoirs = []
            for op in state.p["operations"]:
                for value in op["definition"].get("parameters", []):
                    if (
                        value.get("generator") == "sample"
                        and value.get("reference") == name
                        and sample_key(value) not in state.samples
                    ):
                        reservoir = dict(
                            definition=value,
                            values=[],
                            seen=0,
                            rng=Random(seed(config.get("seed", 42), sample_key(value))),
                        )
                        state.samples[sample_key(value)] = reservoir
                        reservoirs.append(reservoir)
            source = (
                open(table["inputFile"], encoding="utf-8")
                if table.get("inputFile")
                else None
            )
            transaction = False
            try:
                for index in range(1, table["rows"] + 1):
                    check()
                    if not transaction:
                        admin.begin()
                        transaction = True
                    if source:
                        values = json.loads(next(source))
                    else:
                        rng = Random(
                            int(table["seed"]) ^ ((index * 0xD6E8FEB86659FD93) & MASK)
                        )
                        ctx = dict(row=index, sequence=index, client=0, key=index)
                        values = [
                            generate(column, ctx, rng, state) for column in columns
                        ]
                    for column, value in zip(columns, values):
                        if value is None and not column.get("nullable", False):
                            raise ValueError(
                                "NULL in required column " + column["name"]
                            )
                    admin.query(cmd["sql"], values)
                    count += 1
                    for reservoir in reservoirs:
                        definition = reservoir["definition"]
                        column = next(
                            i
                            for i, col in enumerate(columns)
                            if col["name"] == definition.get("sampleColumn", "id")
                        )
                        value = values[column]
                        if value is None:
                            continue
                        reservoir["seen"] += 1
                        items = reservoir["values"]
                        size = definition.get("sampleSize", 10000)
                        if len(items) < size:
                            items.append(value)
                        else:
                            slot = reservoir["rng"].next() % reservoir["seen"]
                            if slot < len(items):
                                items[slot] = value
                    if index % config.get("batchSize", 1000) == 0:
                        admin.end(True)
                        transaction = False
                if transaction:
                    admin.end(True)
                    transaction = False
            finally:
                if transaction:
                    admin.end(False)
                if source:
                    source.close()
        return count

    try:
        metadata = admin.db.metadata()
        metadata["settings"].update(
            workload_name=state.w["name"],
            workload_sha256=state.p["fingerprint"],
            workload_mode=state.w.get("mode", "mix"),
            generator="custom-splitmix64-v1",
            parameter_binding="nullable text with explicit SQL casts",
            prepared_queries="driver-managed; explicit psycopg prepare="
            + str(state.w.get("prepareCommands", True)),
        )
        emit("metadata", metadata)
        prepared = False
        for unit in state.p["units"]:
            check()
            phase = unit["phase"]
            selected = unit.get("operation")
            prefix = "custom/setup/" + (selected or "mix") + "/"
            if not prepared or state.w.get("resetBetweenOperations", True):
                setup(prefix + "schema", schema)
                setup(prefix + "load", load, True)
                setup(
                    prefix + "indexes",
                    lambda: [admin.query(sql) for sql in state.p["afterLoad"]] and 0,
                )
                prepared = True
            if any(c["definition"].get("before", True) for c in state.p["checks"]):
                setup(prefix + "before", lambda: state.checks(admin, True))
            if config["warmupSeconds"] > 0 and any(
                op["definition"].get("readOnly", True)
                and (selected is None or op["definition"]["id"] == selected)
                for op in state.p["operations"]
            ):
                count = (
                    config["plan"]["stages"][0]["clients"]
                    if config.get("plan")
                    else run["clients"]
                )
                sessions = [Session(state, i + 1, selected, True) for i in range(count)]
                try:
                    warm = measure(
                        sessions,
                        [0] * count,
                        [0],
                        phase,
                        config["warmupSeconds"],
                        count,
                        None,
                        None,
                        False,
                        config,
                    )
                    if warm["errors"]:
                        raise ValueError(
                            "Custom warmup failed: " + str(warm["errorExamples"])
                        )
                finally:
                    for session in sessions:
                        session.close()
            sessions = []
            sequences = []
            try:
                stages = (
                    config["plan"]["stages"]
                    if config.get("plan")
                    else [
                        dict(
                            name="Load",
                            clients=run["clients"],
                            seconds=config["durationSeconds"],
                            operationsPerSecond=(
                                config["targetOpsPerSecond"]
                                if config["loadModel"] == "constantRate"
                                else None
                            ),
                        )
                    ]
                )
                for index, stage in enumerate(stages):
                    check()
                    while len(sessions) < stage["clients"]:
                        sessions.append(Session(state, len(sessions) + 1, selected))
                        sequences.append(0)
                    for session in sessions:
                        session.stats = {}
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
                            phase=phase,
                            targetSeconds=stage["seconds"],
                            stage=stamp,
                            clients=stage["clients"],
                        ),
                    )
                    result = measure(
                        sessions,
                        sequences,
                        [0],
                        phase,
                        stage["seconds"],
                        stage["clients"],
                        stage.get("operationsPerSecond"),
                        stamp,
                        True,
                        config,
                    )
                    emit("phase_end", {})
                    details = breakdown(sessions[: stage["clients"]])
                    result["operationBreakdown"] = details
                    state.completed += result["successfulOperations"]
                    for item in details:
                        state.by_operation[item["id"]] = (
                            state.by_operation.get(item["id"], 0)
                            + item["successfulOperations"]
                        )
                    result["verified"] = (
                        result["successfulOperations"] > 0 and not cancel.is_set()
                    )
                    try:
                        if not cancel.is_set():
                            state.checks(admin, False)
                    except Exception:
                        result["verified"] = False
                        emit("result", result)
                        raise
                    emit("result", result)
                    check()
            finally:
                for session in sessions:
                    session.close()
    finally:
        admin.close()
