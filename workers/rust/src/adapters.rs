use crate::{portable::Row, Run};
use anyhow::{bail, Context, Result};
use mongodb::{
    bson::{doc, Bson, Document},
    sync::{Client as MongoClient, Collection, Database as MongoDatabase},
};
use mysql::{prelude::Queryable, Conn, OptsBuilder, Params, Value as MyValue};
use postgres::{binary_copy::BinaryCopyInWriter, types::Type, Client as PgClient, NoTls};
use redis::Commands;
use rusqlite::{params, Connection as Sqlite};
use serde_json::{json, Value};
use std::{collections::HashMap, sync::Arc, time::Duration};

enum Backend {
    Sqlite(Sqlite),
    Postgres(PgClient),
    MySql(Conn),
    Mongo {
        _client: MongoClient,
        db: MongoDatabase,
        col: Collection<Document>,
    },
    Redis(redis::Connection),
    ClickHouse(reqwest::blocking::Client),
}
pub struct OriginalDatabase {
    run: Arc<Run>,
    backend: Backend,
    prefix: String,
    pg_statements: HashMap<String, postgres::Statement>,
    my_statements: HashMap<String, mysql::Statement>,
}
fn document(r: &Row) -> Document {
    doc! {"_id":r.id,"group_id":r.group_id,"amount_cents":r.amount_cents,"version":r.version,"payload":r.payload.clone()}
}
fn number(v: &Bson) -> Result<i64> {
    match v {
        Bson::Int32(n) => Ok(*n as i64),
        Bson::Int64(n) => Ok(*n),
        Bson::Double(n) => Ok(*n as i64),
        _ => bail!("Expected integer aggregate"),
    }
}
pub(crate) fn dependency(name: &str) -> String {
    let mut current = false;
    for line in include_str!("../Cargo.lock").lines() {
        if line.starts_with("name = ") {
            current = line.trim_start_matches("name = ").trim_matches('"') == name;
        } else if current && line.starts_with("version = ") {
            return format!(
                "{} {}",
                name,
                line.trim_start_matches("version = ").trim_matches('"')
            );
        }
    }
    name.to_owned()
}
impl OriginalDatabase {
    pub fn open(run: Arc<Run>) -> Result<Self> {
        let ep = &run.endpoint;
        let backend = match run.target.as_str() {
            "sqlite" => {
                let db = Sqlite::open(ep.file_path.as_deref().context("Missing SQLite file")?)?;
                db.busy_timeout(Duration::from_secs(10))?;
                db.execute_batch(
                    "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=10000;",
                )?;
                Backend::Sqlite(db)
            }
            "postgreSql" => {
                let mut options = postgres::Config::new();
                options
                    .host(&ep.host)
                    .port(ep.port)
                    .user(&ep.username)
                    .password(&ep.password)
                    .dbname(&ep.database)
                    .connect_timeout(Duration::from_secs(5))
                    .options("-c synchronous_commit=on -c statement_timeout=10000");
                Backend::Postgres(options.connect(NoTls)?)
            }
            "mySql" | "mariaDb" => Backend::MySql(Conn::new(
                OptsBuilder::default()
                    .ip_or_hostname(Some(ep.host.clone()))
                    .tcp_port(ep.port)
                    .user(Some(ep.username.clone()))
                    .pass(Some(ep.password.clone()))
                    .db_name(Some(ep.database.clone()))
                    .tcp_connect_timeout(Some(Duration::from_secs(5)))
                    .read_timeout(Some(Duration::from_secs(15)))
                    .write_timeout(Some(Duration::from_secs(15))),
            )?),
            "mongoDb" => {
                let mut uri = url::Url::parse(&format!("mongodb://{}:{}/", ep.host, ep.port))?;
                uri.set_username(&ep.username)
                    .map_err(|_| anyhow::anyhow!("Invalid MongoDB username"))?;
                uri.set_password(Some(&ep.password))
                    .map_err(|_| anyhow::anyhow!("Invalid MongoDB password"))?;
                uri.query_pairs_mut()
                    .append_pair("authSource", "admin")
                    .append_pair("retryWrites", "false")
                    .append_pair("w", "1")
                    .append_pair("journal", "true")
                    .append_pair("wTimeoutMS", "10000")
                    .append_pair("connectTimeoutMS", "5000")
                    .append_pair("serverSelectionTimeoutMS", "5000")
                    .append_pair("maxPoolSize", &run.clients.max(4).to_string());
                let client = MongoClient::with_uri_str(uri.as_str())?;
                let db = client.database(&ep.database);
                db.run_command(doc! {"ping":1}).run()?;
                let col = db.collection("records");
                Backend::Mongo {
                    _client: client,
                    db,
                    col,
                }
            }
            "redis" | "valkey" => {
                let mut uri = url::Url::parse(&format!("redis://{}:{}/0", ep.host, ep.port))?;
                uri.set_password(Some(&ep.password))
                    .map_err(|_| anyhow::anyhow!("Invalid Redis password"))?;
                let client = redis::Client::open(uri.as_str())?;
                let mut connection = client.get_connection_with_timeout(Duration::from_secs(5))?;
                connection.set_read_timeout(Some(Duration::from_secs(10)))?;
                connection.set_write_timeout(Some(Duration::from_secs(10)))?;
                let _: String = redis::cmd("PING").query(&mut connection)?;
                Backend::Redis(connection)
            }
            "clickHouse" => Backend::ClickHouse(
                reqwest::blocking::Client::builder()
                    .connect_timeout(Duration::from_secs(5))
                    .timeout(Duration::from_secs(600))
                    .build()?,
            ),
            _ => bail!("Unsupported target"),
        };
        let prefix = format!("dblab:{}:", run.run_id);
        let mut database = Self {
            run,
            backend,
            prefix,
            pg_statements: HashMap::new(),
            my_statements: HashMap::new(),
        };
        if database.run.target == "clickHouse" {
            database.query("SELECT 1", "")?;
        }
        Ok(database)
    }
    fn query(&mut self, query: &str, body: &str) -> Result<String> {
        if let Backend::ClickHouse(client) = &self.backend {
            let ep = &self.run.endpoint;
            let response = client
                .post(format!("http://{}:{}/", ep.host, ep.port))
                .query(&[("database", ep.database.as_str()), ("query", query)])
                .basic_auth(&ep.username, Some(&ep.password))
                .header("Content-Type", "text/plain")
                .body(body.to_owned())
                .send()?;
            let status = response.status();
            let text = response.text()?;
            if !status.is_success() {
                bail!(
                    "ClickHouse HTTP {}: {}",
                    status,
                    text.chars().take(350).collect::<String>()
                )
            };
            Ok(text)
        } else {
            bail!("Not an HTTP database")
        }
    }
    pub fn reset(&mut self) -> Result<()> {
        let text = if self.run.target == "mySql" || self.run.target == "mariaDb" {
            "LONGTEXT"
        } else {
            "TEXT"
        };
        let create=format!("CREATE TABLE records (id BIGINT PRIMARY KEY,group_id INTEGER NOT NULL,amount_cents BIGINT NOT NULL,version BIGINT NOT NULL,payload {} NOT NULL)",text);
        match &mut self.backend {
            Backend::Sqlite(db) => {
                db.execute_batch(&format!("DROP TABLE IF EXISTS records;{}", create))?;
            }
            Backend::Postgres(db) => {
                db.batch_execute(&format!("DROP TABLE IF EXISTS records;{}", create))?
            }
            Backend::MySql(db) => {
                db.query_drop("DROP TABLE IF EXISTS records")?;
                db.query_drop(create)?;
            }
            Backend::Mongo { db, col, .. } => {
                if let Err(error) = col.drop().run() {
                    if !matches!(error.kind.as_ref(),mongodb::error::ErrorKind::Command(command) if command.code==26)
                    {
                        return Err(error.into());
                    }
                };
                db.create_collection("records").run()?;
            }
            Backend::Redis(_) => {}
            Backend::ClickHouse(_) => {
                self.query("DROP TABLE IF EXISTS records", "")?;
                self.query("CREATE TABLE records (id Int64,group_id Int32,amount_cents Int64,version Int64,payload String) ENGINE=MergeTree ORDER BY id","")?;
            }
        };
        Ok(())
    }
    pub fn batch(&mut self, rows: &[Row]) -> Result<()> {
        match &mut self.backend {
            Backend::Sqlite(db) => {
                let tx = db.transaction()?;
                {
                    let mut statement = tx.prepare("INSERT INTO records VALUES(?,?,?,?,?)")?;
                    for row in rows {
                        statement.execute(params![
                            row.id,
                            row.group_id,
                            row.amount_cents,
                            row.version,
                            row.payload
                        ])?;
                    }
                }
                tx.commit()?;
            }
            Backend::Postgres(db) => {
                let sink=db.copy_in("COPY records(id,group_id,amount_cents,version,payload) FROM STDIN (FORMAT BINARY)")?;
                let mut writer = BinaryCopyInWriter::new(
                    sink,
                    &[Type::INT8, Type::INT4, Type::INT8, Type::INT8, Type::TEXT],
                );
                for r in rows {
                    writer.write(&[&r.id, &r.group_id, &r.amount_cents, &r.version, &r.payload])?;
                }
                writer.finish()?;
            }
            Backend::MySql(db) => {
                let query = format!(
                    "INSERT INTO records VALUES {}",
                    vec!["(?,?,?,?,?)"; rows.len()].join(",")
                );
                let values = rows
                    .iter()
                    .flat_map(|r| {
                        vec![
                            MyValue::Int(r.id),
                            MyValue::Int(r.group_id as i64),
                            MyValue::Int(r.amount_cents),
                            MyValue::Int(r.version),
                            MyValue::Bytes(r.payload.as_bytes().to_vec()),
                        ]
                    })
                    .collect();
                db.exec_drop(query, Params::Positional(values))?;
            }
            Backend::Mongo { col, .. } => {
                col.insert_many(rows.iter().map(document)).run()?;
            }
            Backend::Redis(db) => {
                let mut pipeline = redis::pipe();
                for r in rows {
                    pipeline
                        .cmd("HSET")
                        .arg(format!("{}{}", self.prefix, r.id))
                        .arg("group_id")
                        .arg(r.group_id)
                        .arg("amount_cents")
                        .arg(r.amount_cents)
                        .arg("version")
                        .arg(r.version)
                        .arg("payload")
                        .arg(&r.payload)
                        .ignore();
                }
                let _: () = pipeline.query(db)?;
            }
            Backend::ClickHouse(_) => {
                let mut body = String::new();
                for r in rows {
                    body.push_str(&serde_json::to_string(&json!({"id":r.id,"group_id":r.group_id,"amount_cents":r.amount_cents,"version":r.version,"payload":r.payload}))?);
                    body.push('\n');
                }
                self.query("INSERT INTO records FORMAT JSONEachRow", &body)?;
            }
        };
        Ok(())
    }
    pub fn prepare(&mut self) -> Result<()> {
        match &mut self.backend {
            Backend::Sqlite(db) => db.execute_batch(
                "CREATE INDEX records_group_id ON records(group_id); ANALYZE records;",
            )?,
            Backend::Postgres(db) => db.batch_execute(
                "CREATE INDEX records_group_id ON records(group_id); ANALYZE records;",
            )?,
            Backend::MySql(db) => {
                db.query_drop("CREATE INDEX records_group_id ON records(group_id)")?;
                db.query_drop("ANALYZE TABLE records")?;
            }
            Backend::Mongo { col, .. } => {
                col.create_index(
                    mongodb::IndexModel::builder()
                        .keys(doc! {"group_id":1})
                        .build(),
                )
                .run()?;
            }
            _ => {}
        };
        Ok(())
    }
    pub fn digest(&mut self) -> Result<[i64; 3]> {
        match &mut self.backend {
            Backend::Sqlite(db) => Ok(db.query_row(
                "SELECT COUNT(*),COALESCE(SUM(id),0),COALESCE(SUM(amount_cents),0) FROM records",
                [],
                |row| Ok([row.get(0)?, row.get(1)?, row.get(2)?]),
            )?),
            Backend::Postgres(db) => {
                let row=db.query_one("SELECT COUNT(*),COALESCE(SUM(id),0)::bigint,COALESCE(SUM(amount_cents),0)::bigint FROM records",&[])?;
                Ok([row.get(0), row.get(1), row.get(2)])
            }
            Backend::MySql(db) => {
                let row:Option<(i64,i64,i64)>=db.query_first("SELECT COUNT(*),COALESCE(SUM(id),0),COALESCE(SUM(amount_cents),0) FROM records")?;
                let (a, b, c) = row.context("Empty digest")?;
                Ok([a, b, c])
            }
            Backend::Mongo { col, .. } => {
                let mut cursor=col.aggregate([doc!{"$group":{"_id":Bson::Null,"n":{"$sum":1},"ids":{"$sum":"$_id"},"amount":{"$sum":"$amount_cents"}}}]).run()?;
                if let Some(row) = cursor.next() {
                    let r = row?;
                    Ok([
                        number(r.get("n").context("n missing")?)?,
                        number(r.get("ids").context("ids missing")?)?,
                        number(r.get("amount").context("amount missing")?)?,
                    ])
                } else {
                    Ok([0, 0, 0])
                }
            }
            Backend::Redis(db) => {
                let script="local r=redis.call('SCAN',ARGV[1],'MATCH',ARGV[2]..'*','COUNT',1000); local n=0; local ids=0; local a=0; for _,k in ipairs(r[2]) do n=n+1; ids=ids+tonumber(string.sub(k,string.len(ARGV[2])+1)); a=a+tonumber(redis.call('HGET',k,'amount_cents')); end; return {r[1],n,ids,a}";
                let mut cursor = "0".to_owned();
                let mut sums = [0; 3];
                loop {
                    let (next, n, ids, amount): (String, i64, i64, i64) = redis::cmd("EVAL")
                        .arg(script)
                        .arg(0)
                        .arg(&cursor)
                        .arg(&self.prefix)
                        .query(db)?;
                    sums[0] += n;
                    sums[1] += ids;
                    sums[2] += amount;
                    cursor = next;
                    if cursor == "0" {
                        return Ok(sums);
                    }
                }
            }
            Backend::ClickHouse(_) => {
                let text = self.query(
                    "SELECT count(),sum(id),sum(amount_cents) FROM records FORMAT TabSeparated",
                    "",
                )?;
                let values = text
                    .trim()
                    .split('\t')
                    .map(str::parse)
                    .collect::<Result<Vec<i64>, _>>()?;
                Ok(values
                    .try_into()
                    .map_err(|_| anyhow::anyhow!("Invalid digest"))?)
            }
        }
    }
    pub fn count(&mut self) -> Result<i64> {
        match &mut self.backend {
            Backend::Sqlite(db) => {
                Ok(db.query_row("SELECT COUNT(*) FROM records", [], |r| r.get(0))?)
            }
            Backend::Postgres(db) => Ok(db.query_one("SELECT COUNT(*) FROM records", &[])?.get(0)),
            Backend::MySql(db) => Ok(db
                .query_first("SELECT COUNT(*) FROM records")?
                .context("Missing count")?),
            Backend::Mongo { col, .. } => Ok(col.count_documents(doc! {}).run()? as i64),
            Backend::Redis(db) => Ok(redis::cmd("DBSIZE").query(db)?),
            Backend::ClickHouse(_) => Ok(self
                .query("SELECT count() FROM records FORMAT TabSeparated", "")?
                .trim()
                .parse()?),
        }
    }
    pub fn metadata(&mut self) -> Result<Value> {
        let mut settings = std::collections::BTreeMap::<String, String>::new();
        settings.insert(
            "build".into(),
            if cfg!(debug_assertions) {
                "Debug; not suitable for performance comparison"
            } else {
                "Release"
            }
            .into(),
        );
        settings.insert("generator".into(), crate::portable::GENERATOR.into());
        settings.insert(
            "connections".into(),
            "one connection and OS thread per logical client".into(),
        );
        settings.insert("gc_metrics".into(), "no managed GC; null".into());
        let (version, driver) = match &mut self.backend {
            Backend::Sqlite(db) => {
                for key in ["journal_mode", "synchronous", "cache_size"] {
                    let value = db.query_row(&format!("PRAGMA {}", key), [], |r| {
                        let v = r.get_ref(0)?;
                        Ok(match v {
                            rusqlite::types::ValueRef::Text(bytes) => {
                                String::from_utf8_lossy(bytes).into_owned()
                            }
                            rusqlite::types::ValueRef::Integer(n) => n.to_string(),
                            _ => format!("{:?}", v),
                        })
                    })?;
                    settings.insert(key.into(), value);
                }
                settings.insert(
                    "batch_transport".into(),
                    "prepared INSERT in transaction".into(),
                );
                (
                    db.query_row("SELECT sqlite_version()", [], |r| r.get::<_, String>(0))?,
                    dependency("rusqlite"),
                )
            }
            Backend::Postgres(db) => {
                for key in [
                    "synchronous_commit",
                    "fsync",
                    "shared_buffers",
                    "max_connections",
                ] {
                    settings.insert(
                        key.into(),
                        db.query_one(&format!("SHOW {}", key), &[])?
                            .get::<_, String>(0),
                    );
                }
                settings.insert("batch_transport".into(), "binary COPY".into());
                (
                    db.query_one("SELECT VERSION()", &[])?.get::<_, String>(0),
                    dependency("postgres"),
                )
            }
            Backend::MySql(db) => {
                for key in [
                    "innodb_flush_log_at_trx_commit",
                    "innodb_buffer_pool_size",
                    "max_connections",
                ] {
                    let value: Option<String> =
                        db.query_first(format!("SELECT CAST(@@GLOBAL.{} AS CHAR)", key))?;
                    settings.insert(key.into(), value.unwrap_or_default());
                }
                settings.insert(
                    "batch_transport".into(),
                    "multi-row parameterized INSERT".into(),
                );
                (
                    db.query_first::<String, _>("SELECT VERSION()")?
                        .context("No version")?,
                    dependency("mysql"),
                )
            }
            Backend::Mongo { db, .. } => {
                let version = db
                    .run_command(doc! {"buildInfo":1})
                    .run()?
                    .get_str("version")?
                    .to_owned();
                settings.insert("write_concern".into(), "w=1,j=true".into());
                settings.insert("retry_writes".into(), "false".into());
                settings.insert("topology".into(), "standalone".into());
                (version, dependency("mongodb"))
            }
            Backend::Redis(db) => {
                let info: String = redis::cmd("INFO").arg("server").query(db)?;
                let mut version = "unknown";
                for line in info.lines() {
                    if let Some(value) = line
                        .strip_prefix("redis_version:")
                        .or_else(|| line.strip_prefix("valkey_version:"))
                    {
                        version = value.trim();
                    }
                }
                settings.insert(
                    "durability_profile".into(),
                    "memory cache: AOF and RDB disabled".into(),
                );
                settings.insert(
                    "batch_transport".into(),
                    "pipelined HSET; non-atomic".into(),
                );
                (version.to_owned(), dependency("redis"))
            }
            Backend::ClickHouse(_) => {
                let version = self
                    .query("SELECT version() FORMAT TabSeparated", "")?
                    .trim()
                    .to_owned();
                settings.insert(
                    "transport".into(),
                    "HTTP / JSONEachRow / uncompressed".into(),
                );
                settings.insert("insert".into(), "synchronous HTTP; async_insert=0".into());
                settings.insert(
                    "table".into(),
                    "MergeTree ORDER BY id; no unique constraint".into(),
                );
                (version, dependency("reqwest"))
            }
        };
        Ok(
            json!({"engine":"rust","runtime":env!("BENCHFORGE_RUSTC"),"driver":driver,"serverVersion":version,"settings":settings}),
        )
    }
    pub fn execute(
        &mut self,
        operation: &str,
        key: i64,
        row: Option<&Row>,
        batch: &[Row],
    ) -> Result<i64> {
        if operation == "BatchInsert" {
            self.batch(batch)?;
            return Ok(batch.len() as i64);
        }
        match &mut self.backend {
            Backend::Mongo { col, .. } => {
                let filter = doc! {"_id":key};
                match operation {
                    "PointRead" => {
                        let found = col
                            .find_one(filter)
                            .max_time(Duration::from_secs(10))
                            .run()?
                            .context("Missing MongoDB row")?;
                        if number(found.get("_id").context("Missing id")?)? != key {
                            bail!("Invalid id")
                        };
                        found.get_str("payload")?;
                        Ok(1)
                    }
                    "RangeRead" => {
                        let mut count = 0;
                        for value in col
                            .find(doc! {"_id":{"$gte":key}})
                            .sort(doc! {"_id":1})
                            .limit(100)
                            .max_time(Duration::from_secs(10))
                            .run()?
                        {
                            value?;
                            count += 1;
                        }
                        if count == 0 {
                            bail!("Empty range")
                        };
                        Ok(count)
                    }
                    "Aggregate" => {
                        let mut count = 0;
                        for value in col.aggregate([doc!{"$match":{"group_id":(key-1)%100}},doc!{"$group":{"_id":"$group_id","n":{"$sum":1},"amount":{"$sum":"$amount_cents"}}}]).max_time(Duration::from_secs(10)).run()?{value?;count+=1;}
                        if count == 0 {
                            bail!("Empty aggregate")
                        };
                        Ok(count)
                    }
                    "Update" => {
                        if col
                            .update_one(filter, doc! {"$inc":{"version":1i64}})
                            .run()?
                            .matched_count
                            != 1
                        {
                            bail!("Missing update")
                        };
                        Ok(1)
                    }
                    "Insert" => {
                        col.insert_one(document(row.context("Missing row")?))
                            .run()?;
                        Ok(1)
                    }
                    "Delete" => {
                        if col.delete_one(filter).run()?.deleted_count != 1 {
                            bail!("Missing delete")
                        };
                        Ok(1)
                    }
                    _ => bail!("Unsupported operation"),
                }
            }
            Backend::Redis(db) => {
                let name = format!("{}{}", self.prefix, key);
                match operation {
                    "PointRead" => {
                        let values: HashMap<String, String> = db.hgetall(name)?;
                        if values.len() != 4 {
                            bail!("Incomplete Redis hash")
                        };
                        Ok(1)
                    }
                    "Update" => {
                        let _: i64 = db.hincr(name, "version", 1)?;
                        Ok(1)
                    }
                    "Insert" => {
                        let r = row.context("Missing row")?;
                        let _: i64 = redis::cmd("HSET")
                            .arg(format!("{}{}", self.prefix, r.id))
                            .arg("group_id")
                            .arg(r.group_id)
                            .arg("amount_cents")
                            .arg(r.amount_cents)
                            .arg("version")
                            .arg(r.version)
                            .arg("payload")
                            .arg(&r.payload)
                            .query(db)?;
                        Ok(1)
                    }
                    "Delete" => {
                        let n: i64 = db.del(name)?;
                        if n != 1 {
                            bail!("Missing delete")
                        };
                        Ok(1)
                    }
                    _ => bail!("Unsupported operation"),
                }
            }
            Backend::ClickHouse(_) => {
                let query=match operation{"RangeRead"=>format!("SELECT id,group_id,amount_cents,version,payload FROM records WHERE id>={} ORDER BY id LIMIT 100",key),"Aggregate"=>format!("SELECT group_id,count(),sum(amount_cents) FROM records WHERE group_id={} GROUP BY group_id",(key-1)%100),_=>format!("SELECT count(),sum(amount_cents),avg(amount_cents) FROM records WHERE id>={} AND id<{}",key,key+100.max(self.run.rows/100))};
                let text = self.query(
                    &(query + " SETTINGS max_execution_time=10 FORMAT JSONEachRow"),
                    "",
                )?;
                let mut count = 0;
                for line in text.lines().filter(|s| !s.is_empty()) {
                    serde_json::from_str::<Value>(line)?;
                    count += 1;
                }
                if count == 0 {
                    bail!("Empty ClickHouse response")
                };
                Ok(count)
            }
            Backend::Sqlite(db) => {
                let query = sql(operation, false);
                let mut statement = db.prepare_cached(&query)?;
                let parameter = if operation == "Aggregate" {
                    (key - 1) % 100
                } else {
                    key
                };
                if matches!(operation, "PointRead" | "RangeRead" | "Aggregate") {
                    let mut rows = statement.query(params![parameter])?;
                    let mut count = 0;
                    while let Some(r) = rows.next()? {
                        if operation == "Aggregate" {
                            r.get::<_, i64>(1)?;
                            r.get::<_, i64>(2)?;
                        } else {
                            let id = r.get::<_, i64>(0)?;
                            r.get::<_, String>(4)?;
                            if operation == "PointRead" && id != key {
                                bail!("Invalid id")
                            }
                        };
                        count += 1;
                    }
                    if count == 0 || operation == "PointRead" && count != 1 {
                        bail!("Invalid SQL response")
                    };
                    Ok(count)
                } else {
                    let n = if operation == "Insert" {
                        let r = row.context("Missing row")?;
                        statement.execute(params![
                            r.id,
                            r.group_id,
                            r.amount_cents,
                            r.version,
                            r.payload
                        ])?
                    } else {
                        statement.execute(params![parameter])?
                    };
                    if n != 1 {
                        bail!("Write did not affect one record")
                    };
                    Ok(n as i64)
                }
            }
            Backend::Postgres(db) => {
                let statement = if let Some(value) = self.pg_statements.get(operation) {
                    value.clone()
                } else {
                    let value = db.prepare(&sql(operation, true))?;
                    self.pg_statements.insert(operation.into(), value.clone());
                    value
                };
                let group = ((key - 1) % 100) as i32;
                if matches!(operation, "PointRead" | "RangeRead" | "Aggregate") {
                    let rows = if operation == "Aggregate" {
                        db.query(&statement, &[&group])?
                    } else {
                        db.query(&statement, &[&key])?
                    };
                    if rows.is_empty() || operation == "PointRead" && rows.len() != 1 {
                        bail!("Invalid SQL response")
                    };
                    for r in &rows {
                        if operation == "Aggregate" {
                            r.get::<_, i64>(1);
                            r.get::<_, i64>(2);
                        } else {
                            let id = r.get::<_, i64>(0);
                            r.get::<_, String>(4);
                            if operation == "PointRead" && id != key {
                                bail!("Invalid id")
                            }
                        }
                    }
                    Ok(rows.len() as i64)
                } else {
                    let n = if operation == "Insert" {
                        let r = row.context("Missing row")?;
                        db.execute(
                            &statement,
                            &[&r.id, &r.group_id, &r.amount_cents, &r.version, &r.payload],
                        )?
                    } else {
                        db.execute(&statement, &[&key])?
                    };
                    if n != 1 {
                        bail!("Write did not affect one record")
                    };
                    Ok(n as i64)
                }
            }
            Backend::MySql(db) => {
                let statement = if let Some(value) = self.my_statements.get(operation) {
                    value.clone()
                } else {
                    let value = db.prep(sql(operation, false))?;
                    self.my_statements.insert(operation.into(), value.clone());
                    value
                };
                let parameters = if operation == "Insert" {
                    let r = row.context("Missing row")?;
                    vec![
                        MyValue::Int(r.id),
                        MyValue::Int(r.group_id as i64),
                        MyValue::Int(r.amount_cents),
                        MyValue::Int(r.version),
                        MyValue::Bytes(r.payload.as_bytes().to_vec()),
                    ]
                } else {
                    vec![MyValue::Int(if operation == "Aggregate" {
                        (key - 1) % 100
                    } else {
                        key
                    })]
                };
                if matches!(operation, "PointRead" | "RangeRead" | "Aggregate") {
                    let rows: Vec<mysql::Row> =
                        db.exec(&statement, Params::Positional(parameters))?;
                    if rows.is_empty() || operation == "PointRead" && rows.len() != 1 {
                        bail!("Invalid SQL response")
                    };
                    for r in &rows {
                        if operation == "Aggregate" {
                            r.get::<i64, _>(1).context("Bad count")?;
                            r.get::<i64, _>(2).context("Bad sum")?;
                        } else {
                            let id = r.get::<i64, _>(0).context("Bad id")?;
                            r.get::<String, _>(4).context("Bad payload")?;
                            if operation == "PointRead" && id != key {
                                bail!("Invalid id")
                            }
                        }
                    }
                    Ok(rows.len() as i64)
                } else {
                    db.exec_drop(&statement, Params::Positional(parameters))?;
                    let n = db.affected_rows();
                    if n != 1 {
                        bail!("Write did not affect one record")
                    };
                    Ok(n as i64)
                }
            }
        }
    }
}
fn sql(operation: &str, pg: bool) -> String {
    let p = if pg { "$1" } else { "?" };
    match operation{"PointRead"=>format!("SELECT id,group_id,amount_cents,version,payload FROM records WHERE id={}",p),"RangeRead"=>format!("SELECT id,group_id,amount_cents,version,payload FROM records WHERE id>={} ORDER BY id LIMIT 100",p),"Aggregate"=>format!("SELECT group_id,COUNT(*),{} FROM records WHERE group_id={} GROUP BY group_id",if pg{"SUM(amount_cents)::bigint"}else{"SUM(amount_cents)"},p),"Update"=>format!("UPDATE records SET version=version+1 WHERE id={}",p),"Delete"=>format!("DELETE FROM records WHERE id={}",p),_=>if pg{"INSERT INTO records VALUES($1,$2,$3,$4,$5)".into()}else{"INSERT INTO records VALUES(?,?,?,?,?)".into()}}
}

