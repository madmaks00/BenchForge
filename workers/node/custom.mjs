import { createReadStream } from 'node:fs';
import { createInterface } from 'node:readline';
import { createHash } from 'node:crypto';
import { Random, Histogram } from './portable.mjs';

const alphabet =
  '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz';
const multiplier = 0xd6e8feb86659fd93n;
const clock = () => Number(process.hrtime.bigint()) / 1e9;
const unsigned = (value) => BigInt.asUintN(64, value);
const sampleKey = (field) =>
  [field.reference, field.sampleColumn ?? 'id', field.sampleSize ?? 10000].join(
    '/',
  );

function stableSeed(seed, name, row = 0n) {
  let hash = 14695981039346656037n;
  for (const byte of Buffer.from(name))
    hash = unsigned((hash ^ BigInt(byte)) * 1099511628211n);
  return BigInt(seed) ^ hash ^ unsigned(row * multiplier);
}

function decimalParts(value) {
  const parts = /^([+-]?)(\d+)(?:\.(\d+))?(?:e([+-]?\d+))?$/i.exec(
    String(value),
  );
  if (!parts) throw Error('Invalid decimal value: ' + value);
  const decimals = parts[3] ?? '';
  return [
    BigInt(parts[1] + parts[2] + decimals),
    decimals.length - Number(parts[4] ?? 0),
  ];
}

function exactDecimal(field, rng) {
  const [lo, ls] = decimalParts(field.min ?? 1),
    [hi, hs] = decimalParts(field.max ?? 1000);
  const scale = field.scale ?? 2,
    common = Math.max(ls, hs, scale);
  const a = lo * 10n ** BigInt(common - ls),
    b = hi * 10n ** BigInt(common - hs);
  const division = 9007199254740992n;
  const numerator = a * division + (b - a) * (rng.next() >> 11n);
  const denominator = division * 10n ** BigInt(common - scale);
  let rounded = numerator / denominator;
  const remainder =
    numerator < 0n ? -(numerator % denominator) : numerator % denominator;
  if (
    remainder * 2n > denominator ||
    (remainder * 2n === denominator && rounded % 2n !== 0n)
  )
    rounded += numerator < 0n ? -1n : 1n;
  if (!scale) return String(rounded);
  const sign = rounded < 0n ? '-' : '';
  const digits = String(rounded < 0n ? -rounded : rounded).padStart(
    scale + 1,
    '0',
  );
  return sign + digits.slice(0, -scale) + '.' + digits.slice(-scale);
}

function guidText(bytes, oracle) {
  if (oracle) return bytes.toString('hex');
  return [
    bytes.readUInt32LE().toString(16).padStart(8, '0'),
    bytes.readUInt16LE(4).toString(16).padStart(4, '0'),
    bytes.readUInt16LE(6).toString(16).padStart(4, '0'),
    bytes.subarray(8, 10).toString('hex'),
    bytes.subarray(10).toString('hex'),
  ].join('-');
}

function format(value, type, target) {
  if (value == null) return null;
  if (type === 'boolean') {
    const normalized = String(value).toLowerCase();
    if (normalized === '1' || normalized === 'true') return '1';
    if (normalized === '0' || normalized === 'false') return '0';
    throw Error('Invalid boolean');
  }
  if (type === 'int32' || type === 'int64') {
    const number = BigInt(value);
    if (number !== BigInt.asIntN(type === 'int32' ? 32 : 64, number))
      throw Error('Integer value out of range');
    return String(number);
  }
  if (type === 'double') {
    const number = Number(value);
    if (!Number.isFinite(number)) throw Error('Invalid double');
    return String(number);
  }
  if (type === 'dateTime') {
    if (
      typeof value === 'string' &&
      /^\d{4}-\d\d-\d\d[ T]\d\d:\d\d:\d\d\.\d{6}$/.test(value)
    )
      return value.replace('T', ' ');
    const date =
      value instanceof Date
        ? value
        : new Date(
            String(value).replace(' ', 'T') +
              (String(value).match(/Z$|[+-]\d\d:\d\d$/i) ? '' : 'Z'),
          );
    return date.toISOString().replace('T', ' ').replace('Z', '000');
  }
  if (type === 'guid') {
    if (Buffer.isBuffer(value)) return guidText(value, target === 'oracle');
    const text = String(value).toLowerCase();
    if (target !== 'oracle' || /^[\da-f]{32}$/.test(text)) return text;
    const [a, b, c] = text.split('-');
    const bytes = Buffer.from(text.replaceAll('-', ''), 'hex');
    bytes.writeUInt32LE(parseInt(a, 16));
    bytes.writeUInt16LE(parseInt(b, 16), 4);
    bytes.writeUInt16LE(parseInt(c, 16), 6);
    return bytes.toString('hex');
  }
  return String(value);
}

