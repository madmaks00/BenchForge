import { DuckDatabase } from './duckdb.mjs';
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';
import { row as makeRow } from './portable.mjs';
const require = createRequire(import.meta.url);
export const extendedTargets = new Set([
  'sqlServer',
  'duckDb',
  'oracle',
  'cockroachDb',
  'elasticsearch',
  'openSearch',
]);
const values = (r) => [r.id, r.groupId, r.amountCents, r.version, r.payload];
function packageVersion(name) {
  for (
    let directory = path.dirname(require.resolve(name));
    ;
    directory = path.dirname(directory)
  ) {
    const file = path.join(directory, 'package.json');
    if (fs.existsSync(file)) {
      const info = JSON.parse(fs.readFileSync(file, 'utf8'));
      if (info.name === name) return info.version;
    }
    if (path.dirname(directory) === directory)
      throw Error('Missing driver version: ' + name);
  }
}

export class ExtendedDatabase {
  constructor(run) {
    this.run = run;
    this.t = run.target;
    this.e = run.endpoint;
  }
  async open() {
    const e = this.e,
      t = this.t;
    if (t !== 'duckDb' && !/^[A-Za-z][A-Za-z0-9_]*$/.test(e.database))
      throw Error('Invalid owned database name');
    try {
      if (t === 'duckDb') {
        this.db = new DuckDatabase(this.run);
      } else if (t === 'sqlServer') {
        this.driver = require('mssql');
        const options = {
          server: e.host,
          port: e.port,
          user: e.username,
          password: e.password,
          connectionTimeout: 15000,
          requestTimeout: 600000,
          pool: { min: 0, max: 1 },
          options: {
            encrypt: false,
            trustServerCertificate: true,
            enableArithAbort: true,
          },
        };
        const admin = await new this.driver.ConnectionPool({
          ...options,
          database: 'master',
        }).connect();
        try {
          await admin
            .request()
            .input('name', this.driver.NVarChar, e.database)
            .query(`IF DB_ID(@name) IS NULL CREATE DATABASE [${e.database}]`);
        } finally {
          await admin.close();
        }
        this.db = await new this.driver.ConnectionPool({
          ...options,
          database: e.database,
        }).connect();
        await this.sql(
          'ALTER DATABASE [dblab] SET RECOVERY SIMPLE; ALTER DATABASE [dblab] SET DELAYED_DURABILITY=DISABLED; SET QUOTED_IDENTIFIER ON; SET NOCOUNT OFF; SET XACT_ABORT ON',
        );
      } else if (t === 'oracle') {
        this.driver = require('oracledb');
        const options = {
          password: e.password,
          connectString: `${e.host}:${e.port}/${e.database}`,
          stmtCacheSize: 0,
        };
        const admin = await this.driver.getConnection({
          ...options,
          user: 'SYSTEM',
        });
        try {
          const r = await admin.execute(
            "SELECT COUNT(*) FROM all_users WHERE username='DBLAB'",
          );
          if (Number(r.rows[0][0]) === 0) {
            await admin.execute(
              `CREATE USER DBLAB IDENTIFIED BY "${e.password.replaceAll('"', '""')}"`,
            );
            await admin.execute(
              'GRANT CREATE SESSION,CREATE TABLE,UNLIMITED TABLESPACE TO DBLAB',
            );
          }
        } finally {
          await admin.close();
        }
        this.db = await this.driver.getConnection({
          ...options,
          user: e.username,
        });
        this.db.callTimeout = 600000;
      } else if (t === 'cockroachDb') {
        const { Client } = require('pg');
        const options = {
          host: e.host,
          port: e.port,
          user: e.username,
          password: e.password,
          connectionTimeoutMillis: 15000,
          ssl: false,
        };
        const admin = new Client({ ...options, database: 'defaultdb' });
        await admin.connect();
        try {
          await admin.query(`CREATE DATABASE IF NOT EXISTS "${e.database}"`);
        } finally {
          await admin.end();
        }
        this.db = new Client({ ...options, database: e.database });
        await this.db.connect();
      } else {
        await this.request('GET', '/');
        await this.request(
          'GET',
          '/_cluster/health?wait_for_status=yellow&timeout=5s',
        );
      }
      return this;
    } catch (error) {
      await this.close().catch(() => {});
      throw error;
    }
  }
  async sql(text, params = [], read = false, transaction = null) {
    if (this.t === 'duckDb') {
      const r = this.db.run(text, params);
      return read ? r.rows : r.changed;
    }
    if (this.t === 'sqlServer') {
      const r = new this.driver.Request(transaction ?? this.db);
      params.forEach((v, i) =>
        r.input(
          'p' + (i + 1),
          typeof v === 'string'
            ? this.driver.NVarChar(this.driver.MAX)
            : this.driver.BigInt,
          typeof v === 'string' ? v : String(v),
        ),
      );
      const result = await r.query(text);
      return read
        ? result.recordset.map((r) => Object.values(r))
        : result.rowsAffected.reduce((a, b) => a + b, 0);
    }
    if (this.t === 'oracle') {
      const binds = params.map((v, i) =>
        typeof v === 'string' && params.length === 5 && i === 4
          ? { val: v, type: this.driver.CLOB }
          : v,
      );
      const result = await this.db.execute(text, binds, {
        autoCommit: !transaction,
        fetchInfo: { PAYLOAD: { type: this.driver.STRING } },
      });
      return read ? result.rows : result.rowsAffected;
    }
    const result = await this.db.query(text, params);
    return read ? result.rows.map((r) => Object.values(r)) : result.rowCount;
  }
  parameter(n) {
    return this.t === 'oracle'
      ? ':' + n
      : this.t === 'sqlServer'
        ? '@p' + n
        : this.t === 'cockroachDb'
          ? '$' + n
          : '?';
  }
  insertSql() {
    return (
      'INSERT INTO records(id,group_id,amount_cents,version,payload) VALUES(' +
      [1, 2, 3, 4, 5].map((n) => this.parameter(n)).join(',') +
      ')'
    );
  }
  searchDocument(r) {
    return {
      id: r.id,
      group_id: r.groupId,
      amount_cents: r.amountCents,
      version: r.version,
      payload: r.payload,
      search_text:
        'benchforge group' +
        r.groupId +
        ' ' +
        r.payload.match(/.{1,16}/g).join(' '),
    };
  }
  get search() {
    return this.t === 'elasticsearch' || this.t === 'openSearch';
  }
  async request(method, path, body = null, missing = false) {
    const response = await fetch(
      `http://${this.e.host}:${this.e.port}${path}`,
      {
        method,
        headers: {
          'Content-Type': path.startsWith('/_bulk')
            ? 'application/x-ndjson'
            : 'application/json',
        },
        body:
          body === null
            ? undefined
            : typeof body === 'string'
              ? body
              : JSON.stringify(body),
        signal: AbortSignal.timeout(600000),
      },
    );
    const text = await response.text();
    if (missing && response.status === 404) return {};
    if (!response.ok)
      throw Error(`Search HTTP ${response.status}: ${text.slice(0, 350)}`);
    const r = JSON.parse(text);
    if (r.timed_out || (r._shards?.failed ?? 0) > 0)
      throw Error('Incomplete search response');
    return r;
  }
  async reset() {
    if (this.search) {
      await this.request('DELETE', '/records', null, true);
      await this.request('PUT', '/records', {
        settings: {
          number_of_shards: 1,
          number_of_replicas: 0,
          refresh_interval: '-1',
          'translog.durability': 'request',
        },
        mappings: {
          dynamic: 'strict',
          properties: {
            id: { type: 'long' },
            group_id: { type: 'integer' },
            amount_cents: { type: 'long' },
            version: { type: 'long' },
            payload: { type: 'keyword', index: false, doc_values: false },
            search_text: { type: 'text', analyzer: 'standard' },
          },
        },
      });
      return;
    }
    if (this.t === 'oracle') {
      await this.sql(
        "BEGIN EXECUTE IMMEDIATE 'DROP TABLE records PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;",
      );
      await this.sql(
        'CREATE TABLE records(id NUMBER(19) PRIMARY KEY,group_id NUMBER(10) NOT NULL,amount_cents NUMBER(19) NOT NULL,version NUMBER(19) NOT NULL,payload CLOB NOT NULL)',
      );
    } else {
      await this.sql('DROP TABLE IF EXISTS records');
      await this.sql(
        'CREATE TABLE records(id BIGINT PRIMARY KEY,group_id INTEGER NOT NULL,amount_cents BIGINT NOT NULL,version BIGINT NOT NULL,payload ' +
          (this.t === 'sqlServer' ? 'NVARCHAR(MAX)' : 'TEXT') +
          ' NOT NULL)',
      );
    }
  }
  async batch(rows) {
    if (this.search) {
      const body = rows
        .map(
          (r) =>
            JSON.stringify({
              create: { _index: 'records', _id: String(r.id) },
            }) +
            '\n' +
            JSON.stringify(this.searchDocument(r)) +
            '\n',
        )
        .join('');
      const result = await this.request('POST', '/_bulk?refresh=false', body);
      if (
        result.errors ||
        result.items?.length !== rows.length ||
        result.items.some((i) => i.create.status !== 201 || i.create.error)
      )
        throw Error('Bulk insert contains failed items');
      return;
    }
    let tx;
    if (this.t === 'sqlServer') {
      tx = new this.driver.Transaction(this.db);
      await tx.begin();
    } else if (this.t === 'oracle') tx = true;
    else await this.sql('BEGIN');
    try {
      for (const r of rows) {
        const n = await this.sql(this.insertSql(), values(r), false, tx);
        if (n !== 1) throw Error('Insert did not affect one record');
      }
      if (this.t === 'sqlServer') await tx.commit();
      else if (this.t === 'oracle') await this.db.commit();
      else await this.sql('COMMIT');
    } catch (error) {
      if (this.t === 'sqlServer') await tx.rollback();
      else if (this.t === 'oracle') await this.db.rollback();
      else await this.sql('ROLLBACK');
      throw error;
    }
  }
  async prepare() {
    if (this.search) {
      await this.request('PUT', '/records/_settings', {
        index: { refresh_interval: '1s' },
      });
      await this.request('POST', '/records/_refresh');
      return;
    }
    await this.sql('CREATE INDEX records_group_id ON records(group_id)');
    await this.sql(
      {
        sqlServer: 'UPDATE STATISTICS records WITH FULLSCAN',
        oracle: "BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER,'RECORDS'); END;",
        cockroachDb: 'CREATE STATISTICS records_stats FROM records',
        duckDb: 'ANALYZE records',
      }[this.t],
    );
  }
  async digest() {
    if (!this.search)
      return (
        await this.sql(
          'SELECT COUNT(*) AS n,COALESCE(SUM(id),0) AS ids,COALESCE(SUM(amount_cents),0) AS amount FROM records',
          [],
          true,
        )
      )[0].map(Number);
    await this.request('POST', '/records/_refresh');
    const r = await this.request('POST', '/records/_search', {
      size: 0,
      track_total_hits: true,
      aggs: {
        ids: { sum: { field: 'id' } },
        amount: { sum: { field: 'amount_cents' } },
      },
    });
    return [
      r.hits.total.value,
      r.aggregations.ids.value,
      r.aggregations.amount.value,
    ];
  }
  async count() {
    if (!this.search)
      return Number(
        (await this.sql('SELECT COUNT(*) FROM records', [], true))[0][0],
      );
    await this.request('POST', '/records/_refresh');
    return (await this.request('GET', '/records/_count')).count;
  }
  async metadata() {
    let version, driver;
    if (this.search) {
      version = (await this.request('GET', '/')).version.number;
      driver = 'Node fetch / HTTP JSON';
    } else {
      version = String(
        (
          await this.sql(
            {
              sqlServer:
                "SELECT CAST(SERVERPROPERTY('ProductVersion') AS VARCHAR(100))",
              oracle: 'SELECT banner FROM v$version WHERE ROWNUM=1',
            }[this.t] ?? 'SELECT version()',
            [],
            true,
          )
        )[0][0],
      );
      const name = {
        sqlServer: 'mssql',
        oracle: 'oracledb',
        duckDb: 'koffi',
        cockroachDb: 'pg',
      }[this.t];
      driver =
        name +
        ' / ' +
        packageVersion(name) +
        (this.t === 'duckDb' ? ' / DuckDB C API' : '');
    }
    return {
      engine: 'node',
      runtime: process.version,
      driver,
      serverVersion: version,
      settings: {
        generator: 'splitmix64-ascii-v1',
        connections: 'one driver connection/client worker thread',
        prepared_queries: 'driver parameter binding',
        batch_transport: this.search
          ? 'NDJSON bulk create; non-atomic; each item checked'
          : 'parameterized row INSERTs in transaction',
        retries: 'none',
        ...(this.search
          ? {
              search_corpus: 'payload-terms16-v1',
              durability_profile:
                'translog=request; refresh=1s; 1 primary; 0 replicas',
            }
          : {
              isolation:
                this.t === 'cockroachDb'
                  ? 'SERIALIZABLE; single-node'
                  : 'driver/server default',
            }),
      },
    };
  }
  async execute(op, key, row, batch) {
    if (op === 'BatchInsert') {
      await this.batch(batch);
      return batch.length;
    }
    if (this.search) return this.searchExecute(op, key, row);
    const p = this.parameter(1),
      select = 'SELECT id,group_id,amount_cents,version,payload FROM records';
    const texts = {
      PointRead: select + ' WHERE id=' + p,
      RangeRead: select + ' WHERE id>=' + p + ' ORDER BY id',
      Aggregate:
        'SELECT group_id,COUNT(*) AS n,SUM(amount_cents) AS amount FROM records WHERE group_id=' +
        p +
        ' GROUP BY group_id',
      Update: 'UPDATE records SET version=version+1 WHERE id=' + p,
      Delete: 'DELETE FROM records WHERE id=' + p,
      Insert: this.insertSql(),
      AnalyticalScan:
        'SELECT COUNT(*),SUM(amount_cents),AVG(amount_cents) FROM records WHERE id>=? AND id<?',
    };
    if (op === 'RangeRead')
      texts[op] =
        this.t === 'sqlServer'
          ? texts[op].replace('SELECT ', 'SELECT TOP (100) ')
          : texts[op] +
            (this.t === 'oracle' ? ' FETCH FIRST 100 ROWS ONLY' : ' LIMIT 100');
    const read = [
      'PointRead',
      'RangeRead',
      'Aggregate',
      'AnalyticalScan',
    ].includes(op);
    const params =
      op === 'Insert'
        ? values(row)
        : op === 'AnalyticalScan'
          ? [key, key + Math.max(100, Math.floor(this.run.rows / 100))]
          : [op === 'Aggregate' ? (key - 1) % 100 : key];
    const result = await this.sql(texts[op], params, read);
    if (!read) {
      if (result !== 1) throw Error('Write did not affect one record');
      return 1;
    }
    if (
      !result.length ||
      (op === 'PointRead' &&
        (result.length !== 1 || Number(result[0][0]) !== key))
    )
      throw Error('Invalid SQL response');
    if (op === 'PointRead' || op === 'RangeRead') {
      let previous = key - 1;
      for (const r of result) {
        if (
          Number(r[0]) <= previous ||
          typeof r[4] !== 'string' ||
          r[4].length !== (this.run.config.payloadBytes ?? 256)
        )
          throw Error('Invalid ID or payload');
        previous = Number(r[0]);
      }
    }
    return result.length;
  }
  async searchExecute(op, key, row) {
    const path = '/records/_doc/' + key;
    if (op === 'PointRead') {
      const r = await this.request('GET', path);
      if (
        !r.found ||
        r._source.id !== key ||
        r._source.payload.length !== (this.run.config.payloadBytes ?? 256)
      )
        throw Error('Invalid document');
      return 1;
    }
    if (['Update', 'Insert', 'Delete'].includes(op)) {
      const r =
        op === 'Update'
          ? await this.request(
              'POST',
              '/records/_update/' + key + '?refresh=false&retry_on_conflict=0',
              {
                script: {
                  source: 'ctx._source.version += 1',
                  lang: 'painless',
                },
              },
            )
          : op === 'Insert'
            ? await this.request(
                'PUT',
                '/records/_create/' + row.id + '?refresh=false',
                this.searchDocument(row),
              )
            : await this.request('DELETE', path + '?refresh=false');
      if (
        r.result !==
        { Update: 'updated', Insert: 'created', Delete: 'deleted' }[op]
      )
        throw Error('Search write failed');
      return 1;
    }
    if (op === 'Aggregate') {
      const r = await this.request('POST', '/records/_search', {
        size: 0,
        track_total_hits: true,
        query: { term: { group_id: (key - 1) % 100 } },
        aggs: { amount: { sum: { field: 'amount_cents' } } },
      });
      if (
        r.hits.total.value <= 0 ||
        typeof r.aggregations.amount.value !== 'number'
      )
        throw Error('Empty aggregate');
      return 1;
    }
    const term =
      op === 'FullTextSearch'
        ? makeRow(
            key,
            BigInt(this.run.config.seed ?? 42),
            this.run.config.payloadBytes ?? 256,
          ).payload.slice(0, 16)
        : null;
    const body = term
      ? { size: 10, query: { match: { search_text: term } } }
      : {
          size: 100,
          query: { range: { id: { gte: key } } },
          sort: [{ id: 'asc' }],
        };
    const hits = (await this.request('POST', '/records/_search', body)).hits
      .hits;
    if (!hits.length) throw Error('Empty search response');
    let previous = key - 1;
    for (const { _source: r } of hits) {
      if (
        r.payload.length !== (this.run.config.payloadBytes ?? 256) ||
        (term
          ? !r.search_text.toLowerCase().split(' ').includes(term.toLowerCase())
          : r.id <= previous)
      )
        throw Error('Invalid search result');
      previous = r.id;
    }
    return hits.length;
  }
  async close() {
    if (this.t === 'duckDb') this.db?.closeSync();
    else if (this.t === 'cockroachDb') await this.db?.end();
    else await this.db?.close();
  }
}
