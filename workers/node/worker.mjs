import { Worker, isMainThread, parentPort, workerData } from 'node:worker_threads';
import { createInterface } from 'node:readline';
import { setTimeout as delay } from 'node:timers/promises';
import { GENERATOR, Random, Histogram, row, vectors } from './portable.mjs';
import { Database } from './adapters.mjs';

const scenarios = [
  'PointRead',
  'RangeRead',
  'Aggregate',
  'AnalyticalScan',
  'Update',
  'Mixed',
  'Insert',
  'BatchInsert',
  'Delete',
];
const caps = Object.fromEntries(
  ['sqlite', 'postgreSql', 'mySql', 'mariaDb', 'mongoDb'].map((t) => [
    t,
    scenarios.filter((s) => s !== 'AnalyticalScan'),
  ]),
);
caps.redis = caps.valkey = ['PointRead', 'Update', 'Mixed', 'Insert', 'BatchInsert', 'Delete'];
caps.clickHouse = ['RangeRead', 'Aggregate', 'AnalyticalScan', 'BatchInsert'];
const defaults = {
  durationSeconds: 5,
  warmupSeconds: 2,
  payloadBytes: 256,
  batchSize: 1000,
  seed: 42n,
  hotspot: false,
  loadModel: 'closedLoop',
  targetOpsPerSecond: 1000,
  maxStartLagMs: 20,
  scenarios: [],
};
const clock = () => Number(process.hrtime.bigint()) / 1e9;
const cancelled = (flag) => Atomics.load(flag, 0) !== 0;
const batchSize = (c) =>
  Math.min(c.batchSize, Math.max(1, Math.floor((8 * 1024 * 1024) / (c.payloadBytes + 128))));
const base = (run, phase) => ({
  engine: 'node',
  target: run.target,
  datasetRows: run.rows,
  clients: run.clients,
  repeat: run.repeat,
  phase,
});
const emit = (run, type, data) =>
  process.stdout.write(
    JSON.stringify({ protocolVersion: 1, type, runId: run?.runId ?? '', data }) + '\n',
  );