function generate(field, context, rng, state) {
  if (
    field.nullPercent > 0 &&
    Number(rng.next() % 1000000n) < field.nullPercent * 10000
  )
    return null;
  const min = () => BigInt(field.min ?? 1),
    max = () => BigInt(field.max ?? 1000);
  const integer = () => min() + (rng.next() % (max() - min() + 1n));
  let value;
  switch (field.generator ?? 'sequence') {
    case 'sequence':
      value = min() + context.sequence - 1n;
      break;
    case 'integer':
      value = integer();
      break;
    case 'decimal':
      value = exactDecimal(field, rng);
      break;
    case 'text': {
      value = (field.prefix ?? '').replace(
        /\{(row|sequence|key|client)\}/g,
        (_, field) => String(context[field]),
      );
      for (let i = 0; i < (field.length ?? 32); i++)
        value += alphabet[Number(rng.next() % 62n)];
      break;
    }
    case 'choice':
      value = field.choices[Number(rng.next() % BigInt(field.choices.length))];
      break;
    case 'reference': {
      const count = state.rows[field.reference.toLowerCase()];
      if (!count) throw Error('Empty reference table ' + field.reference);
      value = (rng.next() % BigInt(count)) + 1n;
      break;
    }
    case 'guid': {
      const bytes = Buffer.alloc(16);
      bytes.writeBigUInt64LE(rng.next(), 0);
      bytes.writeBigUInt64LE(rng.next(), 8);
      value = guidText(bytes, state.run.target === 'oracle');
      break;
    }
    case 'timestamp':
      value = new Date(Number(integer()) * 1000);
      break;
    case 'boolean':
      value = rng.next() % 2n === 0n;
      break;
    case 'constant':
      value = field.constant;
      break;
    case 'key':
      value = context.key;
      break;
    case 'client':
      value = context.client;
      break;
    case 'sample': {
      const items = state.samples[sampleKey(field)];
      if (!items?.length) throw Error('No non-null sample values');
      value = items[Number(rng.next() % BigInt(items.length))];
      break;
    }
    default:
      throw Error('Unknown generator ' + field.generator);
  }
  return format(value, field.type ?? 'int64', state.run.target);
}

