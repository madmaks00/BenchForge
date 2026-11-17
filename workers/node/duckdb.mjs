// Share the native database handle inside this process. Each worker thread owns
// an independent connection and prepared statements; no requests cross languages.
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { isMainThread } from 'node:worker_threads';
import path from 'node:path';
const require = createRequire(import.meta.url);
let api;
function load() {
  if (api) return api;
  const koffi = require('koffi');
  const name = {
    win32: 'duckdb.dll',
    linux: 'libduckdb.so',
    darwin: 'libduckdb.dylib',
  }[process.platform];
  const lib = koffi.load(
    path.join(path.dirname(fileURLToPath(import.meta.url)), 'native', name),
  );
  api = { koffi };
  const signatures = {
    open: 'int duckdb_open(str path, _Out_ void **db)',
    close: 'void duckdb_close(_Inout_ void **db)',
    connect: 'int duckdb_connect(void *db, _Out_ void **connection)',
    disconnect: 'void duckdb_disconnect(_Inout_ void **connection)',
    prepare:
      'int duckdb_prepare(void *connection, str sql, _Out_ void **statement)',
    prepareError: 'str duckdb_prepare_error(void *statement)',
    destroyPrepare: 'void duckdb_destroy_prepare(_Inout_ void **statement)',
    bindInt:
      'int duckdb_bind_int64(void *statement, uint64_t index, int64_t value)',
    bindText:
      'int duckdb_bind_varchar(void *statement, uint64_t index, str value)',
    execute: 'int duckdb_execute_prepared(void *statement, void *result)',
    destroy: 'void duckdb_destroy_result(void *result)',
    error: 'str duckdb_result_error(void *result)',
    rows: 'uint64_t duckdb_row_count(void *result)',
    columns: 'uint64_t duckdb_column_count(void *result)',
    changed: 'uint64_t duckdb_rows_changed(void *result)',
    value:
      'void *duckdb_value_varchar(void *result, uint64_t column, uint64_t row)',
    isNull:
      'bool duckdb_value_is_null(void *result, uint64_t column, uint64_t row)',
    free: 'void duckdb_free(void *value)',
  };
  for (const [name, signature] of Object.entries(signatures))
    api[name] = lib.func(signature);
  return api;
}
export class DuckDatabase {
  constructor(run) {
    this.api = load();
    this.statements = new Map();
    const a = this.api;
    if (isMainThread) {
      const db = [null];
      if (a.open(run.endpoint.filePath, db) !== 0)
        throw Error('DuckDB could not open owned file');
      this.owner = db;
      run.endpoint.localDuckHandle = String(db[0]);
    }
    if (!run.endpoint.localDuckHandle)
      throw Error('Missing process-local DuckDB handle');
    const c = [null];
    if (a.connect(BigInt(run.endpoint.localDuckHandle), c) !== 0)
      throw Error('DuckDB connection failed');
    this.connection = c;
    this.run(`SET memory_limit='${run.config.memoryMb ?? 512}MB'`);
    this.run(`SET threads=${Math.max(1, Math.ceil(run.config.cpuLimit ?? 2))}`);
  }
  run(text, params = []) {
    const a = this.api;
    let statement = this.statements.get(text);
    if (!statement) {
      statement = [null];
      if (a.prepare(this.connection[0], text, statement) !== 0) {
        const message = a.prepareError(statement[0]);
        if (statement[0]) a.destroyPrepare(statement);
        throw Error(message);
      }
      this.statements.set(text, statement);
    }
    params.forEach((v, i) => {
      const status =
        typeof v === 'string'
          ? a.bindText(statement[0], i + 1, v)
          : a.bindInt(statement[0], i + 1, BigInt(v));
      if (status !== 0) throw Error('DuckDB parameter binding failed');
    });
    const result = Buffer.alloc(48);
    try {
      if (a.execute(statement[0], result) !== 0) throw Error(a.error(result));
      const rows = [];
      for (let i = 0; i < Number(a.rows(result)); i++) {
        const r = [];
        for (let j = 0; j < Number(a.columns(result)); j++) {
          if (a.isNull(result, j, i)) {
            r.push(null);
            continue;
          }
          const p = a.value(result, j, i);
          try {
            r.push(a.koffi.decode(p, 'char', -1));
          } finally {
            a.free(p);
          }
        }
        rows.push(r);
      }
      return { rows, changed: Number(a.changed(result)) };
    } finally {
      a.destroy(result);
    }
  }
  closeSync() {
    for (const statement of this.statements.values())
      this.api.destroyPrepare(statement);
    this.statements.clear();
    if (this.connection?.[0]) this.api.disconnect(this.connection);
    if (this.owner?.[0]) this.api.close(this.owner);
  }
}
