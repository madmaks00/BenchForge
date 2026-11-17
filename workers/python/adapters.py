"""Native Python drivers. Connections never cross client threads."""

import base64
import http.client
import importlib.metadata
import json
import sqlite3
import urllib.parse


def values(row):
    return (
        row["id"],
        row["groupId"],
        row["amountCents"],
        row["version"],
        row["payload"],
    )


def document(row):
    return dict(
        _id=row["id"],
        group_id=row["groupId"],
        amount_cents=row["amountCents"],
        version=row["version"],
        payload=row["payload"],
    )


class Database:
    def __new__(cls, run):
        from extended import TARGETS, SqlDatabase, SearchDatabase

        if run["target"] in TARGETS:
            return (
                SearchDatabase
                if run["target"] in ("elasticsearch", "openSearch")
                else SqlDatabase
            )(run)
        return super().__new__(cls)

    def __init__(self, run):
        self.connection = None
        try:
            self._open(run)
        except BaseException:
            self.close()
            raise

    def _open(self, run):
        self.run, self.target, self.ep = run, run["target"], run["endpoint"]
        self.prefix = "dblab:" + run["runId"] + ":"
        self.connection = self.collection = None
        ep = self.ep
        if self.target == "sqlite":
            self.connection = sqlite3.connect(
                ep["filePath"],
                timeout=10,
                isolation_level=None,
                check_same_thread=False,
            )
            self.connection.execute("PRAGMA journal_mode=WAL")
            self.connection.execute("PRAGMA synchronous=FULL")
            self.connection.execute("PRAGMA busy_timeout=10000")
            self.placeholder = "?"
        elif self.target == "postgreSql":
            import psycopg

            self.connection = psycopg.connect(
                host=ep["host"],
                port=ep["port"],
                user=ep["username"],
                password=ep["password"],
                dbname=ep["database"],
                autocommit=True,
                connect_timeout=5,
                options="-c synchronous_commit=on -c statement_timeout=10000",
                prepare_threshold=0,
            )
            self.placeholder = "%s"
        elif self.target in ("mySql", "mariaDb"):
            import pymysql

            self.connection = pymysql.connect(
                host=ep["host"],
                port=ep["port"],
                user=ep["username"],
                password=ep["password"],
                database=ep["database"],
                autocommit=True,
                connect_timeout=5,
                read_timeout=15,
                write_timeout=15,
                charset="utf8mb4",
            )
            self.placeholder = "%s"
        elif self.target == "mongoDb":
            from pymongo import MongoClient
            from pymongo.write_concern import WriteConcern

            self.connection = MongoClient(
                host=ep["host"],
                port=ep["port"],
                username=ep["username"],
                password=ep["password"],
                authSource="admin",
                retryWrites=False,
                serverSelectionTimeoutMS=5000,
                connectTimeoutMS=5000,
                socketTimeoutMS=600000,
                maxPoolSize=max(4, run["clients"] + 2),
            )
            self.db = self.connection[ep["database"]]
            self.collection = self.db.get_collection(
                "records", write_concern=WriteConcern(w=1, j=True, wtimeout=10000)
            )
            self.db.command("ping")
        elif self.target in ("redis", "valkey"):
            import redis

            self.connection = redis.Redis(
                host=ep["host"],
                port=ep["port"],
                password=ep["password"],
                socket_timeout=10,
                socket_connect_timeout=5,
                decode_responses=True,
            )
            self.connection.ping()
        else:
            self.connection = http.client.HTTPConnection(
                ep["host"], ep["port"], timeout=600
            )
            self.query("SELECT 1")
        if self.target in ("sqlite", "postgreSql", "mySql", "mariaDb"):
            p = self.placeholder
            self.sql = dict(
                PointRead=f"SELECT id,group_id,amount_cents,version,payload FROM records WHERE id={p}",
                RangeRead=f"SELECT id,group_id,amount_cents,version,payload FROM records WHERE id>={p} ORDER BY id LIMIT 100",
                Aggregate=f"SELECT group_id,COUNT(*),SUM(amount_cents) FROM records WHERE group_id={p} GROUP BY group_id",
                Update=f"UPDATE records SET version=version+1 WHERE id={p}",
                Delete=f"DELETE FROM records WHERE id={p}",
                Insert="INSERT INTO records(id,group_id,amount_cents,version,payload) VALUES("
                + ",".join([p] * 5)
                + ")",
            )

    def cursor(self):
        return self.connection.cursor()

    def execute_sql(self, query, parameters=(), read=False):
        cursor = self.cursor()
        try:
            cursor.execute(query, parameters)
            return cursor.fetchall() if read else cursor.rowcount
        finally:
            cursor.close()

    def query(self, query, body=""):
        url = "/?" + urllib.parse.urlencode(
            dict(database=self.ep["database"], query=query)
        )
        credentials = base64.b64encode(
            (self.ep["username"] + ":" + self.ep["password"]).encode()
        ).decode()
        self.connection.request(
            "POST",
            url,
            body=body.encode(),
            headers={
                "Authorization": "Basic " + credentials,
                "Content-Type": "text/plain",
            },
        )
        response = self.connection.getresponse()
        result = response.read().decode()
        if response.status != 200:
            raise RuntimeError(f"ClickHouse HTTP {response.status}: {result[:350]}")
        return result

    def reset(self):
        if self.target == "mongoDb":
            self.db.drop_collection("records")
            self.db.create_collection("records")
        elif self.target in ("redis", "valkey"):
            pass  # Coordinator creates an owned empty server for every case.
        elif self.target == "clickHouse":
            self.query("DROP TABLE IF EXISTS records")
            self.query(
                "CREATE TABLE records (id Int64,group_id Int32,amount_cents Int64,version Int64,payload String) ENGINE=MergeTree ORDER BY id"
            )
        else:
            self.execute_sql("DROP TABLE IF EXISTS records")
            text = "LONGTEXT" if self.target in ("mySql", "mariaDb") else "TEXT"
            self.execute_sql(
                f"CREATE TABLE records (id BIGINT PRIMARY KEY,group_id INTEGER NOT NULL,amount_cents BIGINT NOT NULL,version BIGINT NOT NULL,payload {text} NOT NULL)"
            )

    def batch(self, rows):
        if self.target == "mongoDb":
            self.collection.insert_many([document(row) for row in rows], ordered=True)
        elif self.target in ("redis", "valkey"):
            with self.connection.pipeline(transaction=False) as pipeline:
                for row in rows:
                    fields = document(row)
                    identifier = fields.pop("_id")
                    pipeline.hset(self.prefix + str(identifier), mapping=fields)
                pipeline.execute()
        elif self.target == "clickHouse":
            self.query(
                "INSERT INTO records FORMAT JSONEachRow",
                "".join(
                    json.dumps(
                        dict(
                            zip(
                                (
                                    "id",
                                    "group_id",
                                    "amount_cents",
                                    "version",
                                    "payload",
                                ),
                                values(row),
                            )
                        ),
                        separators=(",", ":"),
                    )
                    + "\n"
                    for row in rows
                ),
            )
        elif self.target == "postgreSql":
            with self.connection.cursor() as cursor:
                with cursor.copy(
                    "COPY records(id,group_id,amount_cents,version,payload) FROM STDIN (FORMAT BINARY)"
                ) as copy:
                    copy.set_types(["int8", "int4", "int8", "int8", "text"])
                    for row in rows:
                        copy.write_row(values(row))
        elif self.target == "sqlite":
            self.connection.execute("BEGIN")
            try:
                self.connection.executemany(self.sql["Insert"], map(values, rows))
                self.connection.execute("COMMIT")
            except BaseException:
                self.connection.execute("ROLLBACK")
                raise
        else:
            with self.connection.cursor() as cursor:
                cursor.executemany(self.sql["Insert"], list(map(values, rows)))

    def prepare(self):
        if self.target == "mongoDb":
            self.collection.create_index("group_id")
        elif self.target not in ("redis", "valkey", "clickHouse"):
            self.execute_sql("CREATE INDEX records_group_id ON records(group_id)")
            self.execute_sql(
                "ANALYZE TABLE records"
                if self.target in ("mySql", "mariaDb")
                else "ANALYZE records",
                read=self.target in ("mySql", "mariaDb"),
            )

    def digest(self):
        if self.target == "mongoDb":
            rows = list(
                self.collection.aggregate(
                    [
                        {
                            "$group": {
                                "_id": None,
                                "n": {"$sum": 1},
                                "ids": {"$sum": "$_id"},
                                "amount": {"$sum": "$amount_cents"},
                            }
                        }
                    ],
                    maxTimeMS=600000,
                )
            )
            return (
                (rows[0]["n"], rows[0]["ids"], rows[0]["amount"]) if rows else (0, 0, 0)
            )
        if self.target in ("redis", "valkey"):
            script = "local r=redis.call('SCAN',ARGV[1],'MATCH',ARGV[2]..'*','COUNT',1000); local n=0; local ids=0; local a=0; for _,k in ipairs(r[2]) do n=n+1; ids=ids+tonumber(string.sub(k,string.len(ARGV[2])+1)); a=a+tonumber(redis.call('HGET',k,'amount_cents')); end; return {r[1],n,ids,a}"
            cursor, n, ids, amount = 0, 0, 0, 0
            while True:
                result = self.connection.eval(script, 0, cursor, self.prefix)
                cursor = int(result[0])
                n += int(result[1])
                ids += int(result[2])
                amount += int(result[3])
                if not cursor:
                    return n, ids, amount
        if self.target == "clickHouse":
            return tuple(
                map(
                    int,
                    self.query(
                        "SELECT count(),sum(id),sum(amount_cents) FROM records FORMAT TabSeparated"
                    )
                    .strip()
                    .split("\t"),
                )
            )
        return tuple(
            map(
                int,
                self.execute_sql(
                    "SELECT COUNT(*),COALESCE(SUM(id),0),COALESCE(SUM(amount_cents),0) FROM records",
                    read=True,
                )[0],
            )
        )

    def count(self):
        if self.target == "mongoDb":
            return self.collection.count_documents({})
        if self.target in ("redis", "valkey"):
            return self.connection.dbsize()
        if self.target == "clickHouse":
            return int(
                self.query("SELECT count() FROM records FORMAT TabSeparated").strip()
            )
        return int(self.execute_sql("SELECT COUNT(*) FROM records", read=True)[0][0])

    def metadata(self):
        settings = dict(
            connections="one connection/client thread; Python GIL enabled unless runtime says otherwise",
            build="Release",
            generator="splitmix64-ascii-v1",
            gc_metrics="not measured; null",
            batch_transport="native driver batching",
        )
        if self.target == "sqlite":
            version, driver = (
                sqlite3.sqlite_version,
                "stdlib sqlite3 / " + sqlite3.sqlite_version,
            )
            for key in ("journal_mode", "synchronous", "cache_size"):
                settings[key] = str(self.execute_sql("PRAGMA " + key, read=True)[0][0])
            settings["batch_transport"] = "prepared executemany in transaction"
        elif self.target in ("postgreSql", "mySql", "mariaDb"):
            package = "psycopg" if self.target == "postgreSql" else "pymysql"
            version = str(self.execute_sql("SELECT VERSION()", read=True)[0][0])
            driver = package + " / " + importlib.metadata.version(package)
            for key in (
                ("synchronous_commit", "fsync", "shared_buffers", "max_connections")
                if self.target == "postgreSql"
                else (
                    "innodb_flush_log_at_trx_commit",
                    "innodb_buffer_pool_size",
                    "max_connections",
                )
            ):
                query = (
                    "SHOW " + key
                    if self.target == "postgreSql"
                    else "SELECT @@GLOBAL." + key
                )
                settings[key] = str(self.execute_sql(query, read=True)[0][0])
            settings["batch_transport"] = (
                "binary COPY"
                if self.target == "postgreSql"
                else "multi-row parameterized INSERT"
            )
            settings["prepared_queries"] = (
                "true"
                if self.target == "postgreSql"
                else "driver parameter binding; no server prepare"
            )
        elif self.target == "mongoDb":
            version = str(self.db.command("buildInfo")["version"])
            driver = "pymongo / " + importlib.metadata.version("pymongo")
            settings.update(
                write_concern="w=1,j=true", retry_writes="false", topology="standalone"
            )
        elif self.target in ("redis", "valkey"):
            info = self.connection.info("server")
            version = info.get("valkey_version", info.get("redis_version", "unknown"))
            driver = "redis-py / " + importlib.metadata.version("redis")
            settings.update(
                durability_profile="memory cache: AOF and RDB disabled",
                batch_transport="pipelined HSET; non-atomic",
            )
        else:
            version = self.query("SELECT version() FORMAT TabSeparated").strip()
            driver = "stdlib http.client"
            settings.update(
                transport="HTTP / JSONEachRow / uncompressed",
                insert="synchronous HTTP; async_insert=0",
                table="MergeTree ORDER BY id; no unique constraint",
            )
        return dict(
            engine="python",
            runtime=__import__("sys").version.splitlines()[0],
            driver=driver,
            serverVersion=version,
            settings=settings,
        )

    def execute(self, operation, key, row=None, batch=None):
        if operation == "BatchInsert":
            self.batch(batch)
            return len(batch)
        if self.target == "mongoDb":
            collection = self.collection
            if operation == "PointRead":
                result = collection.find_one({"_id": key}, max_time_ms=10000)
                if (
                    result is None
                    or result["_id"] != key
                    or not isinstance(result["payload"], str)
                ):
                    raise ValueError("Invalid MongoDB row")
                return 1
            if operation == "RangeRead":
                result = list(
                    collection.find({"_id": {"$gte": key}})
                    .sort("_id", 1)
                    .limit(100)
                    .max_time_ms(10000)
                )
                if not result:
                    raise ValueError("Empty range")
                return len(result)
            if operation == "Aggregate":
                result = list(
                    collection.aggregate(
                        [
                            {"$match": {"group_id": (key - 1) % 100}},
                            {
                                "$group": {
                                    "_id": "$group_id",
                                    "n": {"$sum": 1},
                                    "amount": {"$sum": "$amount_cents"},
                                }
                            },
                        ],
                        maxTimeMS=10000,
                    )
                )
                if not result:
                    raise ValueError("Empty aggregate")
                return len(result)
            if operation == "Insert":
                collection.insert_one(document(row))
                return 1
            affected = (
                collection.update_one(
                    {"_id": key}, {"$inc": {"version": 1}}
                ).matched_count
                if operation == "Update"
                else collection.delete_one({"_id": key}).deleted_count
            )
        elif self.target in ("redis", "valkey"):
            client = self.connection
            name = self.prefix + str(key)
            if operation == "PointRead":
                if len(client.hgetall(name)) != 4:
                    raise ValueError("Incomplete Redis hash")
                return 1
            if operation == "Update":
                client.hincrby(name, "version", 1)
                return 1
            if operation == "Insert":
                self.batch([row])
                return 1
            affected = client.delete(name)
        elif self.target == "clickHouse":
            query = (
                f"SELECT id,group_id,amount_cents,version,payload FROM records WHERE id>={key} ORDER BY id LIMIT 100"
                if operation == "RangeRead"
                else f"SELECT group_id,count(),sum(amount_cents) FROM records WHERE group_id={(key - 1) % 100} GROUP BY group_id"
                if operation == "Aggregate"
                else f"SELECT count(),sum(amount_cents),avg(amount_cents) FROM records WHERE id>={key} AND id<{key + max(100, self.run['rows'] // 100)}"
            )
            result = [
                json.loads(line)
                for line in self.query(
                    query + " SETTINGS max_execution_time=10 FORMAT JSONEachRow"
                ).splitlines()
                if line
            ]
            if not result:
                raise ValueError("Empty ClickHouse response")
            return len(result)
        else:
            parameters = (
                values(row)
                if operation == "Insert"
                else (((key - 1) % 100 if operation == "Aggregate" else key),)
            )
            read = operation in ("PointRead", "RangeRead", "Aggregate")
            result = self.execute_sql(self.sql[operation], parameters, read=read)
            if read:
                if (
                    not result
                    or operation == "PointRead"
                    and (len(result) != 1 or int(result[0][0]) != key)
                ):
                    raise ValueError("Invalid SQL response")
                for value in result:
                    if operation != "Aggregate" and not isinstance(value[4], str):
                        raise ValueError("Payload was not read")
                return len(result)
            affected = result
        if affected != 1:
            raise ValueError("Write did not affect one record")
        return affected

    def close(self):
        if self.connection is not None:
            self.connection.close()