class Sql {
  constructor(database, run) {
    this.database = database;
    this.raw = database.db;
    this.target = run.target;
    this.settings = run.config.customWorkload;
    this.commandMs = (this.settings.commandTimeoutSeconds ?? 10) * 1000;
    if (this.target === 'oracle') this.raw.callTimeout = this.commandMs;
    if (this.target === 'sqlite') this.raw.exec('PRAGMA foreign_keys=ON');
    this.transaction = undefined;
  }
  async configure() {
    if (this.target === 'oracle')
      await this.execute("ALTER SESSION SET NLS_NUMERIC_CHARACTERS = '.,'");
  }
  async execute(sql, params = [], mode = 'nonQuery', setup = false) {
    const target = this.target,
      read = mode !== 'nonQuery',
      timeout = setup ? 600000 : this.commandMs;
    let rows, changed;
    if (
      target === 'postgresql' ||
      target === 'postgreSql' ||
      target === 'cockroachDb'
    ) {
      const name =
        this.settings.prepareCommands && params.length
          ? createHash('sha256').update(sql).digest('hex').slice(0, 35)
          : undefined;
      const result = await this.raw.query({
        name,
        text: sql,
        values: params,
        rowMode: 'array',
        query_timeout: timeout,
      });
      rows = result.rows;
      changed = result.rowCount;
    } else if (target === 'duckDb') {
      ({ rows, changed } = this.raw.run(
        sql,
        params,
        this.settings.prepareCommands,
      ));
    } else if (target === 'sqlServer') {
      const driver = this.database.driver;
      const req = new driver.Request(this.transaction ?? this.raw);
      params.forEach((value, index) =>
        req.input('p' + (index + 1), driver.NVarChar(driver.MAX), value),
      );
      const result = await req.query(sql);
      rows = result.recordset?.map((row) => Object.values(row));
      changed = result.rowsAffected.reduce((a, b) => a + b, 0);
    } else if (target === 'oracle') {
      const driver = this.database.driver;
      const binds = params.map((value) => ({
        val: value,
        type: value?.length > 2000 ? driver.CLOB : driver.STRING,
      }));
      const result = await this.raw.execute(sql, binds, {
        autoCommit: !this.transaction,
        keepInStmtCache: this.settings.prepareCommands,
        outFormat: driver.OUT_FORMAT_ARRAY,
      });
      rows = result.rows;
      changed = result.rowsAffected;
    } else if (target === 'sqlite') {
      if (setup && !params.length) this.raw.exec(sql);
      else {
        const statement = this.settings.prepareCommands
          ? (this.database.statements[sql] ??= this.raw.prepare(sql))
          : this.raw.prepare(sql);
        statement.setReadBigInts(true);
        if (read)
          rows = statement.all(...params).map((row) => Object.values(row));
        else changed = Number(statement.run(...params).changes);
      }
    } else {
      const method =
        this.settings.prepareCommands && params.length ? 'execute' : 'query';
      const [result] = await this.raw[method](
        { sql, rowsAsArray: true, timeout },
        params,
      );
      if (read) rows = result;
      else changed = result.affectedRows;
    }
    if (mode === 'scalar')
      return {
        scalar: rows?.[0]?.[0] ?? null,
        count: rows?.[0]?.[0] == null ? 0 : 1,
      };
    return { count: read ? (rows?.length ?? 0) : Math.max(0, changed ?? 0) };
  }
  async begin(isolation = 'default') {
    const levels = {
      readCommitted: 'READ COMMITTED',
      repeatableRead: 'REPEATABLE READ',
      serializable: 'SERIALIZABLE',
    };
    if (this.target === 'sqlServer') {
      const driver = this.database.driver;
      const tx = new driver.Transaction(this.raw);
      await tx.begin(
        isolation === 'default'
          ? undefined
          : driver.ISOLATION_LEVEL[levels[isolation].replaceAll(' ', '_')],
      );
      this.transaction = tx;
    } else if (this.target === 'oracle') {
      this.transaction = true;
      if (isolation !== 'default')
        await this.execute(
          'SET TRANSACTION ISOLATION LEVEL ' + levels[isolation],
        );
    } else if (this.target === 'sqlite' || this.target === 'duckDb') {
      if (isolation !== 'default' && isolation !== 'serializable')
        throw Error('Unsupported embedded isolation ' + isolation);
      await this.execute('BEGIN TRANSACTION');
    } else if (this.target === 'mySql' || this.target === 'mariaDb') {
      if (isolation !== 'default')
        await this.execute(
          'SET TRANSACTION ISOLATION LEVEL ' + levels[isolation],
        );
      await this.execute('START TRANSACTION');
    } else
      await this.execute(
        isolation === 'default'
          ? 'BEGIN'
          : 'BEGIN ISOLATION LEVEL ' + levels[isolation],
      );
  }
  async finish(commit) {
    const method = commit ? 'commit' : 'rollback';
    if (this.target === 'sqlServer') await this.transaction[method]();
    else if (this.target === 'oracle') await this.raw[method]();
    else await this.execute(method.toUpperCase());
    this.transaction = undefined;
  }
}

function builtin(state, name, ctx) {
  const key = name.toLowerCase();
  const builtins = {
    key: ctx.key,
    sequence: BigInt(state.run.rows) + ctx.sequence,
    client: ctx.client,
    baserows: state.run.rows,
    seed: BigInt.asIntN(64, BigInt(state.run.config.seed)),
    completed: state.completed,
  };
  if (Object.hasOwn(builtins, key)) return String(builtins[key]);
  if (key.startsWith('rows_') && Object.hasOwn(state.rows, key.slice(5)))
    return String(state.rows[key.slice(5)]);
  if (key.startsWith('completed_'))
    return String(state.byOperation[key.slice(10)] ?? 0);
  throw Error('Unknown parameter @' + name);
}

function newDetail(id, name) {
  return {
    id,
    name,
    successfulOperations: 0,
    successfulRows: 0,
    errors: 0,
    service: new Histogram(),
    end: new Histogram(),
  };
}

