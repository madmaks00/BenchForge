"""Additional native DB-API and search adapters; no subprocess bridge."""

import http.client
import importlib.metadata
import json
import re
import sys
from portable import row as make_row

TARGETS = {
    "sqlServer",
    "duckDb",
    "oracle",
    "cockroachDb",
    "elasticsearch",
    "openSearch",
}


def values(r):
    return r["id"], r["groupId"], r["amountCents"], r["version"], r["payload"]


class SqlDatabase:
    def __init__(self, run):
        self.run, self.target, self.connection = run, run["target"], None
        e, t = run["endpoint"], self.target
        if t != "duckDb" and not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*", e["database"]):
            raise ValueError("Invalid owned database name")
        try:
            if t == "duckDb":
                import duckdb

                c = run["config"]
                self.connection = duckdb.connect(
                    e["filePath"],
                    config={
                        "memory_limit": str(c.get("memoryMb", 512)) + "MB",
                        "threads": str(
                            max(1, __import__("math").ceil(c.get("cpuLimit", 2)))
                        ),
                    },
                )
                self.package, self.placeholder = "duckdb", "?"
            elif t == "oracle":
                import oracledb

                options = dict(
                    password=e["password"],
                    dsn=f"{e['host']}:{e['port']}/{e['database']}",
                    stmtcachesize=0,
                )
                with oracledb.connect(user="SYSTEM", **options) as admin:
                    with admin.cursor() as cursor:
                        cursor.execute(
                            "SELECT COUNT(*) FROM all_users WHERE username='DBLAB'"
                        )
                        if cursor.fetchone()[0] == 0:
                            password = e["password"].replace('"', '""')
                            cursor.execute(
                                'CREATE USER DBLAB IDENTIFIED BY "' + password + '"'
                            )
                            cursor.execute(
                                "GRANT CREATE SESSION,CREATE TABLE,UNLIMITED TABLESPACE TO DBLAB"
                            )
                self.connection = oracledb.connect(user=e["username"], **options)
                self.connection.autocommit = True
                self.connection.call_timeout = 600000
                self.package, self.placeholder = "oracledb", ":1"
            elif t == "sqlServer":
                import pytds

                options = dict(
                    server=e["host"],
                    port=e["port"],
                    user=e["username"],
                    password=e["password"],
                    autocommit=True,
                    timeout=600,
                    login_timeout=15,
                )
                with pytds.connect(database="master", **options) as admin:
                    with admin.cursor() as cursor:
                        cursor.execute(
                            "IF DB_ID(%s) IS NULL CREATE DATABASE ["
                            + e["database"]
                            + "]",
                            (e["database"],),
                        )
                        cursor.execute(
                            "ALTER DATABASE [" + e["database"] + "] SET RECOVERY SIMPLE"
                        )
                        cursor.execute(
                            "ALTER DATABASE ["
                            + e["database"]
                            + "] SET DELAYED_DURABILITY=DISABLED"
                        )
                self.connection = pytds.connect(database=e["database"], **options)
                self.package, self.placeholder = "python-tds", "%s"
                self.query(
                    "SET QUOTED_IDENTIFIER ON; SET NOCOUNT OFF; SET XACT_ABORT ON"
                )
            else:
                import psycopg

                options = dict(
                    host=e["host"],
                    port=e["port"],
                    user=e["username"],
                    password=e["password"],
                    sslmode="disable",
                    autocommit=True,
                    connect_timeout=15,
                    prepare_threshold=0,
                )
                with psycopg.connect(dbname="defaultdb", **options) as admin:
                    admin.execute(
                        'CREATE DATABASE IF NOT EXISTS "' + e["database"] + '"'
                    )
                self.connection = psycopg.connect(dbname=e["database"], **options)
                self.package, self.placeholder = "psycopg", "%s"
        except BaseException:
            self.close()
            raise

    def query(self, text, params=(), read=False):
        if self.target == "duckDb":
            result = self.connection.execute(text, params)
            return result.fetchall() if read else (result.fetchone() or (0,))[0]
        with self.connection.cursor() as cursor:
            if self.target == "oracle" and len(params) == 5:
                import oracledb

                cursor.setinputsizes(None, None, None, None, oracledb.DB_TYPE_CLOB)
            cursor.execute(text, params)
            if read:
                return [
                    tuple(v.read() if hasattr(v, "read") else v for v in r)
                    for r in cursor.fetchall()
                ]
            return cursor.rowcount

    def reset(self):
        if self.target == "oracle":
            self.query(
                "BEGIN EXECUTE IMMEDIATE 'DROP TABLE records PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;"
            )
            definition = "id NUMBER(19) PRIMARY KEY,group_id NUMBER(10) NOT NULL,amount_cents NUMBER(19) NOT NULL,version NUMBER(19) NOT NULL,payload CLOB NOT NULL"
        else:
            self.query("DROP TABLE IF EXISTS records")
            definition = (
                "id BIGINT PRIMARY KEY,group_id INTEGER NOT NULL,amount_cents BIGINT NOT NULL,version BIGINT NOT NULL,payload "
                + ("NVARCHAR(MAX)" if self.target == "sqlServer" else "TEXT")
            )
        self.query(
            "CREATE TABLE records ("
            + definition
            + ("" if definition.endswith("NOT NULL") else " NOT NULL")
            + ")"
        )

    def insert_sql(self):
        p = (
            [":" + str(i) for i in range(1, 6)]
            if self.target == "oracle"
            else [self.placeholder] * 5
        )
        return (
            "INSERT INTO records(id,group_id,amount_cents,version,payload) VALUES("
            + ",".join(p)
            + ")"
        )

    def batch(self, rows):
        duck = self.target == "duckDb"
        if duck:
            self.query("BEGIN TRANSACTION")
        else:
            self.connection.autocommit = False
        try:
            for r in rows:
                self.query(self.insert_sql(), values(r))
            self.query("COMMIT") if duck else self.connection.commit()
        except BaseException:
            self.query("ROLLBACK") if duck else self.connection.rollback()
            raise
        finally:
            if not duck:
                self.connection.autocommit = True

    def prepare(self):
        self.query("CREATE INDEX records_group_id ON records(group_id)")
        self.query(
            {
                "oracle": "BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER,'RECORDS'); END;",
                "sqlServer": "UPDATE STATISTICS records WITH FULLSCAN",
                "cockroachDb": "CREATE STATISTICS records_stats FROM records",
                "duckDb": "ANALYZE records",
            }[self.target]
        )

    def digest(self):
        return tuple(
            map(
                int,
                self.query(
                    "SELECT COUNT(*),COALESCE(SUM(id),0),COALESCE(SUM(amount_cents),0) FROM records",
                    read=True,
                )[0],
            )
        )

    def count(self):
        return int(self.query("SELECT COUNT(*) FROM records", read=True)[0][0])

    def metadata(self):
        text = {
            "oracle": "SELECT banner FROM v$version WHERE ROWNUM=1",
            "sqlServer": "SELECT CAST(SERVERPROPERTY('ProductVersion') AS VARCHAR(100))",
        }.get(self.target, "SELECT version()")
        return dict(
            engine="python",
            runtime=sys.version.splitlines()[0],
            driver=self.package + " / " + importlib.metadata.version(self.package),
            serverVersion=str(self.query(text, read=True)[0][0]),
            settings=dict(
                connections="one DB-API connection/client thread",
                prepared_queries="driver parameter binding",
                batch_transport="parameterized row INSERTs in one transaction",
                generator="splitmix64-ascii-v1",
                durability_profile="embedded process"
                if self.target == "duckDb"
                else "server defaults; no application retries",
                isolation="SERIALIZABLE"
                if self.target == "cockroachDb"
                else "driver/server default",
            ),
        )

    def execute(self, operation, key, row=None, batch=None):
        if operation == "BatchInsert":
            self.batch(batch)
            return len(batch)
        p = self.placeholder
        select = "SELECT id,group_id,amount_cents,version,payload FROM records"
        texts = dict(
            PointRead=select + " WHERE id=" + p,
            RangeRead=select + " WHERE id>=" + p + " ORDER BY id",
            Aggregate="SELECT group_id,COUNT(*),SUM(amount_cents) FROM records WHERE group_id="
            + p
            + " GROUP BY group_id",
            Update="UPDATE records SET version=version+1 WHERE id=" + p,
            Delete="DELETE FROM records WHERE id=" + p,
            Insert=self.insert_sql(),
        )
        if operation == "RangeRead":
            if self.target == "sqlServer":
                texts[operation] = texts[operation].replace(
                    "SELECT ", "SELECT TOP (100) ", 1
                )
            else:
                texts[operation] += (
                    " FETCH FIRST 100 ROWS ONLY"
                    if self.target == "oracle"
                    else " LIMIT 100"
                )
        if operation == "AnalyticalScan":
            texts[operation] = (
                "SELECT COUNT(*),SUM(amount_cents),AVG(amount_cents) FROM records WHERE id>=? AND id<?"
            )
            params = (key, key + max(100, self.run["rows"] // 100))
        else:
            params = (
                values(row)
                if operation == "Insert"
                else (((key - 1) % 100 if operation == "Aggregate" else key),)
            )
        read = operation in ("PointRead", "RangeRead", "Aggregate", "AnalyticalScan")
        result = self.query(texts[operation], params, read)
        if not read:
            if result != 1:
                raise ValueError("Write did not affect one record")
            return 1
        if (
            not result
            or operation == "PointRead"
            and (len(result) != 1 or int(result[0][0]) != key)
        ):
            raise ValueError("Invalid SQL response")
        if operation in ("PointRead", "RangeRead"):
            previous = key - 1
            for r in result:
                if (
                    not isinstance(r[4], str)
                    or len(r[4]) != self.run["config"].get("payloadBytes", 256)
                    or int(r[0]) <= previous
                ):
                    raise ValueError("Invalid ID or payload")
                previous = int(r[0])
        return len(result)

    def close(self):
        if self.connection is not None:
            self.connection.close()


class SearchDatabase:
    def __init__(self, run):
        self.run = run
        e = run["endpoint"]
        self.connection = http.client.HTTPConnection(e["host"], e["port"], timeout=600)
        try:
            self.request("GET", "/")
            self.request("GET", "/_cluster/health?wait_for_status=yellow&timeout=5s")
        except BaseException:
            self.connection.close()
            raise

    def request(self, method, path, body=None, missing=False, bulk=False):
        data = (
            body
            if bulk
            else None
            if body is None
            else json.dumps(body, separators=(",", ":"))
        )
        self.connection.request(
            method,
            path,
            data.encode() if data is not None else None,
            {"Content-Type": "application/x-ndjson" if bulk else "application/json"},
        )
        response = self.connection.getresponse()
        text = response.read().decode()
        if missing and response.status == 404:
            return {}
        if not 200 <= response.status < 300:
            raise RuntimeError(
                "Search HTTP " + str(response.status) + ": " + text[:350]
            )
        result = json.loads(text)
        if result.get("timed_out") or result.get("_shards", {}).get("failed", 0):
            raise ValueError("Search timed out or returned partial shards")
        return result

    @staticmethod
    def document(r):
        payload = r["payload"]
        return dict(
            zip(
                ("id", "group_id", "amount_cents", "version", "payload", "search_text"),
                (
                    *values(r),
                    "benchforge group"
                    + str(r["groupId"])
                    + " "
                    + " ".join(payload[i : i + 16] for i in range(0, len(payload), 16)),
                ),
            )
        )

    def reset(self):
        self.request("DELETE", "/records", missing=True)
        self.request(
            "PUT",
            "/records",
            dict(
                settings={
                    "number_of_shards": 1,
                    "number_of_replicas": 0,
                    "refresh_interval": "-1",
                    "translog.durability": "request",
                },
                mappings=dict(
                    dynamic="strict",
                    properties=dict(
                        id=dict(type="long"),
                        group_id=dict(type="integer"),
                        amount_cents=dict(type="long"),
                        version=dict(type="long"),
                        payload=dict(type="keyword", index=False, doc_values=False),
                        search_text=dict(type="text", analyzer="standard"),
                    ),
                ),
            ),
        )

    def batch(self, rows):
        body = "".join(
            json.dumps({"create": {"_index": "records", "_id": str(r["id"])}})
            + "\n"
            + json.dumps(self.document(r))
            + "\n"
            for r in rows
        )
        result = self.request("POST", "/_bulk?refresh=false", body, bulk=True)
        if (
            result.get("errors")
            or len(result.get("items", [])) != len(rows)
            or any(
                item["create"].get("status") != 201 or "error" in item["create"]
                for item in result["items"]
            )
        ):
            raise ValueError("Bulk insert contains failed items")

    def prepare(self):
        self.request("PUT", "/records/_settings", {"index": {"refresh_interval": "1s"}})
        self.request("POST", "/records/_refresh")

    def digest(self):
        self.request("POST", "/records/_refresh")
        r = self.request(
            "POST",
            "/records/_search",
            dict(
                size=0,
                track_total_hits=True,
                aggs=dict(
                    ids=dict(sum=dict(field="id")),
                    amount=dict(sum=dict(field="amount_cents")),
                ),
            ),
        )
        return (
            int(r["hits"]["total"]["value"]),
            int(r["aggregations"]["ids"]["value"]),
            int(r["aggregations"]["amount"]["value"]),
        )

    def count(self):
        self.request("POST", "/records/_refresh")
        return int(self.request("GET", "/records/_count")["count"])

    def metadata(self):
        version = self.request("GET", "/")["version"]["number"]
        return dict(
            engine="python",
            runtime=sys.version.splitlines()[0],
            driver="stdlib http.client / JSON",
            serverVersion=version,
            settings=dict(
                connections="one HTTP connection/client thread",
                generator="splitmix64-ascii-v1",
                batch_transport="NDJSON bulk create; non-atomic; each item checked",
                shards="1 primary / 0 replicas",
                durability_profile="translog=request; refresh=1s after load",
                search_corpus="payload-terms16-v1",
                security="owned loopback benchmark; authentication disabled",
                retries="none",
            ),
        )

    def execute(self, operation, key, row=None, batch=None):
        path = "/records/_doc/" + str(key)
        if operation == "BatchInsert":
            self.batch(batch)
            return len(batch)
        if operation == "PointRead":
            r = self.request("GET", path)
            if (
                not r.get("found")
                or int(r["_source"]["id"]) != key
                or len(r["_source"]["payload"])
                != self.run["config"].get("payloadBytes", 256)
            ):
                raise ValueError("Invalid search document")
            return 1
        if operation in ("Update", "Insert", "Delete"):
            if operation == "Update":
                r = self.request(
                    "POST",
                    "/records/_update/"
                    + str(key)
                    + "?refresh=false&retry_on_conflict=0",
                    {
                        "script": {
                            "source": "ctx._source.version += 1",
                            "lang": "painless",
                        }
                    },
                )
            elif operation == "Insert":
                r = self.request(
                    "PUT",
                    "/records/_create/" + str(row["id"]) + "?refresh=false",
                    self.document(row),
                )
            else:
                r = self.request("DELETE", path + "?refresh=false")
            if (
                r.get("result")
                != {"Update": "updated", "Insert": "created", "Delete": "deleted"}[
                    operation
                ]
            ):
                raise ValueError("Search write was not acknowledged")
            return 1
        if operation == "Aggregate":
            r = self.request(
                "POST",
                "/records/_search",
                dict(
                    size=0,
                    track_total_hits=True,
                    query={"term": {"group_id": (key - 1) % 100}},
                    aggs={"amount": {"sum": {"field": "amount_cents"}}},
                ),
            )
            if r["hits"]["total"]["value"] <= 0:
                raise ValueError("Empty aggregate")
            float(r["aggregations"]["amount"]["value"])
            return 1
        term = None
        if operation == "FullTextSearch":
            term = make_row(
                key,
                self.run["config"].get("seed", 42),
                self.run["config"].get("payloadBytes", 256),
            )["payload"][:16]
            body = dict(size=10, query={"match": {"search_text": term}})
        elif operation == "RangeRead":
            body = dict(
                size=100, query={"range": {"id": {"gte": key}}}, sort=[{"id": "asc"}]
            )
        else:
            raise ValueError("Unsupported search operation")
        hits = self.request("POST", "/records/_search", body)["hits"]["hits"]
        previous = key - 1
        if not hits:
            raise ValueError("Empty search response")
        for hit in hits:
            r = hit["_source"]
            if (
                len(r["payload"]) != self.run["config"].get("payloadBytes", 256)
                or term
                and term.lower() not in r["search_text"].lower().split()
                or not term
                and int(r["id"]) <= previous
            ):
                raise ValueError("Invalid search response")
            previous = int(r["id"])
        return len(hits)

    def close(self):
        self.connection.close()