if (!isMainThread) {
  const { run, index, shared, deletedShared } = workerData,
    flag = new Int32Array(shared),
    deleted = new BigInt64Array(deletedShared),
    config = { ...defaults, ...run.config };
  const db = await new Database(run).open();
  let sequence = 0;
  parentPort.postMessage({ type: 'ready' });
  parentPort.on('message', async (command) => {
    if (command.type === 'close') {
      await db.close();
      parentPort.close();
      return;
    }
    if (command.type !== 'measure') return;
    const { operation, seconds, clients, rate, stage, start } = command,
      deadline = start + seconds,
      planned = Math.floor(seconds * (rate || 0));
    const c = {
      success: 0,
      rows: 0,
      errors: 0,
      dropped: 0,
      service: new Histogram(),
      end: new Histogram(),
      failed: new Histogram(),
      examples: {},
    };
    const rng = new Random(
      BigInt(config.seed) ^
        (BigInt(index + 1) * 104729n) ^
        BigInt(run.repeat) ^
        (BigInt(stage?.number ?? 0) * 32452843n),
    );
    let slot = index,
      reportAt = start + 0.25;
    try {
      while (clock() < start && !cancelled(flag)) await delay(1);
      while (!cancelled(flag) && clock() < deadline) {
        let scheduled = clock();
        if (rate) {
          if (slot >= planned) break;
          scheduled = start + slot / rate;
          const lag = clock() - scheduled;
          if (lag > config.maxStartLagMs / 1000) {
            const skipped = Math.min(
              Math.max(1, Math.floor(((lag - config.maxStartLagMs / 1000) * rate) / clients) + 1),
              Math.floor((planned - 1 - slot) / clients) + 1,
            );
            c.dropped += skipped;
            slot += skipped * clients;
            continue;
          }
          while (clock() < scheduled && !cancelled(flag)) await delay(1);
          if (cancelled(flag) || clock() >= deadline) break;
          if (clock() - scheduled > config.maxStartLagMs / 1000) {
            c.dropped++;
            slot += clients;
            continue;
          }
        }
        const selected =
          operation === 'Mixed' ? (rng.next() % 100n < 80n ? 'PointRead' : 'Update') : operation;
        let key = rng.key(run.rows, config.hotspot),
          one = null,
          batch = null;
        if (selected === 'Insert' || selected === 'BatchInsert') {
          const size = selected === 'BatchInsert' ? batchSize(config) : 1,
            first =
              (scenarios.indexOf(operation) + 1) * 1e12 +
              (sequence * run.clients + index) * size +
              1;
          if (!Number.isSafeInteger(first + size))
            throw Error('Generated ID exceeds JavaScript safe integer range');
          if (selected === 'Insert') one = row(first, config.seed, config.payloadBytes);
          else
            batch = Array.from({ length: size }, (_, i) =>
              row(first + i, config.seed, config.payloadBytes),
            );
        }
        if (selected === 'Delete') {
          key = stage ? Number(Atomics.add(deleted, 0, 1n)) + 1 : sequence * clients + index + 1;
          if (key > run.rows) break;
        }
        const before = clock();
        try {
          const affected = await db.execute(selected, key, one, batch),
            finish = clock();
          c.success++;
          c.rows += affected;
          c.service.add(Math.max(1, (finish - before) * 1e6));
          c.end.add(Math.max(1, (finish - (rate ? scheduled : before)) * 1e6));
        } catch (e) {
          c.errors++;
          c.failed.add(Math.max(1, (clock() - before) * 1e6));
          const message = (e.name + ': ' + e.message)
            .split(run.endpoint.password || '\0')
            .join('[redacted]')
            .slice(0, 250);
          c.examples[message] = (c.examples[message] || 0) + 1;
        }
        sequence++;
        slot += clients;
        if (c.errors >= 50) break;
        if (clock() >= reportAt) {
          parentPort.postMessage({ type: 'progress', success: c.success, errors: c.errors });
          reportAt = clock() + 0.25;
        }
      }
      if (rate && slot < planned && !cancelled(flag))
        c.dropped += Math.floor((planned - 1 - slot) / clients) + 1;
      parentPort.postMessage({
        type: 'result',
        ...c,
        service: c.service.snapshot(),
        end: c.end.snapshot(),
        failed: c.failed.snapshot(),
        finished: clock(),
      });
    } catch (e) {
      parentPort.postMessage({ type: 'failed', message: e.message });
    }
  });
} else if (process.argv.includes('--self-test')) {
  console.log(JSON.stringify(vectors()));
} else {
  let run,
    active = false;
  const flag = new Int32Array(new SharedArrayBuffer(4));
  emit(null, 'hello', {
    engine: 'node',
    runtime: 'Node.js ' + process.version,
    generator: GENERATOR,
    histogram: 'binary128-us-v1',
    targets: Object.keys(caps),
  });
  const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
  input.on('close', () => Atomics.store(flag, 0, 1));
  input.on('line', (line) => {
    try {
      if (line.length > 1048576) throw Error('Command too long');
      const command = JSON.parse(line.replace(/^\uFEFF/, ''), (key, value, context) =>
        key === 'seed' ? BigInt(context.source) : value,
      );
      if (command.protocolVersion !== 1) throw Error('Expected protocol v1');
      if (command.type === 'cancel') {
        Atomics.store(flag, 0, 1);
        return;
      }
      if (active || command.type !== 'run') throw Error('Expected one run command');
      active = true;
      run = command.run;
      if (
        (run.engine ?? 'node') !== 'node' ||
        !caps[run.target] ||
        run.rows < 100 ||
        run.clients < 1
      )
        throw Error('Invalid run');
      benchmark(run, flag).then(
        () => {
          emit(run, 'completed', { cancelled: cancelled(flag) });
          process.exit(cancelled(flag) ? 2 : 0);
        },
        (e) => {
          if (e.message === 'Cancelled') {
            emit(run, 'completed', { cancelled: true });
            process.exit(2);
          }
          emit(run, 'failed', {
            message: String(e.message)
              .split(run.endpoint.password || '\0')
              .join('[redacted]'),
          });
          process.exit(1);
        },
      );
    } catch (e) {
      emit(run, 'failed', { message: e.message });
      process.exit(1);
    }
  });
}

