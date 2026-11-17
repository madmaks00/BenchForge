use crate::{
    duckdb::DuckConnection,
    portable::{row as make_row, Row},
    Run,
};
use anyhow::{bail, Context, Result};
use serde_json::{json, Value};
use std::{sync::Arc, time::Duration};
use tokio_util::compat::{Compat, TokioAsyncWriteCompatExt};
pub(crate) type Tds = tiberius::Client<Compat<tokio::net::TcpStream>>;
pub fn supports(t: &str) -> bool {
    matches!(
        t,
        "sqlServer" | "oracle" | "duckDb" | "cockroachDb" | "elasticsearch" | "openSearch"
    )
}
enum Sql {
    Duck(DuckConnection),
    Oracle(oracle::Connection),
    Cockroach(postgres::Client),
    Server {
        runtime: tokio::runtime::Runtime,
        client: Tds,
    },
}
pub struct Extended {
    run: Arc<Run>,
    sql: Option<Sql>,
    custom_prepared: std::collections::HashMap<String, postgres::Statement>,
    http: Option<reqwest::blocking::Client>,
}
fn number(v: &Value) -> Result<i64> {
    if let Some(n) = v.as_i64() {
        Ok(n)
    } else {
        Ok(v.as_str().context("Expected integer")?.parse()?)
    }
}
impl Extended {
    pub fn open(run: Arc<Run>) -> Result<Self> {
        let ep = &run.endpoint;
        if run.target != "duckDb"
            && (ep.database.is_empty()
                || !ep
                    .database
                    .chars()
                    .all(|c| c.is_ascii_alphanumeric() || c == '_'))
        {
            bail!("Invalid owned database name")
        }
        let backend = match run.target.as_str() {
            "duckDb" => Some(Sql::Duck(DuckConnection::open(
                ep.file_path.as_deref().context("Missing DuckDB file")?,
            )?)),
            "oracle" => {
                let address = format!("//{}:{}/{}", ep.host, ep.port, ep.database);
                let admin = oracle::Connection::connect("SYSTEM", &ep.password, &address)?;
                let count: i64 = admin
                    .query_row_as("SELECT COUNT(*) FROM all_users WHERE username='DBLAB'", &[])?;
                if count == 0 {
                    admin.execute(
                        &format!(
                            "CREATE USER DBLAB IDENTIFIED BY \"{}\"",
                            ep.password.replace('"', "\"\"")
                        ),
                        &[],
                    )?;
                    admin.execute(
                        "GRANT CREATE SESSION,CREATE TABLE,UNLIMITED TABLESPACE TO DBLAB",
                        &[],
                    )?;
                }
                let mut connection =
                    oracle::Connection::connect(&ep.username, &ep.password, &address)?;
                connection.set_autocommit(true);
                if run.config.custom_workload.is_some() {
                    connection.execute("ALTER SESSION SET NLS_NUMERIC_CHARACTERS = '.,'", &[])?;
                }
                Some(Sql::Oracle(connection))
            }
            "cockroachDb" => {
                let mut cfg = postgres::Config::new();
                cfg.host(&ep.host)
                    .port(ep.port)
                    .user(&ep.username)
                    .password(&ep.password)
                    .dbname("defaultdb")
                    .connect_timeout(Duration::from_secs(15));
                cfg.connect(postgres::NoTls)?.batch_execute(&format!(
                    "CREATE DATABASE IF NOT EXISTS \"{}\"",
                    ep.database
                ))?;
                cfg.dbname(&ep.database);
                Some(Sql::Cockroach(cfg.connect(postgres::NoTls)?))
            }
            "sqlServer" => {
                let runtime = tokio::runtime::Builder::new_current_thread()
                    .enable_all()
                    .build()?;
                let mut config = tiberius::Config::new();
                config.host(&ep.host);
                config.port(ep.port);
                config.authentication(tiberius::AuthMethod::sql_server(&ep.username, &ep.password));
                config.encryption(tiberius::EncryptionLevel::NotSupported);
                config.database("master");
                let mut client = runtime.block_on(async {
                    let tcp = tokio::net::TcpStream::connect(config.get_addr()).await?;
                    tcp.set_nodelay(true)?;
                    tiberius::Client::connect(config, tcp.compat_write()).await
                })?;
                runtime.block_on(async {
                    client
                        .simple_query(format!(
                            "IF DB_ID('{}') IS NULL CREATE DATABASE [{}];",
                            ep.database, ep.database
                        ))
                        .await?
                        .into_results()
                        .await
                })?;
                runtime.block_on(async{client.simple_query(format!("USE [{}]; ALTER DATABASE [dblab] SET RECOVERY SIMPLE; ALTER DATABASE [dblab] SET DELAYED_DURABILITY=DISABLED; SET QUOTED_IDENTIFIER ON; SET NOCOUNT OFF; SET XACT_ABORT ON",ep.database)).await?.into_results().await})?;
                Some(Sql::Server { runtime, client })
            }
            _ => None,
        };
        let http = if backend.is_none() {
            Some(
                reqwest::blocking::Client::builder()
                    .connect_timeout(Duration::from_secs(15))
                    .timeout(Duration::from_secs(600))
                    .build()?,
            )
        } else {
            None
        };
        let mut d = Self {
            run,
            sql: backend,
            custom_prepared: std::collections::HashMap::new(),
            http,
        };
        if d.http.is_some() {
            d.request("GET", "/", None, false)?;
            d.request(
                "GET",
                "/_cluster/health?wait_for_status=yellow&timeout=5s",
                None,
                false,
            )?;
        } else if d.run.target == "duckDb" {
            d.query(
                &format!("SET memory_limit='{}MB'", d.run.config.memory_mb),
                &[],
            )?;
            d.query(
                &format!(
                    "SET threads={}",
                    d.run.config.cpu_limit.ceil().max(1.) as usize
                ),
                &[],
            )?;
        }
        Ok(d)
    }
    pub fn custom_query(
        &mut self,
        text: &str,
        values: &[Option<String>],
        mode: &str,
        cache: bool,
        timeout: Duration,
    ) -> Result<crate::custom_query::Answer> {
        use crate::custom_query::{self as query, Answer};
        match self.sql.as_mut().context("Missing SQL connection")? {
            Sql::Duck(connection) => {
                let args: Vec<Value> = values.iter().map(|value| json!(value)).collect();
                let (rows, changed) = connection.query(text, &args)?;
                if mode == "scalar" {
                    let scalar = rows
                        .first()
                        .and_then(|row| row.first())
                        .and_then(Value::as_str)
                        .map(str::to_owned);
                    Ok(query::answer(scalar.clone(), i64::from(scalar.is_some())))
                } else if mode == "nonQuery" {
                    Ok(query::answer(None, changed as i64))
                } else {
                    Ok(query::answer(None, rows.len() as i64))
                }
            }
            Sql::Cockroach(connection) => query::postgres(
                connection,
                &mut self.custom_prepared,
                text,
                values,
                mode,
                cache,
            ),
            Sql::Oracle(connection) => {
                connection.set_call_timeout(Some(timeout))?;
                let ready = |connection: &mut oracle::Connection, commit: bool| -> Result<Answer> {
                    if commit {
                        connection.commit()?;
                    } else {
                        connection.rollback()?;
                    }
                    connection.set_autocommit(true);
                    Ok(query::answer(None, 0))
                };
                match text {
                    "BEGIN" => {
                        connection.set_autocommit(false);
                        Ok(query::answer(None, 0))
                    }
                    "COMMIT" => ready(connection, true),
                    "ROLLBACK" => ready(connection, false),
                    _ => query::oracle(connection, text, values, mode),
                }
            }
            Sql::Server { runtime, client } => {
                runtime.block_on(query::tds(client, text, values, mode, timeout))
            }
        }
    }
    fn query(&mut self, text: &str, args: &[Value]) -> Result<(Vec<Vec<Value>>, u64)> {
        match self.sql.as_mut().context("Missing SQL connection")? {
            Sql::Duck(c) => c.query(text, args),
            Sql::Cockroach(c) => {
                use postgres::types::{ToSql, Type};
                let statement = c.prepare(text)?;
                let values: Vec<Box<dyn ToSql + Sync>> = args
                    .iter()
                    .zip(statement.params())
                    .map(|(value, kind)| -> Result<Box<dyn ToSql + Sync>> {
                        if let Some(n) = value.as_i64() {
                            match *kind {
                                Type::INT4 => Ok(Box::new(i32::try_from(n)?)),
                                Type::INT8 => Ok(Box::new(n)),
                                _ => bail!("Unexpected CockroachDB integer parameter: {kind}"),
                            }
                        } else {
                            Ok(Box::new(
                                value.as_str().context("Expected SQL text")?.to_owned(),
                            ))
                        }
                    })
                    .collect::<Result<_>>()?;
                let parameters: Vec<&(dyn ToSql + Sync)> =
                    values.iter().map(|v| v.as_ref()).collect();
                if statement.columns().is_empty() {
                    return Ok((vec![], c.execute(&statement, &parameters)?));
                }
                let mut result = Vec::new();
                for r in c.query(&statement, &parameters)? {
                    let mut row = Vec::new();
                    for (i, col) in r.columns().iter().enumerate() {
                        let v = match *col.type_() {
                            Type::INT8 => json!(r.try_get::<_, i64>(i)?),
                            Type::INT4 => json!(r.try_get::<_, i32>(i)?),
                            _ => json!(r.try_get::<_, String>(i)?),
                        };
                        row.push(v)
                    }
                    result.push(row)
                }
                Ok((result, 0))
            }
            Sql::Oracle(c) => {
                enum Param {
                    Number(i64),
                    Text(String),
                }
                let values: Vec<Param> = args
                    .iter()
                    .map(|v| {
                        if let Some(n) = v.as_i64() {
                            Param::Number(n)
                        } else {
                            Param::Text(v.as_str().unwrap().to_owned())
                        }
                    })
                    .collect();
                let mut lob = None;
                if args.len() == 5 {
                    use std::io::Write;
                    let mut clob = oracle::sql_type::Clob::new(c)?;
                    clob.write_all(args[4].as_str().unwrap().as_bytes())?;
                    lob = Some(clob);
                }
                let parameters: Vec<&dyn oracle::sql_type::ToSql> = values
                    .iter()
                    .enumerate()
                    .map(|(i, v)| {
                        if i == 4 && lob.is_some() {
                            lob.as_ref().unwrap() as &dyn oracle::sql_type::ToSql
                        } else {
                            match v {
                                Param::Number(n) => n as &dyn oracle::sql_type::ToSql,
                                Param::Text(s) => s as &dyn oracle::sql_type::ToSql,
                            }
                        }
                    })
                    .collect();
                if !text.trim_start().to_ascii_uppercase().starts_with("SELECT") {
                    let s = c.execute(text, &parameters)?;
                    return Ok((vec![], s.row_count()?));
                }
                let rows = c.query(text, &parameters)?;
                let numeric: Vec<bool> = rows
                    .column_info()
                    .iter()
                    .map(|i| matches!(i.oracle_type(), oracle::sql_type::OracleType::Number(..)))
                    .collect();
                let mut result = Vec::new();
                for r in rows {
                    let r = r?;
                    let mut row = Vec::new();
                    for (i, is_number) in numeric.iter().enumerate() {
                        row.push(if *is_number {
                            json!(r.get::<_, i64>(i)?)
                        } else {
                            json!(r.get::<_, String>(i)?)
                        });
                    }
                    result.push(row)
                }
                Ok((result, 0))
            }
            Sql::Server { runtime, client } => runtime.block_on(async {
                if matches!(text, "BEGIN TRANSACTION" | "COMMIT" | "ROLLBACK") {
                    client.simple_query(text).await?.into_results().await?;
                    return Ok((vec![], 0));
                }
                let mut q = tiberius::Query::new(text);
                for v in args {
                    if let Some(n) = v.as_i64() {
                        q.bind(n)
                    } else {
                        q.bind(v.as_str().unwrap().to_owned())
                    }
                }
                if !text.trim_start().to_ascii_uppercase().starts_with("SELECT") {
                    return Ok((vec![], q.execute(client).await?.total()));
                }
                let rows = q.query(client).await?.into_first_result().await?;
                let mut result = Vec::new();
                for r in rows {
                    let mut row = Vec::new();
                    for i in 0..r.len() {
                        let v = if let Ok(v) = r.try_get::<&str, _>(i) {
                            json!(v)
                        } else if let Ok(v) = r.try_get::<i64, _>(i) {
                            json!(v)
                        } else if let Ok(v) = r.try_get::<i32, _>(i) {
                            json!(v)
                        } else if let Ok(v) = r.try_get::<f64, _>(i) {
                            json!(v)
                        } else {
                            bail!("Unsupported TDS result column")
                        };
                        row.push(v)
                    }
                    result.push(row)
                }
                Ok((result, 0))
            }),
        }
    }
    fn request(
        &self,
        method: &str,
        path: &str,
        body: Option<Value>,
        missing: bool,
    ) -> Result<Value> {
        let e = &self.run.endpoint;
        let mut q = self.http.as_ref().context("Missing HTTP client")?.request(
            method.parse::<reqwest::Method>()?,
            format!("http://{}:{}{}", e.host, e.port, path),
        );
        if let Some(body) = body {
            if let Value::String(text) = body {
                q = q.header("Content-Type", "application/x-ndjson").body(text)
            } else {
                q = q
                    .header("Content-Type", "application/json")
                    .body(serde_json::to_string(&body)?)
            }
        }
        let response = q.send()?;
        let status = response.status();
        if missing && status.as_u16() == 404 {
            return Ok(json!({}));
        };
        let text = response.text()?;
        if !status.is_success() {
            bail!(
                "Search HTTP {}: {}",
                status,
                text.chars().take(350).collect::<String>()
            )
        }
        let v: Value = serde_json::from_str(&text)?;
        if v["timed_out"] == true || v["_shards"]["failed"].as_u64().unwrap_or(0) > 0 {
            bail!("Incomplete search response")
        };
        Ok(v)
    }
    fn document(r: &Row) -> Value {
        let terms = r
            .payload
            .as_bytes()
            .chunks(16)
            .map(|v| std::str::from_utf8(v).unwrap())
            .collect::<Vec<_>>()
            .join(" ");
        json!({"id":r.id,"group_id":r.group_id,"amount_cents":r.amount_cents,"version":r.version,"payload":r.payload,"search_text":format!("benchforge group{} {}",r.group_id,terms)})
    }
    fn p(&self, n: usize) -> String {
        match self.run.target.as_str() {
            "oracle" => format!(":{n}"),
            "sqlServer" => format!("@P{n}"),
            "cockroachDb" => format!("${n}"),
            _ => "?".into(),
        }
    }
    fn insert_sql(&self) -> String {
        format!(
            "INSERT INTO records(id,group_id,amount_cents,version,payload) VALUES({})",
            (1..=5).map(|n| self.p(n)).collect::<Vec<_>>().join(",")
        )
    }
    fn values(r: &Row) -> Vec<Value> {
        vec![
            json!(r.id),
            json!(r.group_id),
            json!(r.amount_cents),
            json!(r.version),
            json!(r.payload),
        ]
    }
    pub fn reset(&mut self) -> Result<()> {
        if self.http.is_some() {
            self.request("DELETE", "/records", None, true)?;
            self.request("PUT","/records",Some(json!({"settings":{"number_of_shards":1,"number_of_replicas":0,"refresh_interval":"-1","translog.durability":"request"},"mappings":{"dynamic":"strict","properties":{"id":{"type":"long"},"group_id":{"type":"integer"},"amount_cents":{"type":"long"},"version":{"type":"long"},"payload":{"type":"keyword","index":false,"doc_values":false},"search_text":{"type":"text","analyzer":"standard"}}}})),false)?;
            return Ok(());
        }
        let oracle = self.run.target == "oracle";
        self.query(if oracle{"BEGIN EXECUTE IMMEDIATE 'DROP TABLE records PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;"}else{"DROP TABLE IF EXISTS records"},&[])?;
        let ddl = if oracle {
            "id NUMBER(19) PRIMARY KEY,group_id NUMBER(10) NOT NULL,amount_cents NUMBER(19) NOT NULL,version NUMBER(19) NOT NULL,payload CLOB NOT NULL"
        } else if self.run.target == "sqlServer" {
            "id BIGINT PRIMARY KEY,group_id INTEGER NOT NULL,amount_cents BIGINT NOT NULL,version BIGINT NOT NULL,payload NVARCHAR(MAX) NOT NULL"
        } else {
            "id BIGINT PRIMARY KEY,group_id INTEGER NOT NULL,amount_cents BIGINT NOT NULL,version BIGINT NOT NULL,payload TEXT NOT NULL"
        };
        self.query(&format!("CREATE TABLE records({ddl})"), &[])?;
        Ok(())
    }
    pub fn batch(&mut self, rows: &[Row]) -> Result<()> {
        if self.http.is_some() {
            let mut body = String::new();
            for r in rows {
                body.push_str(&serde_json::to_string(
                    &json!({"create":{"_index":"records","_id":r.id.to_string()}}),
                )?);
                body.push('\n');
                body.push_str(&serde_json::to_string(&Self::document(r))?);
                body.push('\n');
            }
            let v = self.request(
                "POST",
                "/_bulk?refresh=false",
                Some(Value::String(body)),
                false,
            )?;
            let items = v["items"].as_array().context("Missing bulk items")?;
            if v["errors"] == true
                || items.len() != rows.len()
                || items
                    .iter()
                    .any(|v| v["create"]["status"] != 201 || !v["create"]["error"].is_null())
            {
                bail!("Bulk insert contains failed items")
            };
            return Ok(());
        }
        let oracle = self.run.target == "oracle";
        if oracle {
            if let Some(Sql::Oracle(c)) = &mut self.sql {
                c.set_autocommit(false)
            }
        } else {
            self.query(
                if self.run.target == "sqlServer" {
                    "BEGIN TRANSACTION"
                } else {
                    "BEGIN"
                },
                &[],
            )?;
        }
        let text = self.insert_sql();
        let result = (|| -> Result<()> {
            for r in rows {
                let (_, n) = self.query(&text, &Self::values(r))?;
                if n != 1 {
                    bail!("Insert did not affect one record")
                }
            }
            Ok(())
        })();
        if oracle {
            if let Some(Sql::Oracle(c)) = &mut self.sql {
                if result.is_ok() {
                    c.commit()?
                } else {
                    c.rollback()?
                };
                c.set_autocommit(true)
            }
        } else {
            self.query(if result.is_ok() { "COMMIT" } else { "ROLLBACK" }, &[])?;
        }
        result
    }
    pub fn prepare(&mut self) -> Result<()> {
        if self.http.is_some() {
            self.request(
                "PUT",
                "/records/_settings",
                Some(json!({"index":{"refresh_interval":"1s"}})),
                false,
            )?;
            self.request("POST", "/records/_refresh", None, false)?;
            return Ok(());
        }
        self.query("CREATE INDEX records_group_id ON records(group_id)", &[])?;
        let s = match self.run.target.as_str() {
            "sqlServer" => "UPDATE STATISTICS records WITH FULLSCAN",
            "oracle" => "BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER,'RECORDS'); END;",
            "cockroachDb" => "CREATE STATISTICS records_stats FROM records",
            _ => "ANALYZE records",
        };
        self.query(s, &[])?;
        Ok(())
    }
    pub fn digest(&mut self) -> Result<[i64; 3]> {
        if self.http.is_none() {
            let count = if self.run.target == "sqlServer" {
                "COUNT_BIG(*)"
            } else {
                "COUNT(*)"
            };
            let sums = if self.run.target == "cockroachDb" {
                "CAST(COALESCE(SUM(id),0) AS BIGINT),CAST(COALESCE(SUM(amount_cents),0) AS BIGINT)"
            } else {
                "COALESCE(SUM(id),0),COALESCE(SUM(amount_cents),0)"
            };
            let (r, _) = self.query(&format!("SELECT {count},{sums} FROM records"), &[])?;
            return Ok([number(&r[0][0])?, number(&r[0][1])?, number(&r[0][2])?]);
        }
        self.request("POST", "/records/_refresh", None, false)?;
        let r=self.request("POST","/records/_search",Some(json!({"size":0,"track_total_hits":true,"aggs":{"ids":{"sum":{"field":"id"}},"amount":{"sum":{"field":"amount_cents"}}}})),false)?;
        Ok([
            r["hits"]["total"]["value"]
                .as_i64()
                .context("Missing count")?,
            r["aggregations"]["ids"]["value"]
                .as_f64()
                .context("Missing sum")? as i64,
            r["aggregations"]["amount"]["value"]
                .as_f64()
                .context("Missing sum")? as i64,
        ])
    }
    pub fn count(&mut self) -> Result<i64> {
        if self.http.is_none() {
            let (r, _) = self.query(
                if self.run.target == "sqlServer" {
                    "SELECT COUNT_BIG(*) FROM records"
                } else {
                    "SELECT COUNT(*) FROM records"
                },
                &[],
            )?;
            return number(&r[0][0]);
        }
        self.request("POST", "/records/_refresh", None, false)?;
        Ok(
            self.request("GET", "/records/_count", None, false)?["count"]
                .as_i64()
                .context("Missing count")?,
        )
    }
    pub fn metadata(&mut self) -> Result<Value> {
        let (version, driver) = if self.http.is_some() {
            (
                self.request("GET", "/", None, false)?["version"]["number"]
                    .as_str()
                    .context("Missing version")?
                    .to_owned(),
                "reqwest / HTTP JSON".to_owned(),
            )
        } else {
            let text = match self.run.target.as_str() {
                "oracle" => "SELECT banner FROM v$version WHERE ROWNUM=1",
                "sqlServer" => "SELECT CAST(SERVERPROPERTY('ProductVersion') AS VARCHAR(100))",
                _ => "SELECT version()",
            };
            let (r, _) = self.query(text, &[])?;
            (
                r[0][0].as_str().context("Missing version")?.to_owned(),
                match self.run.target.as_str() {
                    "oracle" => super::adapters::dependency("oracle"),
                    "sqlServer" => super::adapters::dependency("tiberius"),
                    "cockroachDb" => super::adapters::dependency("postgres"),
                    _ => "libloading / DuckDB 1.5.6 C API".into(),
                },
            )
        };
        let mut settings = json!({"generator":"splitmix64-ascii-v1","connections":"one native connection/client system thread","batch_transport":"parameterized row INSERTs in transaction","retries":"none","isolation":if self.run.target=="cockroachDb"{"SERIALIZABLE; single-node"}else{"driver/server default"}});
        if self.http.is_some() {
            settings["batch_transport"] =
                json!("NDJSON bulk create; non-atomic; each item checked");
            settings["search_corpus"] = json!("payload-terms16-v1");
            settings["durability_profile"] =
                json!("translog=request; refresh=1s; 1 primary; 0 replicas")
        }
        Ok(
            json!({"engine":"rust","runtime":env!("BENCHFORGE_RUSTC"),"driver":driver,"serverVersion":version,"settings":settings}),
        )
    }
    pub fn execute(&mut self, op: &str, key: i64, row: Option<&Row>, batch: &[Row]) -> Result<i64> {
        if op == "BatchInsert" {
            self.batch(batch)?;
            return Ok(batch.len() as i64);
        }
        if self.http.is_some() {
            return self.search(op, key, row);
        }
        let p = self.p(1);
        let select = "SELECT id,group_id,amount_cents,version,payload FROM records";
        let text = match op {
            "PointRead" => format!("{select} WHERE id={p}"),
            "RangeRead" => match self.run.target.as_str() {
                "sqlServer" => format!("SELECT TOP (100) id,group_id,amount_cents,version,payload FROM records WHERE id>={p} ORDER BY id"),
                "oracle" => format!("{select} WHERE id>={p} ORDER BY id FETCH FIRST 100 ROWS ONLY"),
                _ => format!("{select} WHERE id>={p} ORDER BY id LIMIT 100"),
            },
            "Aggregate" => {
                let count = if self.run.target == "sqlServer" { "COUNT_BIG(*)" } else { "COUNT(*)" };
                let sum = if self.run.target == "cockroachDb" { "CAST(SUM(amount_cents) AS BIGINT)" } else { "SUM(amount_cents)" };
                format!("SELECT group_id,{count},{sum} FROM records WHERE group_id={p} GROUP BY group_id")
            }
            "AnalyticalScan" => "SELECT COUNT(*),SUM(amount_cents),AVG(amount_cents) FROM records WHERE id>=? AND id<?".into(),
            "Update" => format!("UPDATE records SET version=version+1 WHERE id={p}"),
            "Delete" => format!("DELETE FROM records WHERE id={p}"),
            "Insert" => self.insert_sql(),
            _ => bail!("Unsupported SQL operation"),
        };
        let args = if op == "Insert" {
            Self::values(row.context("Missing insert row")?)
        } else if op == "AnalyticalScan" {
            vec![json!(key), json!(key + (self.run.rows / 100).max(100))]
        } else {
            vec![json!(if op == "Aggregate" {
                (key - 1) % 100
            } else {
                key
            })]
        };
        let (rows, n) = self.query(&text, &args)?;
        if matches!(
            op,
            "PointRead" | "RangeRead" | "Aggregate" | "AnalyticalScan"
        ) {
            if rows.is_empty()
                || op == "PointRead" && (rows.len() != 1 || number(&rows[0][0])? != key)
            {
                bail!("Invalid SQL response")
            }
            if matches!(op, "PointRead" | "RangeRead") {
                let mut previous = key - 1;
                for r in &rows {
                    let id = number(&r[0])?;
                    if id <= previous
                        || r[4].as_str().context("Missing payload")?.len()
                            != self.run.config.payload_bytes
                    {
                        bail!("Invalid ID or payload")
                    };
                    previous = id
                }
            }
            Ok(rows.len() as i64)
        } else {
            if n != 1 {
                bail!("Write did not affect one record")
            };
            Ok(1)
        }
    }
    fn search(&self, op: &str, key: i64, row: Option<&Row>) -> Result<i64> {
        let path = format!("/records/_doc/{key}");
        if op == "PointRead" {
            let r = self.request("GET", &path, None, false)?;
            if r["found"] != true
                || r["_source"]["id"] != key
                || r["_source"]["payload"]
                    .as_str()
                    .context("Missing payload")?
                    .len()
                    != self.run.config.payload_bytes
            {
                bail!("Invalid document")
            };
            return Ok(1);
        }
        if matches!(op, "Update" | "Insert" | "Delete") {
            let(r,expected)=match op{"Update"=>(self.request("POST",&format!("/records/_update/{key}?refresh=false&retry_on_conflict=0"),Some(json!({"script":{"source":"ctx._source.version += 1","lang":"painless"}})),false)?,"updated"),"Insert"=>{let row=row.context("Missing insert row")?;(self.request("PUT",&format!("/records/_create/{}?refresh=false",row.id),Some(Self::document(row)),false)?,"created")},_=>(self.request("DELETE",&(path+"?refresh=false"),None,false)?,"deleted")};
            if r["result"] != expected {
                bail!("Search write failed")
            };
            return Ok(1);
        }
        if op == "Aggregate" {
            let r=self.request("POST","/records/_search",Some(json!({"size":0,"track_total_hits":true,"query":{"term":{"group_id":(key-1)%100}},"aggs":{"amount":{"sum":{"field":"amount_cents"}}}})),false)?;
            if r["hits"]["total"]["value"].as_i64().unwrap_or(0) <= 0
                || !r["aggregations"]["amount"]["value"].is_number()
            {
                bail!("Empty aggregate")
            };
            return Ok(1);
        }
        let term = if op == "FullTextSearch" {
            Some(
                make_row(key, self.run.config.seed, self.run.config.payload_bytes).payload[..16]
                    .to_owned(),
            )
        } else {
            None
        };
        let body = if let Some(term) = &term {
            json!({"size":10,"query":{"match":{"search_text":term}}})
        } else {
            json!({"size":100,"query":{"range":{"id":{"gte":key}}},"sort":[{"id":"asc"}]})
        };
        let result = self.request("POST", "/records/_search", Some(body), false)?;
        let hits = result["hits"]["hits"].as_array().context("Missing hits")?;
        if hits.is_empty() {
            bail!("Empty search response")
        };
        let mut previous = key - 1;
        for h in hits {
            let r = &h["_source"];
            let id = r["id"].as_i64().context("Missing ID")?;
            if r["payload"].as_str().context("Missing payload")?.len()
                != self.run.config.payload_bytes
            {
                bail!("Invalid payload")
            };
            if let Some(term) = &term {
                if !r["search_text"]
                    .as_str()
                    .context("Missing search text")?
                    .split_whitespace()
                    .any(|word| word.eq_ignore_ascii_case(term))
                {
                    bail!("Invalid full-text result")
                }
            } else if id <= previous {
                bail!("Invalid range order")
            };
            previous = id;
        }
        Ok(hits.len() as i64)
    }
}
