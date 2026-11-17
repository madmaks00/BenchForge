use super::*;
use crate::{
    custom_query::{date, format_uuid, hex, Answer},
    portable::ALPHABET,
};
use num_bigint::BigInt;
use std::{fs::File, io::BufReader};

type SqlValue = Option<String>;

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Field {
    name: String,
    #[serde(rename = "type")]
    kind: String,
    generator: String,
    min: Value,
    max: Value,
    length: usize,
    scale: u32,
    prefix: String,
    choices: Vec<String>,
    reference: Option<String>,
    sample_column: String,
    sample_size: usize,
    constant: Option<String>,
    null_percent: f64,
    nullable: bool,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SqlCommand {
    sql: String,
    names: Vec<String>,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Step {
    result: String,
    min_rows: Option<i64>,
    max_rows: Option<i64>,
    expected_scalar: Option<String>,
    capture_as: Option<String>,
    capture_type: String,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct OperationDefinition {
    id: String,
    name: String,
    weight: u64,
    read_only: bool,
    transaction: bool,
    isolation: String,
    parameters: Vec<Field>,
    steps: Vec<Step>,
}
#[derive(Clone, Deserialize)]
struct Operation {
    definition: OperationDefinition,
    commands: Vec<SqlCommand>,
}
#[derive(Clone, Deserialize)]
struct TableDefinition {
    name: String,
    columns: Vec<Field>,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Dataset {
    definition: TableDefinition,
    rows: i64,
    seed: String,
    insert: SqlCommand,
    input_file: Option<String>,
}
#[derive(Clone, Deserialize)]
struct CheckDefinition {
    name: String,
    expected: String,
    comparison: String,
    before: bool,
    after: bool,
}
#[derive(Clone, Deserialize)]
struct Check {
    definition: CheckDefinition,
    command: SqlCommand,
}
#[derive(Clone, Deserialize)]
struct Unit {
    phase: String,
    operation: Option<String>,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Program {
    fingerprint: String,
    drop: Vec<String>,
    schema: Vec<String>,
    after_load: Vec<String>,
    datasets: Vec<Dataset>,
    operations: Vec<Operation>,
    checks: Vec<Check>,
    units: Vec<Unit>,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Workload {
    name: String,
    reset_between_operations: bool,
    prepare_commands: bool,
    command_timeout_seconds: u64,
}

#[derive(Clone, Copy)]
struct DataContext {
    sequence: i64,
    key: i64,
    client: usize,
}
struct Reservoir {
    values: Vec<String>,
    size: usize,
    column: usize,
    seen: u64,
    rng: Random,
}
#[derive(Default)]
struct Progress {
    completed: i64,
    by_operation: HashMap<String, i64>,
}
struct State {
    run: Arc<Run>,
    rows: HashMap<String, i64>,
    samples: HashMap<String, Reservoir>,
    sequence: AtomicI64,
    progress: Mutex<Progress>,
}
impl State {
    fn program(&self) -> &Program {
        self.run.sql_program.as_ref().unwrap()
    }
    fn workload(&self) -> &Workload {
        self.run.config.custom_workload.as_ref().unwrap()
    }
}
fn stable_seed(seed: u64, name: &str, row: i64) -> u64 {
    let hash = name.bytes().fold(14695981039346656037u64, |hash, b| {
        (hash ^ b as u64).wrapping_mul(1099511628211)
    });
    seed ^ hash ^ (row as u64).wrapping_mul(0xd6e8feb86659fd93)
}
fn sample_key(field: &Field) -> String {
    format!(
        "{}/{}/{}",
        field.reference.as_deref().unwrap_or(""),
        field.sample_column,
        field.sample_size
    )
}
fn int_value(value: &Value) -> Result<i64> {
    value.as_i64().context("Expected 64-bit integer")
}
fn integer(rng: &mut Random, min: i64, max: i64) -> i64 {
    let width = (max as u64).wrapping_sub(min as u64).wrapping_add(1);
    let raw = rng.next();
    (min as u64).wrapping_add(if width == 0 { raw } else { raw % width }) as i64
}
fn decimal_parts(text: &str) -> Result<(BigInt, u32)> {
    let (mantissa, exponent) = match text.split_once(['e', 'E']) {
        Some((m, e)) => (m, e.parse::<i32>()?),
        None => (text, 0),
    };
    let (digits, scale) = if let Some((a, b)) = mantissa.split_once('.') {
        (format!("{a}{b}"), i32::try_from(b.len())? - exponent)
    } else {
        (mantissa.to_owned(), -exponent)
    };
    if scale.unsigned_abs() > 512 {
        bail!("Decimal scale too large");
    }
    let mut n: BigInt = digits.parse()?;
    if scale < 0 {
        n *= BigInt::from(10).pow(scale.unsigned_abs());
    }
    Ok((n, scale.max(0) as u32))
}
fn compare_decimal(a: &str, b: &str) -> Result<std::cmp::Ordering> {
    let (aa, as_) = decimal_parts(a)?;
    let (bb, bs) = decimal_parts(b)?;
    let scale = as_.max(bs);
    Ok((aa * BigInt::from(10).pow(scale - as_)).cmp(&(bb * BigInt::from(10).pow(scale - bs))))
}
fn decimal(field: &Field, rng: &mut Random) -> Result<String> {
    let (lo, ls) = decimal_parts(&field.min.to_string())?;
    let (hi, hs) = decimal_parts(&field.max.to_string())?;
    let scale = ls.max(hs).max(field.scale);
    let a = lo * BigInt::from(10).pow(scale - ls);
    let b = hi * BigInt::from(10).pow(scale - hs);
    let division = BigInt::from(9007199254740992u64);
    let numerator = &a * &division + (b - a) * BigInt::from(rng.next() >> 11);
    let denom = division * BigInt::from(10).pow(scale - field.scale);
    let mut rounded = &numerator / &denom;
    let remainder = &numerator % &denom;
    let remainder = if remainder.sign() == num_bigint::Sign::Minus {
        -remainder
    } else {
        remainder
    };
    let times_two = remainder * 2u32;
    if times_two > denom || times_two == denom && rounded.bit(0) {
        if numerator.sign() == num_bigint::Sign::Minus {
            rounded -= 1;
        } else {
            rounded += 1;
        }
    }
    if field.scale == 0 {
        return Ok(rounded.to_string());
    }
    let text = rounded.to_string();
    let digits = text.trim_start_matches('-');
    let width = field.scale as usize + 1;
    let padded = format!("{digits:0>width$}");
    let pos = padded.len() - field.scale as usize;
    Ok(format!(
        "{}{}.{}",
        if text.starts_with('-') { "-" } else { "" },
        &padded[..pos],
        &padded[pos..]
    ))
}
fn normalize(value: SqlValue, kind: &str, target: &str) -> Result<SqlValue> {
    let Some(text) = value else { return Ok(None) };
    let value = match kind {
        "int32" => text.parse::<i32>()?.to_string(),
        "int64" => text.parse::<i64>()?.to_string(),
        "double" => text.parse::<f64>()?.to_string(),
        "boolean" => match text.to_ascii_lowercase().as_str() {
            "true" | "1" => "1".to_owned(),
            "false" | "0" => "0".to_owned(),
            _ => bail!("Invalid boolean"),
        },
        "dateTime" => {
            if let Ok(parsed) =
                time::OffsetDateTime::parse(&text, &time::format_description::well_known::Rfc3339)
            {
                date(parsed)
            } else {
                let normalized = text.replace('T', " ");
                let (seconds, fraction) = normalized.split_once('.').unwrap_or((&normalized, ""));
                if seconds.len() == 19 && fraction.bytes().all(|item| item.is_ascii_digit()) {
                    format!("{seconds}.{:0<6}", &fraction[..fraction.len().min(6)])
                } else if seconds.len() == 10 {
                    format!("{seconds} 00:00:00.000000")
                } else {
                    bail!("Invalid date/time: {text}")
                }
            }
        }
        "guid" => {
            let packed = text.replace('-', "");
            if packed.len() != 32 || !packed.bytes().all(|b| b.is_ascii_hexdigit()) {
                bail!("Invalid GUID");
            }
            let mut raw = [0u8; 16];
            for (index, value) in raw.iter_mut().enumerate() {
                *value = u8::from_str_radix(&packed[index * 2..index * 2 + 2], 16)?;
            }
            if target == "oracle" {
                if text.contains('-') {
                    raw[..4].reverse();
                    raw[4..6].reverse();
                    raw[6..8].reverse();
                }
                hex(&raw)
            } else {
                format_uuid(raw, false)
            }
        }
        _ => text,
    };
    Ok(Some(value))
}
impl State {
    fn generate(&self, field: &Field, c: DataContext, rng: &mut Random) -> Result<SqlValue> {
        if field.null_percent > 0. && ((rng.next() % 1000000) as f64) < field.null_percent * 10000.
        {
            return Ok(None);
        }
        let value = match field.generator.as_str() {
            "sequence" => Some(
                int_value(&field.min)?
                    .checked_add(c.sequence - 1)
                    .context("Sequence overflow")?
                    .to_string(),
            ),
            "integer" => {
                Some(integer(rng, int_value(&field.min)?, int_value(&field.max)?).to_string())
            }
            "decimal" => Some(decimal(field, rng)?),
            "text" => {
                let mut value = field
                    .prefix
                    .replace("{row}", &c.sequence.to_string())
                    .replace("{sequence}", &c.sequence.to_string())
                    .replace("{key}", &c.key.to_string())
                    .replace("{client}", &c.client.to_string());
                for _ in 0..field.length {
                    value.push(ALPHABET[(rng.next() % 62) as usize] as char);
                }
                Some(value)
            }
            "choice" => {
                Some(field.choices[(rng.next() % field.choices.len() as u64) as usize].clone())
            }
            "reference" => {
                let key = field
                    .reference
                    .as_deref()
                    .context("Missing referenced table")?
                    .to_ascii_lowercase();
                let count = *self.rows.get(&key).context("Unknown referenced table")?;
                if count <= 0 {
                    bail!("Empty referenced table");
                }
                Some(integer(rng, 1, count).to_string())
            }
            "guid" => {
                let mut bytes = [0u8; 16];
                bytes[..8].copy_from_slice(&rng.next().to_le_bytes());
                bytes[8..].copy_from_slice(&rng.next().to_le_bytes());
                Some(if self.run.target == "oracle" {
                    hex(&bytes)
                } else {
                    format_uuid(bytes, true)
                })
            }
            "timestamp" => Some(date(time::OffsetDateTime::from_unix_timestamp(integer(
                rng,
                int_value(&field.min)?,
                int_value(&field.max)?,
            ))?)),
            "boolean" => Some(if rng.next() % 2 == 0 { "1" } else { "0" }.to_owned()),
            "constant" => field.constant.clone(),
            "key" => Some(c.key.to_string()),
            "client" => Some(c.client.to_string()),
            "sample" => {
                let pool = &self
                    .samples
                    .get(&sample_key(field))
                    .context("Missing sample reservoir")?
                    .values;
                if pool.is_empty() {
                    bail!("No non-null sample values")
                }
                Some(pool[(rng.next() % pool.len() as u64) as usize].clone())
            }
            kind => bail!("Unknown generator: {kind}"),
        };
        normalize(value, &field.kind, &self.run.target)
    }
    fn arguments(
        &self,
        cmd: &SqlCommand,
        defined: &HashMap<String, SqlValue>,
        ctx: DataContext,
    ) -> Result<Vec<SqlValue>> {
        cmd.names
            .iter()
            .map(|name| {
                let key = name.to_ascii_lowercase();
                if let Some(value) = defined.get(&key) {
                    return Ok(value.clone());
                }
                let value = match key.as_str() {
                    "key" => ctx.key,
                    "client" => ctx.client as i64,
                    "sequence" => self.run.rows + ctx.sequence,
                    "baserows" => self.run.rows,
                    "seed" => self.run.config.seed as i64,
                    "completed" => self.progress.lock().unwrap().completed,
                    name if name.starts_with("rows_") => {
                        *self.rows.get(&name[5..]).context("Unknown table")?
                    }
                    name if name.starts_with("completed_") => *self
                        .progress
                        .lock()
                        .unwrap()
                        .by_operation
                        .get(&name[10..])
                        .unwrap_or(&0),
                    _ => bail!("Unknown SQL parameter @{name}"),
                };
                Ok(Some(value.to_string()))
            })
            .collect()
    }
    fn query(
        &self,
        db: &mut Database,
        cmd: &str,
        args: &[SqlValue],
        mode: &str,
        setup: bool,
    ) -> Result<Answer> {
        db.custom_query(
            cmd,
            args,
            mode,
            self.workload().prepare_commands,
            Duration::from_secs(if setup {
                600
            } else {
                self.workload().command_timeout_seconds
            }),
        )
    }
    fn verify(&self, db: &mut Database, before: bool) -> Result<()> {
        for item in &self.program().checks {
            let check = &item.definition;
            if if before { !check.before } else { !check.after } {
                continue;
            }
            let args = self.arguments(
                &item.command,
                &HashMap::new(),
                DataContext {
                    sequence: 0,
                    key: 0,
                    client: 0,
                },
            )?;
            let value = self
                .query(db, &item.command.sql, &args, "scalar", false)?
                .scalar
                .context("Integrity check returned NULL")?;
            let expected = if check.expected == "baseRows" {
                self.run.rows.to_string()
            } else if let Some(table) = check.expected.strip_prefix("rows:") {
                self.rows[&table.to_ascii_lowercase()].to_string()
            } else {
                check.expected.clone()
            };
            let order = compare_decimal(&value, &expected)?;
            let passed = match check.comparison.as_str() {
                "atLeast" => !order.is_lt(),
                "atMost" => !order.is_gt(),
                _ => order.is_eq(),
            };
            if !passed {
                bail!("Integrity check failed: {}", check.name)
            }
        }
        Ok(())
    }
    fn reset(&mut self, db: &mut Database) -> Result<i64> {
        self.sequence.store(0, Ordering::Relaxed);
        *self.progress.get_mut().unwrap() = Progress::default();
        self.samples.clear();
        let sqlite = self.run.target == "sqlite";
        if sqlite {
            self.query(db, "PRAGMA foreign_keys=OFF", &[], "nonQuery", true)?;
        }
        let outcome = (|| {
            for command in self.program().drop.iter().chain(&self.program().schema) {
                check()?;
                self.query(db, command, &[], "nonQuery", true)?;
            }
            Ok(0)
        })();
        if sqlite {
            self.query(db, "PRAGMA foreign_keys=ON", &[], "nonQuery", true)?;
        }
        outcome
    }
    fn load(&mut self, db: &mut Database) -> Result<i64> {
        let tables = self.program().datasets.clone();
        let mut loaded = 0;
        for table in &tables {
            let mut reservoirs = HashMap::new();
            for op in &self.program().operations {
                for field in &op.definition.parameters {
                    if field.generator != "sample"
                        || field.reference.as_deref() != Some(&table.definition.name)
                    {
                        continue;
                    }
                    let key = sample_key(field);
                    let column = table
                        .definition
                        .columns
                        .iter()
                        .position(|col| col.name == field.sample_column)
                        .context("Missing sample column")?;
                    let rng = Random(stable_seed(self.run.config.seed, &key, 0));
                    reservoirs.entry(key).or_insert(Reservoir {
                        values: vec![],
                        size: field.sample_size,
                        column,
                        seen: 0,
                        rng,
                    });
                }
            }
            let mut input = table
                .input_file
                .as_ref()
                .map(|file| File::open(file).map(|f| BufReader::new(f).lines()))
                .transpose()?;
            let seed = table.seed.parse::<u64>()?;
            let mut open = false;
            let outcome = (|| -> Result<()> {
                for index in 1..=table.rows {
                    check()?;
                    if !open {
                        self.transaction(db, "default")?;
                        open = true;
                    }
                    let values: Vec<SqlValue> = if let Some(lines) = &mut input {
                        let line = lines.next().context("Input row count changed")??;
                        serde_json::from_str(&line)?
                    } else {
                        let mut rng =
                            Random(seed ^ (index as u64).wrapping_mul(0xd6e8feb86659fd93));
                        let ctx = DataContext {
                            sequence: index,
                            key: index,
                            client: 0,
                        };
                        table
                            .definition
                            .columns
                            .iter()
                            .map(|column| self.generate(column, ctx, &mut rng))
                            .collect::<Result<_>>()?
                    };
                    if values.len() != table.definition.columns.len() {
                        bail!("Input number of columns changed");
                    }
                    for (value, column) in values.iter().zip(&table.definition.columns) {
                        if value.is_none() && !column.nullable {
                            bail!("NULL in required column {}", column.name);
                        }
                    }
                    self.query(db, &table.insert.sql, &values, "nonQuery", true)?;
                    loaded += 1;
                    for pool in reservoirs.values_mut() {
                        let Some(value) = &values[pool.column] else {
                            continue;
                        };
                        pool.seen += 1;
                        if pool.values.len() < pool.size {
                            pool.values.push(value.clone());
                        } else {
                            let pick = (pool.rng.next() % pool.seen) as usize;
                            if pick < pool.size {
                                pool.values[pick] = value.clone();
                            }
                        }
                    }
                    if index % self.run.config.batch_size as i64 == 0 || index == table.rows {
                        self.finish(db, true)?;
                        open = false;
                    }
                }
                Ok(())
            })();
            if open {
                let _ = self.finish(db, false);
            }
            outcome?;
            self.samples.extend(reservoirs);
        }
        Ok(loaded)
    }
    fn transaction(&self, db: &mut Database, isolation: &str) -> Result<()> {
        let level = match isolation {
            "readCommitted" => "READ COMMITTED",
            "repeatableRead" => "REPEATABLE READ",
            "serializable" => "SERIALIZABLE",
            _ => "",
        };
        let target = self.run.target.as_str();
        let execute =
            |db: &mut Database, sql: &str| self.query(db, sql, &[], "nonQuery", false).map(|_| ());
        match target {
            "oracle" => {
                if !level.is_empty() {
                    execute(db, &format!("SET TRANSACTION ISOLATION LEVEL {level}"))?;
                }
                execute(db, "BEGIN")
            }
            "sqlite" | "duckDb" => {
                if !matches!(isolation, "default" | "serializable") {
                    bail!("Unsupported isolation {isolation}");
                }
                execute(db, "BEGIN TRANSACTION")
            }
            "mySql" | "mariaDb" | "sqlServer" => {
                if !level.is_empty() {
                    execute(db, &format!("SET TRANSACTION ISOLATION LEVEL {level}"))?;
                }
                execute(
                    db,
                    if target == "sqlServer" {
                        "BEGIN TRANSACTION"
                    } else {
                        "START TRANSACTION"
                    },
                )
            }
            _ => execute(
                db,
                &if level.is_empty() {
                    "BEGIN".to_owned()
                } else {
                    format!("BEGIN ISOLATION LEVEL {level}")
                },
            ),
        }
    }
    fn finish(&self, db: &mut Database, commit: bool) -> Result<()> {
        self.query(
            db,
            if commit { "COMMIT" } else { "ROLLBACK" },
            &[],
            "nonQuery",
            false,
        )?;
        Ok(())
    }
}
#[derive(Default)]
struct Detail {
    id: String,
    name: String,
    successful: i64,
    rows: i64,
    errors: i64,
    service: Histogram,
    end: Histogram,
}
impl Detail {
    fn merge(&mut self, d: &Detail) {
        self.successful += d.successful;
        self.rows += d.rows;
        self.errors += d.errors;
        self.service.merge(&d.service);
        self.end.merge(&d.end);
    }
    fn value(&self) -> Value {
        json!({"id":self.id,"name":self.name,"successfulOperations":self.successful,"successfulRows":self.rows,"errors":self.errors,
            "serviceLatency":self.service.snapshot(),"endToEndLatency":self.end.snapshot()})
    }
}
pub struct Session {
    db: Database,
    state: Arc<State>,
    choice: Vec<usize>,
    weight: u64,
    mixed: bool,
    rng: Random,
    client: usize,
    details: HashMap<String, Detail>,
}
impl Session {
    pub fn execute(&mut self, key: i64, scheduled: Instant) -> Result<i64> {
        let start = Instant::now();
        let program = self.state.program();
        let mut op = &program.operations[self.choice[0]];
        if self.mixed {
            let mut pick = self.rng.next() % self.weight;
            for index in &self.choice {
                let candidate = &program.operations[*index];
                if pick < candidate.definition.weight {
                    op = candidate;
                    break;
                }
                pick -= candidate.definition.weight;
            }
        }
        let definition = &op.definition;
        let det = self
            .details
            .entry(definition.id.clone())
            .or_insert_with(|| Detail {
                id: definition.id.clone(),
                name: definition.name.clone(),
                ..Detail::default()
            });
        let ctx = DataContext {
            sequence: self.state.sequence.fetch_add(1, Ordering::Relaxed) + 1,
            key,
            client: self.client,
        };
        let mut began = false;
        let outcome = (|| -> Result<i64> {
            let mut data = HashMap::new();
            for field in &definition.parameters {
                data.insert(
                    field.name.to_ascii_lowercase(),
                    self.state.generate(field, ctx, &mut self.rng)?,
                );
            }
            if definition.transaction {
                self.state
                    .transaction(&mut self.db, &definition.isolation)?;
                began = true;
            }
            let mut affected = 0;
            for (step, cmd) in definition.steps.iter().zip(&op.commands) {
                let args = self.state.arguments(cmd, &data, ctx)?;
                let result =
                    self.state
                        .query(&mut self.db, &cmd.sql, &args, &step.result, false)?;
                if let Some(expected) = &step.expected_scalar {
                    let actual = result.scalar.as_deref().unwrap_or("");
                    if actual != expected
                        && !compare_decimal(actual, expected).is_ok_and(|r| r.is_eq())
                    {
                        bail!("Unexpected scalar result");
                    }
                }
                if let Some(name) = &step.capture_as {
                    let value =
                        normalize(result.scalar, &step.capture_type, &self.state.run.target)?;
                    data.insert(name.to_ascii_lowercase(), value);
                }
                if step.min_rows.is_some_and(|min| result.count < min)
                    || step.max_rows.is_some_and(|max| result.count > max)
                {
                    bail!("Unexpected SQL row count");
                }
                affected += result.count.max(0);
            }
            if began {
                self.state.finish(&mut self.db, true)?;
                began = false;
            }
            Ok(affected)
        })();
        match outcome {
            Ok(affected) => {
                let end = Instant::now();
                det.successful += 1;
                det.rows += affected;
                det.service
                    .add(end.duration_since(start).as_secs_f64() * 1e6);
                det.end
                    .add(end.saturating_duration_since(scheduled).as_secs_f64() * 1e6);
                Ok(affected)
            }
            Err(error) => {
                det.errors += 1;
                if began {
                    let _ = self.state.finish(&mut self.db, false);
                }
                Err(error)
            }
        }
    }
}
fn setup(run: &Run, phase: &str, load: bool, action: impl FnOnce() -> Result<i64>) -> Result<()> {
    emit(
        Some(run),
        "phase_start",
        json!({"phase":phase,"targetSeconds":0}),
    )?;
    let started = Instant::now();
    let outcome = action();
    emit(Some(run), "phase_end", json!({}))?;
    let mut entry = base(run, phase);
    let seconds = started.elapsed().as_secs_f64().max(0.000001);
    entry["elapsedSeconds"] = json!(seconds);
    entry["unit"] = json!(if load { "rows/s" } else { "seconds" });
    match outcome {
        Ok(count) => {
            entry["successfulOperations"] = json!(1);
            entry["verified"] = json!(true);
            if load {
                entry["successfulRows"] = json!(count);
                entry["rowsPerSecond"] = json!(count as f64 / seconds);
            }
            emit(Some(run), "result", entry)?;
            Ok(())
        }
        Err(error) => {
            if !CANCELLED.load(Ordering::Relaxed) {
                let mut message = error.to_string();
                if !run.endpoint.password.is_empty() {
                    message = message.replace(&run.endpoint.password, "[redacted]");
                }
                entry["errors"] = json!(1);
                entry["verified"] = json!(false);
                entry["errorExamples"] = json!({message.chars().take(500).collect::<String>():1});
                emit(Some(run), "result", entry)?;
            }
            Err(error)
        }
    }
}
fn add_sessions(
    state: &Arc<State>,
    sessions: &mut Vec<Database>,
    count: usize,
    selected: Option<&str>,
    warm: bool,
) -> Result<()> {
    let choice: Vec<usize> = state
        .program()
        .operations
        .iter()
        .enumerate()
        .filter(|(_, op)| {
            selected.is_none_or(|id| op.definition.id == id) && (!warm || op.definition.read_only)
        })
        .map(|(i, _)| i)
        .collect();
    let weight = choice
        .iter()
        .map(|i| state.program().operations[*i].definition.weight)
        .sum();
    while sessions.len() < count {
        check()?;
        let client = sessions.len() + 1;
        let mut db = Database::open(state.run.clone())?;
        if state.run.target == "sqlite" {
            state.query(&mut db, "PRAGMA foreign_keys=ON", &[], "nonQuery", true)?;
        }
        sessions.push(Database::Custom(Box::new(Session {
            db,
            state: state.clone(),
            choice: choice.clone(),
            weight,
            mixed: selected.is_none(),
            rng: Random(stable_seed(
                state.run.config.seed ^ state.run.repeat as u64,
                "client",
                client as i64,
            )),
            client,
            details: HashMap::new(),
        })));
    }
    Ok(())
}
pub fn benchmark(run: Arc<Run>, mut db: Database) -> Result<()> {
    let program = run
        .sql_program
        .as_ref()
        .context("Missing compiled SQL program. Update BenchForge.")?;
    let mut state = Arc::new(State {
        run: run.clone(),
        samples: HashMap::new(),
        rows: program
            .datasets
            .iter()
            .map(|table| (table.definition.name.to_ascii_lowercase(), table.rows))
            .collect(),
        sequence: AtomicI64::new(0),
        progress: Mutex::new(Progress::default()),
    });
    let mut metadata = db.metadata()?;
    metadata["settings"]["workload_name"] = json!(state.workload().name);
    metadata["settings"]["workload_sha256"] = json!(program.fingerprint);
    metadata["settings"]["generator"] = json!("custom-splitmix64-v1");
    metadata["settings"]["parameter_binding"] = json!("nullable text with typed SQL casts");
    emit(Some(&run), "metadata", metadata)?;
    let stages = run
        .config
        .plan
        .as_ref()
        .map(|p| p.stages.clone())
        .unwrap_or_else(|| {
            vec![Stage {
                name: "Load".into(),
                clients: run.clients,
                seconds: run.config.duration_seconds,
                operations_per_second: (run.config.load_model == "constantRate")
                    .then_some(run.config.target_ops_per_second),
            }]
        });
    let mut initialized = false;
    for unit in &program.units {
        check()?;
        let prefix = format!(
            "custom/setup/{}/",
            unit.operation.as_deref().unwrap_or("mix")
        );
        if !initialized || state.workload().reset_between_operations {
            let mutable = Arc::get_mut(&mut state).context("SQL sessions still active")?;
            setup(&run, &(prefix.clone() + "schema"), false, || {
                mutable.reset(&mut db)
            })?;
            setup(&run, &(prefix.clone() + "load"), true, || {
                mutable.load(&mut db)
            })?;
            setup(&run, &(prefix.clone() + "indexes"), false, || {
                for stmt in &program.after_load {
                    check()?;
                    mutable.query(&mut db, stmt, &[], "nonQuery", true)?;
                }
                Ok(0)
            })?;
            initialized = true;
        }
        if program.checks.iter().any(|item| item.definition.before) {
            setup(&run, &(prefix.clone() + "before"), false, || {
                state.verify(&mut db, true)?;
                Ok(0)
            })?;
        }
        let warm = run.config.warmup_seconds;
        let selected = unit.operation.as_deref();
        if warm > 0.
            && program.operations.iter().any(|op| {
                op.definition.read_only && selected.is_none_or(|id| op.definition.id == id)
            })
        {
            let mut clients = vec![];
            add_sessions(&state, &mut clients, stages[0].clients, selected, true)?;
            let result = measure(
                &run,
                &mut clients,
                &mut vec![0; run.clients],
                &AtomicI64::new(0),
                &unit.phase,
                warm,
                stages[0].clients,
                None,
                None,
                false,
            )?;
            if result["errors"].as_u64().unwrap_or(0) > 0 {
                bail!("Errors during custom SQL warmup");
            }
        }
        let mut clients = vec![];
        let mut sequences = vec![0; run.clients];
        for (index, stage) in stages.iter().enumerate() {
            check()?;
            add_sessions(&state, &mut clients, stage.clients, selected, false)?;
            for db in &mut clients {
                if let Database::Custom(session) = db {
                    session.details.clear();
                }
            }
            let stamp=run.config.plan.as_ref().map(|_|json!({"number":index+1,"name":stage.name,"targetSeconds":stage.seconds,"operationsPerSecond":stage.operations_per_second}));
            emit(
                Some(&run),
                "phase_start",
                json!({"phase":unit.phase,"targetSeconds":stage.seconds,"stage":stamp,"clients":stage.clients}),
            )?;
            let mut result = measure(
                &run,
                &mut clients,
                &mut sequences,
                &AtomicI64::new(0),
                &unit.phase,
                stage.seconds,
                stage.clients,
                stage.operations_per_second,
                stamp,
                true,
            )?;
            emit(Some(&run), "phase_end", json!({}))?;
            let successful = result["successfulOperations"].as_i64().unwrap_or(0);
            let mut details = HashMap::<String, Detail>::new();
            for client in &clients[..stage.clients] {
                if let Database::Custom(session) = client {
                    for item in session.details.values() {
                        details
                            .entry(item.id.clone())
                            .or_insert_with(|| Detail {
                                id: item.id.clone(),
                                name: item.name.clone(),
                                ..Detail::default()
                            })
                            .merge(item);
                    }
                }
            }
            {
                let mut progress = state.progress.lock().unwrap();
                progress.completed += successful;
                for item in details.values() {
                    *progress
                        .by_operation
                        .entry(item.id.to_ascii_lowercase())
                        .or_default() += item.successful;
                }
            }
            result["operationBreakdown"] = json!(program
                .operations
                .iter()
                .filter_map(|op| details.get(&op.definition.id).map(Detail::value))
                .collect::<Vec<_>>());
            let cancelled = CANCELLED.load(Ordering::Relaxed);
            let verified = if cancelled {
                Ok(())
            } else {
                state.verify(&mut db, false)
            };
            result["verified"] = json!(!cancelled && successful > 0 && verified.is_ok());
            emit(Some(&run), "result", result)?;
            verified?;
            check()?;
        }
    }
    Ok(())
}