export function detailResults(details) {
  return Array.from(details.values(), ({ service, end, ...detail }) => ({
    ...detail,
    serviceLatency: service.snapshot(),
    endToEndLatency: end.snapshot(),
  }));
}

export function mergeDetails(values) {
  const merged = new Map();
  for (const value of values)
    for (const entry of value.details ?? []) {
      if (!merged.has(entry.id))
        merged.set(entry.id, newDetail(entry.id, entry.name));
      const result = merged.get(entry.id);
      for (const key of ['successfulOperations', 'successfulRows', 'errors'])
        result[key] += entry[key];
      result.service.merge(entry.serviceLatency);
      result.end.merge(entry.endToEndLatency);
    }
  return detailResults(merged);
}

export class CustomSession {
  constructor(database, run, scope, index) {
    this.io = new Sql(database, run);
    this.state = { ...scope, run };
    this.sequence = new BigInt64Array(scope.sequence);
    this.client = index + 1;
    this.details = new Map();
    this.random = new Random(
      stableSeed(
        BigInt(run.config.seed) ^ BigInt(run.repeat),
        'client',
        BigInt(this.client),
      ),
    );
    this.ops = run.sqlProgram.operations.filter(
      ({ definition }) =>
        (!scope.operation || scope.operation === definition.id) &&
        (!scope.warm || definition.readOnly),
    );
    this.weight = this.ops.reduce(
      (sum, { definition }) => sum + BigInt(definition.weight),
      0n,
    );
  }
  async execute(key, scheduled) {
    const before = clock();
    let op = this.ops[0];
    if (!this.state.operation) {
      let choice = this.random.next() % this.weight;
      for (const candidate of this.ops) {
        choice -= BigInt(candidate.definition.weight);
        if (choice < 0n) {
          op = candidate;
          break;
        }
      }
    }
    const { definition } = op;
    if (!this.details.has(definition.id))
      this.details.set(
        definition.id,
        newDetail(definition.id, definition.name),
      );
    const detail = this.details.get(definition.id),
      sequence = Atomics.add(this.sequence, 0, 1n) + 1n;
    const ctx = { row: sequence, sequence, key, client: this.client };
    let began = false;
    try {
      const values = new Map(
        definition.parameters.map((field) => [
          field.name.toLowerCase(),
          generate(field, ctx, this.random, this.state),
        ]),
      );
      if (definition.transaction) {
        await this.io.begin(definition.isolation);
        began = true;
      }
      let affected = 0;
      for (const [index, step] of definition.steps.entries()) {
        const command = op.commands[index];
        const args = command.names.map((name) =>
          values.has(name.toLowerCase())
            ? values.get(name.toLowerCase())
            : builtin(this.state, name, ctx),
        );
        const { scalar, count } = await this.io.execute(
          command.sql,
          args,
          step.result,
        );
        if (
          step.expectedScalar != null &&
          String(scalar ?? '') !== step.expectedScalar &&
          !(
            scalar != null &&
            /^[-+]?\d/.test(step.expectedScalar) &&
            compareDecimals(scalar, step.expectedScalar) === 0
          )
        )
          throw Error('Unexpected scalar result');
        if (step.captureAs != null)
          values.set(
            step.captureAs.toLowerCase(),
            format(scalar, step.captureType, this.state.run.target),
          );
        if (
          (step.minRows != null && count < step.minRows) ||
          (step.maxRows != null && count > step.maxRows)
        )
          throw Error('Unexpected SQL row count');
        affected += count;
      }
      if (began) {
        await this.io.finish(true);
        began = false;
      }
      const end = clock();
      detail.successfulOperations++;
      detail.successfulRows += affected;
      detail.service.add(Math.max(1, (end - before) * 1e6));
      detail.end.add(Math.max(1, (end - scheduled) * 1e6));
      return affected;
    } catch (error) {
      detail.errors++;
      if (began) await this.io.finish(false).catch(() => {});
      throw error;
    }
  }
}

function compareDecimals(left, right) {
  try {
    const [a, as] = decimalParts(left),
      [b, bs] = decimalParts(right),
      scale = Math.max(as, bs);
    const diff = a * 10n ** BigInt(scale - as) - b * 10n ** BigInt(scale - bs);
    return diff === 0n ? 0 : diff > 0n ? 1 : -1;
  } catch {
    return NaN;
  }
}