pub enum Database {
    Original(OriginalDatabase),
    Extended(crate::extended::Extended),
    Custom(Box<crate::custom::Session>),
}
impl Database {
    pub fn custom_query(
        &mut self,
        text: &str,
        values: &[Option<String>],
        mode: &str,
        cache: bool,
        timeout: Duration,
    ) -> Result<crate::custom_query::Answer> {
        match self {
            Self::Original(d) => match &mut d.backend {
                Backend::Sqlite(c) => {
                    c.busy_timeout(timeout)?;
                    crate::custom_query::sqlite(c, text, values, mode, cache)
                }
                Backend::Postgres(c) => crate::custom_query::postgres(
                    c,
                    &mut d.pg_statements,
                    text,
                    values,
                    mode,
                    cache,
                ),
                Backend::MySql(c) => crate::custom_query::mysql(c, text, values, mode, cache),
                _ => bail!("Custom SQL requires a SQL target"),
            },
            Self::Extended(d) => d.custom_query(text, values, mode, cache, timeout),
            Self::Custom(_) => bail!("Unexpected direct access to a custom session"),
        }
    }
    pub fn open(run: Arc<Run>) -> Result<Self> {
        if crate::extended::supports(&run.target) {
            Ok(Self::Extended(crate::extended::Extended::open(run)?))
        } else {
            Ok(Self::Original(OriginalDatabase::open(run)?))
        }
    }
    pub fn reset(&mut self) -> Result<()> {
        match self {
            Self::Original(d) => d.reset(),
            Self::Extended(d) => d.reset(),
            Self::Custom(_) => bail!("Unexpected direct access to custom session"),
        }
    }
    pub fn batch(&mut self, rows: &[Row]) -> Result<()> {
        match self {
            Self::Original(d) => d.batch(rows),
            Self::Extended(d) => d.batch(rows),
            Self::Custom(_) => bail!("Unexpected direct access to custom session"),
        }
    }
    pub fn prepare(&mut self) -> Result<()> {
        match self {
            Self::Original(d) => d.prepare(),
            Self::Extended(d) => d.prepare(),
            Self::Custom(_) => bail!("Unexpected direct access to custom session"),
        }
    }
    pub fn digest(&mut self) -> Result<[i64; 3]> {
        match self {
            Self::Original(d) => d.digest(),
            Self::Extended(d) => d.digest(),
            Self::Custom(_) => bail!("Unexpected direct access to custom session"),
        }
    }
    pub fn count(&mut self) -> Result<i64> {
        match self {
            Self::Original(d) => d.count(),
            Self::Extended(d) => d.count(),
            Self::Custom(_) => bail!("Unexpected direct access to custom session"),
        }
    }
    pub fn metadata(&mut self) -> Result<Value> {
        match self {
            Self::Original(d) => d.metadata(),
            Self::Extended(d) => d.metadata(),
            Self::Custom(_) => bail!("Unexpected direct access to custom session"),
        }
    }
    pub fn execute(&mut self, op: &str, key: i64, row: Option<&Row>, batch: &[Row]) -> Result<i64> {
        match self {
            Self::Original(d) => d.execute(op, key, row, batch),
            Self::Extended(d) => d.execute(op, key, row, batch),
            Self::Custom(_) => bail!("Unexpected direct access to custom session"),
        }
    }
}
