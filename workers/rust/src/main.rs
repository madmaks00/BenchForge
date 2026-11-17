mod adapters;
mod duckdb;
mod extended;
mod portable;
use adapters::Database;
use anyhow::{bail, Context, Result};
use portable::{row, Histogram, Random, GENERATOR};
use serde::Deserialize;
use serde_json::{json, Value};
use std::{
    collections::HashMap,
    io::{self, BufRead, Write},
    sync::{
        atomic::{AtomicBool, AtomicI64, AtomicU64, Ordering},
        Arc, Barrier, Mutex, OnceLock,
    },
    thread,
    time::{Duration, Instant},
};
use sysinfo::{Pid, ProcessesToUpdate, System};

static CANCELLED: AtomicBool = AtomicBool::new(false);
static OUTPUT: Mutex<()> = Mutex::new(());
const SCENARIOS: [&str; 10] = [
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
];
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Endpoint {
    host: String,
    port: u16,
    username: String,
    password: String,
    database: String,
    #[serde(default)]
    file_path: Option<String>,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Stage {
    name: String,
    clients: usize,
    seconds: f64,
    operations_per_second: Option<u64>,
}
#[derive(Clone, Deserialize)]
struct Plan {
    stages: Vec<Stage>,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase", default)]
struct Config {
    memory_mb: u64,
    cpu_limit: f64,
    duration_seconds: f64,
    warmup_seconds: f64,
    payload_bytes: usize,
    batch_size: usize,
    seed: u64,
    hotspot: bool,
    load_model: String,
    target_ops_per_second: u64,
    max_start_lag_ms: f64,
    scenarios: Vec<String>,
    plan: Option<Plan>,
}
impl Default for Config {
    fn default() -> Self {
        Self {
            memory_mb: 512,
            cpu_limit: 2.,
            duration_seconds: 5.,
            warmup_seconds: 2.,
            payload_bytes: 256,
            batch_size: 1000,
            seed: 42,
            hotspot: false,
            load_model: "closedLoop".into(),
            target_ops_per_second: 1000,
            max_start_lag_ms: 20.,
            scenarios: vec![],
            plan: None,
        }
    }
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Run {
    run_id: String,
    target: String,
    rows: i64,
    clients: usize,
    repeat: usize,
    config: Config,
    endpoint: Endpoint,
    #[serde(default)]
    engine: String,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Command {
    protocol_version: i32,
    #[serde(rename = "type")]
    kind: String,
    run: Option<Run>,
}
fn emit(run: Option<&Run>, kind: &str, data: Value) -> Result<()> {
    let _guard = OUTPUT
        .lock()
        .map_err(|_| anyhow::anyhow!("Output lock failed"))?;
    let mut output = io::stdout().lock();
    serde_json::to_writer(
        &mut output,
        &json!({"protocolVersion":1,"type":kind,"runId":run.map(|r|r.run_id.as_str()).unwrap_or(""),"data":data}),
    )?;
    writeln!(output)?;
    output.flush()?;
    Ok(())
}
fn check() -> Result<()> {
    if CANCELLED.load(Ordering::Relaxed) {
        bail!("Cancelled")
    };
    Ok(())
}
fn batch_size(config: &Config) -> usize {
    config
        .batch_size
        .min((8 * 1024 * 1024 / (config.payload_bytes + 128)).max(1))
}
fn capabilities(target: &str) -> Vec<&'static str> {
    match target {
        "redis" | "valkey" => vec![
            "PointRead",
            "Update",
            "Mixed",
            "Insert",
            "BatchInsert",
            "Delete",
        ],
        "clickHouse" => vec!["RangeRead", "Aggregate", "AnalyticalScan", "BatchInsert"],
        "duckDb" => SCENARIOS
            .iter()
            .copied()
            .filter(|s| *s != "FullTextSearch")
            .collect(),
        "elasticsearch" | "openSearch" => SCENARIOS
            .iter()
            .copied()
            .filter(|s| *s != "AnalyticalScan")
            .collect(),
        _ => SCENARIOS
            .iter()
            .copied()
            .filter(|s| *s != "AnalyticalScan" && *s != "FullTextSearch")
            .collect(),
    }
}
fn base(run: &Run, phase: &str) -> Value {
    json!({"engine":"rust","target":run.target,"datasetRows":run.rows,"clients":run.clients,"repeat":run.repeat,"phase":phase})
}
struct Counts {
    success: i64,
    rows: i64,
    errors: i64,
    dropped: i64,
    service: Histogram,
    end: Histogram,
    failed: Histogram,
    examples: HashMap<String, i64>,
    finished: Instant,
}
impl Counts {
    fn new() -> Self {
        Self {
            success: 0,
            rows: 0,
            errors: 0,
            dropped: 0,
            service: Histogram::default(),
            end: Histogram::default(),
            failed: Histogram::default(),
            examples: HashMap::new(),
            finished: Instant::now(),
        }
    }
}
fn process_metrics(system: &mut System) -> Result<(u64, f64)> {
    let pid = Pid::from_u32(std::process::id());
    system.refresh_processes(ProcessesToUpdate::Some(&[pid]), true);
    let process = system
        .process(pid)
        .context("Could not read process metrics")?;
    Ok((
        process.accumulated_cpu_time(),
        process.memory() as f64 / 1048576.,
    ))
}
fn measure(
    run: &Run,
    sessions: &mut [Database],
    sequences: &mut [i64],
    deleted: &AtomicI64,
    operation: &str,
    seconds: f64,
    clients: usize,
    rate: Option<u64>,
    stamp: Option<Value>,
    progress: bool,
) -> Result<Value> {
    let config = &run.config;
    let planned = rate
        .map(|r| (seconds * r as f64).floor() as i64)
        .unwrap_or(0);
    let number = stamp
        .as_ref()
        .and_then(|s| s["number"].as_u64())
        .unwrap_or(0);
    let scenario = SCENARIOS
        .iter()
        .position(|s| *s == operation)
        .context("Unknown scenario")?;
    let gate = Arc::new(Barrier::new(clients + 1));
    let start = Arc::new(OnceLock::<Instant>::new());
    let live_success = Arc::new(AtomicU64::new(0));
    let live_errors = Arc::new(AtomicU64::new(0));
    let mut system = System::new();
    thread::scope(|scope| -> Result<Value> {
        let mut tasks = Vec::new();
        for (index, (database, sequence)) in sessions
            .iter_mut()
            .zip(sequences.iter_mut())
            .take(clients)
            .enumerate()
        {
            let gate = gate.clone();
            let start = start.clone();
            let live_success = live_success.clone();
            let live_errors = live_errors.clone();
            let stamped = stamp.is_some();
            tasks.push(scope.spawn(move || {
                gate.wait();
                let started = *start.get().expect("start set before gate");
                let deadline = started + Duration::from_secs_f64(seconds);
                let mut c = Counts::new();
                let mut rng = Random(
                    config.seed
                        ^ ((index as u64 + 1) * 104729)
                        ^ run.repeat as u64
                        ^ number * 32452843,
                );
                let mut current = *sequence;
                let mut slot = index as i64;
                while !CANCELLED.load(Ordering::Relaxed) && Instant::now() < deadline {
                    let mut scheduled = Instant::now();
                    if let Some(rate) = rate {
                        if slot >= planned {
                            break;
                        };
                        scheduled = started + Duration::from_secs_f64(slot as f64 / rate as f64);
                        let lag = Instant::now()
                            .saturating_duration_since(scheduled)
                            .as_secs_f64()
                            - config.max_start_lag_ms / 1000.;
                        if lag > 0. {
                            let skipped = ((lag * rate as f64 / clients as f64).floor() as i64 + 1)
                                .max(1)
                                .min((planned - 1 - slot) / clients as i64 + 1);
                            c.dropped += skipped;
                            slot += skipped * clients as i64;
                            continue;
                        }
                        while Instant::now() < scheduled && !CANCELLED.load(Ordering::Relaxed) {
                            thread::sleep(Duration::from_millis(1));
                        }
                        if CANCELLED.load(Ordering::Relaxed) || Instant::now() >= deadline {
                            break;
                        };
                        if Instant::now()
                            .saturating_duration_since(scheduled)
                            .as_secs_f64()
                            > config.max_start_lag_ms / 1000.
                        {
                            c.dropped += 1;
                            slot += clients as i64;
                            continue;
                        }
                    }
                    let selected = if operation == "Mixed" {
                        if rng.next() % 100 < 80 {
                            "PointRead"
                        } else {
                            "Update"
                        }
                    } else {
                        operation
                    };
                    let mut key = rng.key(run.rows, config.hotspot);
                    let mut one = None;
                    let mut batch = Vec::new();
                    if selected == "Insert" || selected == "BatchInsert" {
                        let size = if selected == "BatchInsert" {
                            batch_size(config)
                        } else {
                            1
                        };
                        let first = (scenario as i64 + 1) * 1_000_000_000_000
                            + (current * run.clients as i64 + index as i64) * size as i64
                            + 1;
                        if selected == "Insert" {
                            one = Some(row(first, config.seed, config.payload_bytes));
                        } else {
                            batch = (0..size)
                                .map(|i| row(first + i as i64, config.seed, config.payload_bytes))
                                .collect();
                        }
                    }
                    if selected == "Delete" {
                        key = if stamped {
                            deleted.fetch_add(1, Ordering::Relaxed) + 1
                        } else {
                            current * clients as i64 + index as i64 + 1
                        };
                        if key > run.rows {
                            break;
                        }
                    }
                    let before = Instant::now();
                    match database.execute(selected, key, one.as_ref(), &batch) {
                        Ok(affected) => {
                            let finish = Instant::now();
                            c.success += 1;
                            c.rows += affected;
                            live_success.fetch_add(1, Ordering::Relaxed);
                            c.service.add(
                                finish.duration_since(before).as_secs_f64().max(0.000001) * 1e6,
                            );
                            c.end.add(
                                finish
                                    .saturating_duration_since(if rate.is_some() {
                                        scheduled
                                    } else {
                                        before
                                    })
                                    .as_secs_f64()
                                    .max(0.000001)
                                    * 1e6,
                            );
                        }
                        Err(error) => {
                            c.errors += 1;
                            live_errors.fetch_add(1, Ordering::Relaxed);
                            c.failed
                                .add(before.elapsed().as_secs_f64().max(0.000001) * 1e6);
                            let mut message = error.to_string();
                            if !run.endpoint.password.is_empty() {
                                message = message.replace(&run.endpoint.password, "[redacted]");
                            }
                            message = message.chars().take(250).collect();
                            *c.examples.entry(message).or_default() += 1;
                        }
                    }
                    current += 1;
                    slot += clients as i64;
                    if c.errors >= 50 {
                        break;
                    }
                }
                if rate.is_some() && slot < planned && !CANCELLED.load(Ordering::Relaxed) {
                    c.dropped += (planned - 1 - slot) / clients as i64 + 1;
                }
                *sequence = current;
                c.finished = Instant::now();
                c
            }));
        }
        let (cpu_before, mut rss) = process_metrics(&mut system)?;
        let started = Instant::now();
        start
            .set(started)
            .map_err(|_| anyhow::anyhow!("Duplicate start"))?;
        gate.wait();
        let mut sample_at = started + Duration::from_millis(250);
        let mut report_at = started + Duration::from_secs(1);
        while tasks.iter().any(|task| !task.is_finished()) {
            thread::sleep(Duration::from_millis(10));
            let now = Instant::now();
            if now >= sample_at {
                rss = rss.max(process_metrics(&mut system)?.1);
                sample_at = now + Duration::from_millis(250);
            }
            if progress && now >= report_at {
                emit(
                    Some(run),
                    "progress",
                    json!({"phase":operation,"stage":stamp.as_ref().map(|s|s["name"].clone()),"clients":clients,"targetSeconds":seconds,"successful":live_success.load(Ordering::Relaxed),"errors":live_errors.load(Ordering::Relaxed),"elapsedSeconds":started.elapsed().as_secs_f64()}),
                )?;
                report_at = now + Duration::from_secs(1);
            }
        }
        let mut counts = Vec::new();
        for task in tasks {
            counts.push(
                task.join()
                    .map_err(|_| anyhow::anyhow!("Client thread panicked"))?,
            );
        }
        let end = counts.iter().map(|c| c.finished).max().unwrap_or(started);
        let elapsed = end.duration_since(started).as_secs_f64().max(0.000001);
        let (cpu_after, end_rss) = process_metrics(&mut system)?;
        rss = rss.max(end_rss);
        let (mut service, mut e2e, mut failed) = (
            Histogram::default(),
            Histogram::default(),
            Histogram::default(),
        );
        let (mut success, mut errors, mut dropped, mut affected) = (0i64, 0i64, 0i64, 0i64);
        let mut examples = HashMap::<String, i64>::new();
        for c in counts {
            service.merge(&c.service);
            e2e.merge(&c.end);
            failed.merge(&c.failed);
            success += c.success;
            errors += c.errors;
            dropped += c.dropped;
            affected += c.rows;
            for (k, v) in c.examples {
                *examples.entry(k).or_default() += v;
            }
        }
        let mut pairs = examples.into_iter().collect::<Vec<_>>();
        pairs.sort_by(|a, b| b.1.cmp(&a.1));
        let limited = pairs.into_iter().take(8).collect::<HashMap<_, _>>();
        let mut result = base(run, operation);
        let object = result.as_object_mut().unwrap();
        let fields = json!({"stage":stamp,"clients":clients,"unit":if operation=="BatchInsert"{"rows/s"}else{"ops/s"},"elapsedSeconds":elapsed,"successfulOperations":success,"successfulRows":affected,"errors":errors,"dropped":dropped,"plannedOperations":if rate.is_some()&&!CANCELLED.load(Ordering::Relaxed){planned}else{success+errors+dropped},"operationsPerSecond":success as f64/elapsed,"rowsPerSecond":if ["Insert","BatchInsert","Update","Delete"].contains(&operation){Some(affected as f64/elapsed)}else{None},"serviceLatency":service.snapshot(),"endToEndLatency":e2e.snapshot(),"errorLatency":failed.snapshot(),"client":{"cpuPercent":cpu_after.saturating_sub(cpu_before)as f64/1000./elapsed*100.,"rssEndMb":end_rss,"rssSampledMaxMb":rss,"allocatedBytes":null,"gen0Collections":null,"gen1Collections":null,"gen2Collections":null,"gcPauseMs":null},"errorExamples":limited,"cancelled":CANCELLED.load(Ordering::Relaxed)});
        object.extend(fields.as_object().unwrap().clone());
        Ok(result)
    })
}
fn sessions(run: &Arc<Run>, count: usize) -> Result<Vec<Database>> {
    let mut result = Vec::with_capacity(count);
    for _ in 0..count {
        check()?;
        result.push(Database::open(run.clone())?);
    }
    Ok(result)
}
fn benchmark(run: Arc<Run>) -> Result<()> {
    let until = Instant::now() + Duration::from_secs(90);
    let mut database = loop {
        check()?;
        match Database::open(run.clone()) {
            Ok(db) => break db,
            Err(error) => {
                if run.target == "sqlite" || Instant::now() >= until {
                    return Err(error);
                };
                thread::sleep(Duration::from_millis(500));
            }
        }
    };
    let mut metadata = database.metadata()?;
    metadata["settings"]["effective_batch_size"] = json!(batch_size(&run.config).to_string());
    emit(Some(&run), "metadata", metadata)?;
    database.reset()?;
    emit(
        Some(&run),
        "phase_start",
        json!({"phase":"bulk_load","targetSeconds":0}),
    )?;
    let started = Instant::now();
    let mut generation = 0.;
    let mut amount = 0i64;
    let mut report_at = Instant::now() + Duration::from_secs(1);
    let size = batch_size(&run.config);
    let mut first = 1;
    while first <= run.rows {
        check()?;
        let before = Instant::now();
        let rows = (0..(size as i64).min(run.rows - first + 1))
            .map(|i| row(first + i, run.config.seed, run.config.payload_bytes))
            .collect::<Vec<_>>();
        generation += before.elapsed().as_secs_f64();
        database.batch(&rows)?;
        amount += rows.iter().map(|r| r.amount_cents).sum::<i64>();
        if Instant::now() >= report_at {
            emit(
                Some(&run),
                "progress",
                json!({"phase":"bulk_load","loaded":first+rows.len()as i64-1,"total":run.rows}),
            )?;
            report_at = Instant::now() + Duration::from_secs(1);
        }
        first += size as i64;
    }
    let elapsed = started.elapsed().as_secs_f64().max(0.000001);
    check()?;
    let actual = database.digest()?;
    if actual != [run.rows, run.rows * (run.rows + 1) / 2, amount] {
        bail!("Dataset digest mismatch")
    }
    let mut result = base(&run, "bulk_load");
    result.as_object_mut().unwrap().extend(json!({"unit":"rows/s","elapsedSeconds":elapsed,"successfulRows":run.rows,"successfulOperations":(run.rows+size as i64-1)/size as i64,"rowsPerSecond":run.rows as f64/elapsed,"generationSeconds":generation,"verified":true}).as_object().unwrap().clone());
    emit(Some(&run), "result", result)?;
    if !["redis", "valkey", "clickHouse"].contains(&run.target.as_str()) {
        check()?;
        emit(
            Some(&run),
            "phase_start",
            json!({"phase":"index_build","targetSeconds":0}),
        )?;
        let before = Instant::now();
        database.prepare()?;
        let mut result = base(&run, "index_build");
        result.as_object_mut().unwrap().extend(json!({"unit":"seconds","elapsedSeconds":before.elapsed().as_secs_f64(),"successfulOperations":1,"verified":true}).as_object().unwrap().clone());
        emit(Some(&run), "result", result)?;
    }
    let supported = capabilities(&run.target);
    let requested = run
        .config
        .scenarios
        .iter()
        .map(|s| {
            let mut chars = s.chars();
            chars
                .next()
                .map(|c| c.to_uppercase().collect::<String>() + chars.as_str())
                .unwrap_or_default()
        })
        .collect::<Vec<_>>();
    for s in &requested {
        if !supported.contains(&s.as_str()) {
            emit(
                Some(&run),
                "unsupported",
                json!({"scenario":s,"message":"Not supported by this database profile"}),
            )?;
        }
    }
    let mut operations = SCENARIOS
        .iter()
        .copied()
        .filter(|s| {
            supported.contains(s)
                && (if requested.is_empty() {
                    run.config.plan.is_none() || *s == supported[0]
                } else {
                    requested.iter().any(|r| r == s)
                })
        })
        .collect::<Vec<_>>();
    operations.sort_by_key(|s| *s == "Delete");
    if run.config.warmup_seconds > 0. {
        let count = run
            .config
            .plan
            .as_ref()
            .map(|p| p.stages[0].clients)
            .unwrap_or(run.clients);
        let mut sessions = sessions(&run, count)?;
        let warmup = measure(
            &run,
            &mut sessions,
            &mut vec![0; count],
            &AtomicI64::new(0),
            supported[0],
            run.config.warmup_seconds,
            count,
            if run.config.plan.is_none() && run.config.load_model == "constantRate" {
                Some(run.config.target_ops_per_second)
            } else {
                None
            },
            None,
            false,
        )?;
        if warmup["errors"].as_i64().unwrap_or(1) > 0 {
            bail!("Warmup errors")
        }
    }
    let mut expected = run.rows;
    for operation in operations {
        check()?;
        let mut pool = Vec::new();
        let mut sequences = vec![0i64; run.clients];
        let deleted = AtomicI64::new(0);
        let stages = run
            .config
            .plan
            .as_ref()
            .map(|p| p.stages.clone())
            .unwrap_or_else(|| {
                vec![Stage {
                    name: String::new(),
                    clients: run.clients,
                    seconds: run.config.duration_seconds,
                    operations_per_second: if run.config.load_model == "constantRate" {
                        Some(run.config.target_ops_per_second)
                    } else {
                        None
                    },
                }]
            });
        for (index, stage) in stages.iter().enumerate() {
            check()?;
            if pool.len() < stage.clients {
                pool.extend(sessions(&run, stage.clients - pool.len())?);
            }
            let stamp=run.config.plan.as_ref().map(|_|json!({"number":index+1,"name":stage.name,"targetSeconds":stage.seconds,"operationsPerSecond":stage.operations_per_second}));
            emit(
                Some(&run),
                "phase_start",
                json!({"phase":operation,"targetSeconds":stage.seconds,"stage":stamp,"clients":stage.clients}),
            )?;
            let mut result = measure(
                &run,
                &mut pool,
                &mut sequences,
                &deleted,
                operation,
                stage.seconds,
                stage.clients,
                stage.operations_per_second,
                stamp,
                true,
            )?;
            emit(Some(&run), "phase_end", json!({}))?;
            if operation == "Insert" || operation == "BatchInsert" {
                expected += result["successfulRows"].as_i64().unwrap_or(0)
            };
            if operation == "Delete" {
                expected -= result["successfulRows"].as_i64().unwrap_or(0)
            };
            let actual = database.count()?;
            let success = result["successfulOperations"].as_i64().unwrap_or(0);
            result["verified"] =
                json!(actual == expected && success > 0 && !CANCELLED.load(Ordering::Relaxed));
            result["cancelled"] = json!(CANCELLED.load(Ordering::Relaxed));
            emit(Some(&run), "result", result)?;
            if actual != expected {
                bail!("Count mismatch: {} != {}", actual, expected)
            };
            if run.config.plan.is_none() && success == 0 && !CANCELLED.load(Ordering::Relaxed) {
                bail!("No successful operations")
            }
        }
    }
    Ok(())
}
fn main() {
    if std::env::args().any(|arg| arg == "--self-test") {
        println!("{}", portable::vectors());
        return;
    }
    let _ = emit(
        None,
        "hello",
        json!({"engine":"rust","runtime":env!("BENCHFORGE_RUSTC"),"generator":GENERATOR,"histogram":"binary128-us-v1"}),
    );
    let mut run = None;
    let outcome = (|| -> Result<()> {
        let mut line = String::new();
        io::stdin().read_line(&mut line)?;
        if line.len() > 1_048_576 {
            bail!("Command too long")
        };
        let command: Command = serde_json::from_str(line.trim_start_matches('\u{feff}'))?;
        if command.protocol_version != 1 || command.kind != "run" {
            bail!("Expected protocol v1 run")
        };
        let value = command.run.context("Missing run")?;
        if value.rows < 100
            || value.clients < 1
            || value.config.payload_bytes < 16
            || batch_size(&value.config) < 1
            || !value.engine.is_empty() && value.engine != "rust"
        {
            bail!("Invalid run")
        };
        run = Some(Arc::new(value));
        thread::spawn(|| {
            for line in io::stdin().lock().lines() {
                match line
                    .ok()
                    .and_then(|line| serde_json::from_str::<Command>(&line).ok())
                {
                    Some(command) if command.protocol_version == 1 && command.kind == "cancel" => {
                        CANCELLED.store(true, Ordering::Relaxed);
                        return;
                    }
                    Some(_) => {}
                    None => {
                        CANCELLED.store(true, Ordering::Relaxed);
                        return;
                    }
                }
            }
            CANCELLED.store(true, Ordering::Relaxed);
        });
        benchmark(run.as_ref().unwrap().clone())
    })();
    let cancelled = CANCELLED.load(Ordering::Relaxed);
    let code = if outcome.is_ok() || cancelled {
        let _ = emit(run.as_deref(), "completed", json!({"cancelled":cancelled}));
        if cancelled {
            2
        } else {
            0
        }
    } else {
        let mut message = outcome.unwrap_err().to_string();
        if let Some(value) = &run {
            if !value.endpoint.password.is_empty() {
                message = message.replace(&value.endpoint.password, "[redacted]");
            }
        }
        let _ = emit(run.as_deref(), "failed", json!({"message":message}));
        1
    };
    std::process::exit(code)
}