export async function benchmarkCustom(run, database, config, hooks) {
  const p = run.sqlProgram,
    w = config.customWorkload,
    { check, emit, base, pool, close, measure, cancelled } = hooks;
  if (!p) throw Error('Missing compiled SQL plan. Update BenchForge.');
  const state = {
    run,
    rows: Object.fromEntries(
      p.datasets.map((table) => [
        table.definition.name.toLowerCase(),
        table.rows,
      ]),
    ),
    samples: {},
    sequence: new SharedArrayBuffer(8),
    completed: 0,
    byOperation: Object.create(null),
  };
  const sql = new Sql(database, run);
  await sql.configure();
  async function checks(before) {
    for (const { definition, command } of p.checks) {
      if (!definition[before ? 'before' : 'after']) continue;
      const expected = definition.expected;
      const value =
        expected === 'baseRows'
          ? run.rows
          : expected.startsWith('rows:')
            ? state.rows[expected.slice(5).toLowerCase()]
            : expected;
      const { scalar } = await sql.execute(
        command.sql,
        command.names.map((name) =>
          builtin(state, name, { sequence: 0n, key: 0, client: 0 }),
        ),
        'scalar',
      );
      const comparison = compareDecimals(scalar, value);
      const ok =
        definition.comparison === 'atLeast'
          ? comparison >= 0
          : definition.comparison === 'atMost'
            ? comparison <= 0
            : comparison === 0;
      if (!ok) throw Error('Integrity check failed: ' + definition.name);
    }
  }
  async function setup(phase, action, loading = false) {
    emit('phase_start', { phase, targetSeconds: 0 });
    const started = clock();
    try {
      const count = await action();
      const elapsed = Math.max(0.000001, clock() - started);
      emit('phase_end', {});
      emit('result', {
        ...base(phase),
        unit: loading ? 'rows/s' : 'seconds',
        elapsedSeconds: elapsed,
        successfulOperations: 1,
        successfulRows: count ?? 0,
        rowsPerSecond: loading ? count / elapsed : null,
        verified: true,
      });
    } catch (error) {
      emit('phase_end', {});
      if (!cancelled())
        emit('result', {
          ...base(phase),
          unit: 'seconds',
          errors: 1,
          verified: false,
          errorExamples: {
            [String(error.message)
              .split(run.endpoint.password || '\0')
              .join('[redacted]')
              .slice(0, 500)]: 1,
          },
        });
      throw error;
    }
  }
  async function schema() {
    state.samples = {};
    state.completed = 0;
    state.byOperation = Object.create(null);
    Atomics.store(new BigInt64Array(state.sequence), 0, 0n);
    if (run.target === 'sqlite')
      await sql.execute('PRAGMA foreign_keys=OFF', [], 'nonQuery', true);
    try {
      for (const statement of [...p.drop, ...p.schema]) {
        check();
        await sql.execute(statement, [], 'nonQuery', true);
      }
    } finally {
      if (run.target === 'sqlite')
        await sql.execute('PRAGMA foreign_keys=ON', [], 'nonQuery', true);
    }
  }
  async function load() {
    let loaded = 0;
    for (const dataset of p.datasets) {
      const { definition, insert } = dataset;
      const reservoirs = [];
      for (const op of p.operations)
        for (const field of op.definition.parameters) {
          if (
            field.generator !== 'sample' ||
            field.reference !== definition.name
          )
            continue;
          const key = sampleKey(field);
          if (Object.hasOwn(state.samples, key)) continue;
          const values = [];
          state.samples[key] = values;
          reservoirs.push({
            values,
            size: field.sampleSize,
            seen: 0,
            column: definition.columns.findIndex(
              (col) => col.name === field.sampleColumn,
            ),
            random: new Random(stableSeed(config.seed, key)),
          });
        }
      const input = dataset.inputFile
        ? createInterface({
            input: createReadStream(dataset.inputFile),
            crlfDelay: Infinity,
          })
        : null;
      const iterator = input?.[Symbol.asyncIterator]();
      let open = false;
      try {
        for (let index = 1; index <= dataset.rows; index++) {
          check();
          if (!open) {
            await sql.begin();
            open = true;
          }
          const row = BigInt(index);
          const rng = new Random(
            BigInt(dataset.seed) ^ unsigned(row * multiplier),
          );
          const ctx = { row, sequence: row, key: row, client: 0 };
          const values = iterator
            ? JSON.parse((await iterator.next()).value)
            : definition.columns.map((field) =>
                generate(field, ctx, rng, state),
              );
          for (const [i, field] of definition.columns.entries())
            if (values[i] == null && !field.nullable)
              throw Error('NULL in required column ' + field.name);
          await sql.execute(insert.sql, values, 'nonQuery', true);
          loaded++;
          for (const pool of reservoirs) {
            const value = values[pool.column];
            if (value == null) continue;
            pool.seen++;
            if (pool.values.length < pool.size) pool.values.push(value);
            else {
              const slot = Number(pool.random.next() % BigInt(pool.seen));
              if (slot < pool.values.length) pool.values[slot] = value;
            }
          }
          if (index % config.batchSize === 0) {
            await sql.finish(true);
            open = false;
          }
        }
        if (open) {
          await sql.finish(true);
          open = false;
        }
      } finally {
        input?.close();
        if (open) await sql.finish(false).catch(() => {});
      }
    }
    return loaded;
  }
  function scope(operation, warm) {
    return {
      operation,
      warm,
      sequence: state.sequence,
      rows: state.rows,
      samples: state.samples,
      completed: state.completed,
      byOperation: state.byOperation,
    };
  }
  const metadata = await database.metadata();
  Object.assign(metadata.settings, {
    workload_name: w.name,
    workload_sha256: p.fingerprint,
    generator: 'custom-splitmix64-v1',
    parameter_binding: 'nullable text with typed SQL casts',
  });
  emit('metadata', metadata);
  const stages = config.plan?.stages ?? [
    {
      clients: run.clients,
      seconds: config.durationSeconds,
      operationsPerSecond:
        config.loadModel === 'constantRate' ? config.targetOpsPerSecond : null,
    },
  ];
  let prepared = false;
  for (const unit of p.units) {
    check();
    const { phase, operation } = unit;
    const prefix = 'custom/setup/' + (operation ?? 'mix') + '/';
    if (!prepared || w.resetBetweenOperations) {
      await setup(prefix + 'schema', schema);
      await setup(prefix + 'load', load, true);
      await setup(prefix + 'indexes', async () => {
        for (const statement of p.afterLoad) {
          check();
          await sql.execute(statement, [], 'nonQuery', true);
        }
      });
      prepared = true;
    }
    if (p.checks.some(({ definition }) => definition.before))
      await setup(prefix + 'before', () => checks(true));
    if (
      config.warmupSeconds > 0 &&
      p.operations.some(
        ({ definition }) =>
          definition.readOnly && (!operation || operation === definition.id),
      )
    ) {
      const clients = await pool(stages[0].clients, scope(operation, true));
      try {
        const warm = await measure(
          clients,
          phase,
          config.warmupSeconds,
          stages[0].clients,
          null,
          null,
          false,
        );
        if (warm.errors) throw Error('Errors during custom workload warmup');
      } finally {
        await close(clients);
      }
    }
    const clients = [];
    try {
      for (const [index, stage] of stages.entries()) {
        check();
        await pool(stage.clients, scope(operation, false), clients);
        const stamp = config.plan
          ? {
              number: index + 1,
              name: stage.name,
              targetSeconds: stage.seconds,
              operationsPerSecond: stage.operationsPerSecond,
            }
          : null;
        emit('phase_start', {
          phase,
          targetSeconds: stage.seconds,
          clients: stage.clients,
          stage: stamp,
        });
        const result = await measure(
          clients,
          phase,
          stage.seconds,
          stage.clients,
          stage.operationsPerSecond,
          stamp,
          true,
          scope(operation, false),
        );
        emit('phase_end', {});
        state.completed += result.successfulOperations;
        for (const detail of result.operationBreakdown)
          state.byOperation[detail.id] =
            (state.byOperation[detail.id] ?? 0) + detail.successfulOperations;
        result.verified = result.successfulOperations > 0 && !cancelled();
        if (!cancelled())
          try {
            await checks(false);
          } catch (error) {
            result.verified = false;
            emit('result', result);
            throw error;
          }
        emit('result', result);
        check();
      }
    } finally {
      await close(clients);
    }
  }
}