async function benchmark(run, flag) {
  const config = { ...defaults, ...run.config };
  const check = () => {
    if (cancelled(flag)) throw Error('Cancelled');
  };
  let db;
  const until = clock() + 90;
  while (true) {
    check();
    try {
      db = await new Database(run).open();
      break;
    } catch (e) {
      if (run.target === 'sqlite' || clock() >= until) throw e;
      await delay(500);
    }
  }
  try {
    const metadata = await db.metadata();
    metadata.settings.effective_batch_size = String(batchSize(config));
    emit(run, 'metadata', metadata);
    await db.reset();
    emit(run, 'phase_start', { phase: 'bulk_load', targetSeconds: 0 });
    const start = clock();
    let generation = 0,
      amount = 0,
      reportAt = start + 1;
    for (let first = 1; first <= run.rows; first += batchSize(config)) {
      check();
      const before = clock(),
        batch = Array.from({ length: Math.min(batchSize(config), run.rows - first + 1) }, (_, i) =>
          row(first + i, config.seed, config.payloadBytes),
        );
      generation += clock() - before;
      await db.batch(batch);
      amount += batch.reduce((sum, r) => sum + r.amountCents, 0);
      if (clock() >= reportAt) {
        emit(run, 'progress', {
          phase: 'bulk_load',
          loaded: Math.min(first + batch.length - 1, run.rows),
          total: run.rows,
        });
        reportAt = clock() + 1;
      }
    }
    const elapsed = Math.max(1e-6, clock() - start);
    check();
    const digest = await db.digest();
    if (
      digest[0] !== run.rows ||
      digest[1] !== (run.rows * (run.rows + 1)) / 2 ||
      digest[2] !== amount
    )
      throw Error('Dataset digest mismatch');
    emit(run, 'result', {
      ...base(run, 'bulk_load'),
      unit: 'rows/s',
      elapsedSeconds: elapsed,
      successfulRows: run.rows,
      successfulOperations: Math.ceil(run.rows / batchSize(config)),
      rowsPerSecond: run.rows / elapsed,
      generationSeconds: generation,
      verified: true,
    });
    if (!['redis', 'valkey', 'clickHouse'].includes(run.target)) {
      check();
      emit(run, 'phase_start', { phase: 'index_build', targetSeconds: 0 });
      const before = clock();
      await db.prepare();
      emit(run, 'result', {
        ...base(run, 'index_build'),
        unit: 'seconds',
        elapsedSeconds: clock() - before,
        successfulOperations: 1,
        verified: true,
      });
    }
    const requested = config.scenarios.map((s) => s[0].toUpperCase() + s.slice(1));
    for (const name of requested)
      if (!caps[run.target].includes(name))
        emit(run, 'unsupported', {
          scenario: name,
          message: 'Not supported by this database profile',
        });
    const operations = scenarios.filter(
      (s) =>
        caps[run.target].includes(s) &&
        (requested.length
          ? requested.includes(s)
          : !config.plan ||
            s === (caps[run.target].includes('PointRead') ? 'PointRead' : 'RangeRead')),
    );
    let expected = run.rows;
    async function pool(count) {
      const clients = [],
        deletedShared = new SharedArrayBuffer(8);
      try {
        for (let index = 0; index < count; index++) {
          check();
          const worker = new Worker(new URL(import.meta.url), {
            workerData: { run, index, shared: flag.buffer, deletedShared },
            stderr: true,
          });
          worker.stderr.on('data', (chunk) => process.stderr.write(chunk));
          const client = { worker, index, success: 0, errors: 0 };
          clients.push(client);
          await new Promise((resolve, reject) => {
            const timer = setTimeout(() => reject(Error('Client startup timeout')), 30000);
            worker.once('error', (e) => {
              clearTimeout(timer);
              reject(e);
            });
            worker.once('message', (message) => {
              clearTimeout(timer);
              message.type === 'ready' ? resolve() : reject(Error('Client not ready'));
            });
          });
        }
        return clients;
      } catch (e) {
        await Promise.all(clients.map((c) => c.worker.terminate()));
        throw e;
      }
    }
    async function close(clients) {
      await Promise.all(
        clients.map(async (c) => {
          c.worker.postMessage({ type: 'close' });
          await new Promise((resolve) => {
            c.worker.once('exit', resolve);
            setTimeout(resolve, 2000).unref();
          });
          await c.worker.terminate();
        }),
      );
    }
    async function measure(clients, operation, seconds, count, rate, stage, progress) {
      const selected = clients.slice(0, count),
        started = clock() + 0.01,
        cpu = process.cpuUsage();
      let rss = process.memoryUsage.rss() / 1048576;
      const tasks = selected.map(
        (c) =>
          new Promise((resolve, reject) => {
            c.success = c.errors = 0;
            const listener = (m) => {
              if (m.type === 'progress') {
                c.success = m.success;
                c.errors = m.errors;
              } else if (m.type === 'result' || m.type === 'failed') {
                c.worker.off('message', listener);
                m.type === 'result' ? resolve(m) : reject(Error(m.message));
              }
            };
            c.worker.on('message', listener);
            c.worker.once('error', reject);
            c.worker.postMessage({
              type: 'measure',
              operation,
              seconds,
              clients: count,
              rate,
              stage,
              start: started,
            });
          }),
      );
      const sampler = setInterval(() => {
        rss = Math.max(rss, process.memoryUsage.rss() / 1048576);
      }, 250);
      const reporter = progress
        ? setInterval(
            () =>
              emit(run, 'progress', {
                phase: operation,
                stage: stage?.name,
                clients: count,
                targetSeconds: seconds,
                successful: selected.reduce((n, c) => n + c.success, 0),
                errors: selected.reduce((n, c) => n + c.errors, 0),
                elapsedSeconds: clock() - started,
              }),
            1000,
          )
        : null;
      let values;
      try {
        values = await Promise.all(tasks);
      } finally {
        clearInterval(sampler);
        if (reporter) clearInterval(reporter);
      }
      const elapsed = Math.max(1e-6, Math.max(...values.map((v) => v.finished)) - started),
        used = process.cpuUsage(cpu),
        service = new Histogram(),
        end = new Histogram(),
        failed = new Histogram(),
        examples = {};
      for (const value of values) {
        service.merge(value.service);
        end.merge(value.end);
        failed.merge(value.failed);
        for (const [k, v] of Object.entries(value.examples)) examples[k] = (examples[k] || 0) + v;
      }
      const sum = (key) => values.reduce((n, c) => n + c[key], 0),
        success = sum('success'),
        errors = sum('errors'),
        dropped = sum('dropped'),
        affected = sum('rows');
      return {
        ...base(run, operation),
        stage,
        clients: count,
        unit: operation === 'BatchInsert' ? 'rows/s' : 'ops/s',
        elapsedSeconds: elapsed,
        successfulOperations: success,
        successfulRows: affected,
        errors,
        dropped,
        plannedOperations:
          rate && !cancelled(flag) ? Math.floor(seconds * rate) : success + errors + dropped,
        operationsPerSecond: success / elapsed,
        rowsPerSecond: ['Insert', 'BatchInsert', 'Update', 'Delete'].includes(operation)
          ? affected / elapsed
          : null,
        serviceLatency: service.snapshot(),
        endToEndLatency: end.snapshot(),
        errorLatency: failed.snapshot(),
        client: {
          cpuPercent: ((used.user + used.system) / 1e6 / elapsed) * 100,
          rssEndMb: process.memoryUsage.rss() / 1048576,
          rssSampledMaxMb: Math.max(rss, process.memoryUsage.rss() / 1048576),
          allocatedBytes: null,
          gen0Collections: null,
          gen1Collections: null,
          gen2Collections: null,
          gcPauseMs: null,
        },
        errorExamples: Object.fromEntries(
          Object.entries(examples)
            .sort((a, b) => b[1] - a[1])
            .slice(0, 8),
        ),
        cancelled: cancelled(flag),
      };
    }
    if (config.warmupSeconds > 0) {
      const count = config.plan?.stages[0].clients ?? run.clients,
        clients = await pool(count);
      try {
        const warmup = await measure(
          clients,
          caps[run.target].includes('PointRead') ? 'PointRead' : 'RangeRead',
          config.warmupSeconds,
          count,
          !config.plan && config.loadModel === 'constantRate' ? config.targetOpsPerSecond : null,
          null,
          false,
        );
        if (warmup.errors) throw Error('Warmup errors');
      } finally {
        await close(clients);
      }
    }
    for (const operation of operations) {
      check();
      const clients = await pool(run.clients);
      try {
        const stages = config.plan?.stages ?? [
          {
            name: '',
            clients: run.clients,
            seconds: config.durationSeconds,
            operationsPerSecond:
              config.loadModel === 'constantRate' ? config.targetOpsPerSecond : null,
          },
        ];
        for (let index = 0; index < stages.length; index++) {
          check();
          const s = stages[index],
            stamp = config.plan
              ? {
                  number: index + 1,
                  name: s.name,
                  targetSeconds: s.seconds,
                  operationsPerSecond: s.operationsPerSecond ?? null,
                }
              : null;
          emit(run, 'phase_start', {
            phase: operation,
            targetSeconds: s.seconds,
            stage: stamp,
            clients: s.clients,
          });
          const result = await measure(
            clients,
            operation,
            s.seconds,
            s.clients,
            s.operationsPerSecond ?? null,
            stamp,
            true,
          );
          emit(run, 'phase_end', {});
          if (operation === 'Insert' || operation === 'BatchInsert')
            expected += result.successfulRows;
          if (operation === 'Delete') expected -= result.successfulRows;
          const actual = await db.count();
          result.verified =
            actual === expected && result.successfulOperations > 0 && !cancelled(flag);
          result.cancelled = cancelled(flag);
          emit(run, 'result', result);
          if (actual !== expected) throw Error(`Count mismatch: ${actual} != ${expected}`);
          if (!config.plan && !result.successfulOperations && !cancelled(flag))
            throw Error('No successful operations');
        }
      } finally {
        await close(clients);
      }
    }
  } finally {
    await db.close();
  }
}
