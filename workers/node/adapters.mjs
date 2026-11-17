import { ExtendedDatabase, extendedTargets } from './extended.mjs';
import { createRequire } from 'node:module';
import { once } from 'node:events';
const require = createRequire(import.meta.url);
const values = (r) => [r.id, r.groupId, r.amountCents, r.version, r.payload];
const document = (r) => ({
  _id: r.id,
  group_id: r.groupId,
  amount_cents: r.amountCents,
  version: r.version,
  payload: r.payload,
});
export class Database {
  constructor(run) {
    this.run = run;
    this.target = run.target;
    this.ep = run.endpoint;
    this.prefix = 'dblab:' + run.runId + ':';
    this.statements = {};
  }
  async open() {
    if (extendedTargets.has(this.target)) return new ExtendedDatabase(this.run).open();
    const e = this.ep,
      t = this.target;
    try {
      if (t === 'sqlite') {
        const { DatabaseSync } = await import('node:sqlite');
        this.db = new DatabaseSync(e.filePath);
        this.db.exec('PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=10000');
      } else if (t === 'postgreSql') {
        const { Client } = require('pg');
        this.db = new Client({
          host: e.host,
          port: e.port,
          user: e.username,
          password: e.password,
          database: e.database,
          connectionTimeoutMillis: 5000,
          options: '-c synchronous_commit=on -c statement_timeout=10000',
        });
        await this.db.connect();
      } else if (t === 'mySql' || t === 'mariaDb') {
        this.db = await require('mysql2/promise').createConnection({
          host: e.host,
          port: e.port,
          user: e.username,
          password: e.password,
          database: e.database,
          connectTimeout: 5000,
          supportBigNumbers: true,
          bigNumberStrings: true,
          enableKeepAlive: true,
        });
      } else if (t === 'mongoDb') {
        const { MongoClient } = require('mongodb');
        this.client = new MongoClient(`mongodb://${e.host}:${e.port}`, {
          auth: { username: e.username, password: e.password },
          authSource: 'admin',
          retryWrites: false,
          connectTimeoutMS: 5000,
          serverSelectionTimeoutMS: 5000,
          socketTimeoutMS: 600000,
          maxPoolSize: Math.max(4, this.run.clients + 2),
        });
        await this.client.connect();
        this.db = this.client.db(e.database);
        this.collection = this.db.collection('records', {
          writeConcern: { w: 1, j: true, wtimeoutMS: 10000 },
        });
        await this.db.command({ ping: 1 });
      } else if (t === 'redis' || t === 'valkey') {
        this.db = require('redis').createClient({
          socket: { host: e.host, port: e.port, connectTimeout: 5000, reconnectStrategy: false },
          password: e.password,
        });
        this.db.on('error', () => {});
        await this.db.connect();
        await this.db.ping();
      } else await this.query('SELECT 1');
      return this;
    } catch (error) {
      await this.close().catch(() => {});
      throw error;
    }
  }
  async sql(query, params = [], read = false, preparedName = null) {
    if (this.target === 'sqlite') {
      let stmt = this.statements[query];
      if (!stmt) {
        stmt = this.db.prepare(query);
        stmt.setReadBigInts(true);
        this.statements[query] = stmt;
      }
      return read ? stmt.all(...params) : Number(stmt.run(...params).changes);
    }
    if (this.target === 'postgreSql') {
      const result = await this.db.query(
        preparedName
          ? { name: preparedName, text: query, values: params }
          : { text: query, values: params },
      );
      return read ? result.rows : result.rowCount;
    }
    const [result] = await this.db[preparedName ? 'execute' : 'query'](query, params);
    return read ? result : result.affectedRows;
  }
  async query(query, body = '') {
    const url =
      `http://${this.ep.host}:${this.ep.port}/?` +
      new URLSearchParams({ database: this.ep.database, query });
    const response = await fetch(url, {
      method: 'POST',
      headers: {
        Authorization:
          'Basic ' + Buffer.from(this.ep.username + ':' + this.ep.password).toString('base64'),
        'Content-Type': 'text/plain',
      },
      body,
      signal: AbortSignal.timeout(600000),
    });
    const text = await response.text();
    if (!response.ok) throw Error(`ClickHouse HTTP ${response.status}: ${text.slice(0, 350)}`);
    return text;
  }
  async reset() {
    const t = this.target;
    if (t === 'mongoDb') {
      try {
        await this.collection.drop();
      } catch (e) {
        if (e.code !== 26) throw e;
      }
      await this.db.createCollection('records');
    } else if (t === 'redis' || t === 'valkey') {
    } else if (t === 'clickHouse') {
      await this.query('DROP TABLE IF EXISTS records');
      await this.query(
        'CREATE TABLE records (id Int64,group_id Int32,amount_cents Int64,version Int64,payload String) ENGINE=MergeTree ORDER BY id',
      );
    } else {
      await this.sql('DROP TABLE IF EXISTS records');
      await this.sql(
        `CREATE TABLE records (id BIGINT PRIMARY KEY,group_id INTEGER NOT NULL,amount_cents BIGINT NOT NULL,version BIGINT NOT NULL,payload ${t === 'mySql' || t === 'mariaDb' ? 'LONGTEXT' : 'TEXT'} NOT NULL)`,
      );
    }
  }
  async batch(rows) {
    const t = this.target;
    if (t === 'mongoDb') {
      await this.collection.insertMany(rows.map(document), { ordered: true });
      return;
    }
    if (t === 'redis' || t === 'valkey') {
      const pipeline = this.db.multi();
      for (const r of rows)
        pipeline.hSet(this.prefix + r.id, {
          group_id: String(r.groupId),
          amount_cents: String(r.amountCents),
          version: String(r.version),
          payload: r.payload,
        });
      await pipeline.execAsPipeline();
      return;
    }
    if (t === 'clickHouse') {
      await this.query(
        'INSERT INTO records FORMAT JSONEachRow',
        rows
          .map((r) =>
            JSON.stringify({
              id: r.id,
              group_id: r.groupId,
              amount_cents: r.amountCents,
              version: r.version,
              payload: r.payload,
            }),
          )
          .join('\n') + '\n',
      );
      return;
    }
    if (t === 'postgreSql') {
      const copy = this.db.query(
        require('pg-copy-streams').from(
          'COPY records(id,group_id,amount_cents,version,payload) FROM STDIN (FORMAT BINARY)',
        ),
      );
      const completed = once(copy, 'finish');
      try {
        copy.write(Buffer.concat([Buffer.from('PGCOPY\n\xff\r\n\0', 'binary'), Buffer.alloc(8)]));
        for (const r of rows) {
          const payload = Buffer.from(r.payload),
            buffer = Buffer.alloc(2 + 4 * 5 + 8 + 4 + 8 + 8 + payload.length);
          let i = 0;
          buffer.writeInt16BE(5, i);
          i += 2;
          for (const [value, size] of [
            [r.id, 8],
            [r.groupId, 4],
            [r.amountCents, 8],
            [r.version, 8],
          ]) {
            buffer.writeInt32BE(size, i);
            i += 4;
            if (size === 8) buffer.writeBigInt64BE(BigInt(value), i);
            else buffer.writeInt32BE(value, i);
            i += size;
          }
          buffer.writeInt32BE(payload.length, i);
          i += 4;
          payload.copy(buffer, i);
          if (!copy.write(buffer)) await once(copy, 'drain');
        }
        copy.end(Buffer.from([255, 255]));
        await completed;
      } catch (e) {
        copy.destroy(e);
        await completed.catch(() => {});
        throw e;
      }
      return;
    }
    if (t === 'sqlite') {
      this.db.exec('BEGIN');
      try {
        for (const r of rows) await this.sql('INSERT INTO records VALUES(?,?,?,?,?)', values(r));
        this.db.exec('COMMIT');
      } catch (e) {
        this.db.exec('ROLLBACK');
        throw e;
      }
      return;
    }
    await this.sql(
      'INSERT INTO records VALUES ' + rows.map(() => '(?,?,?,?,?)').join(','),
      rows.flatMap(values),
    );
  }
  async prepare() {
    if (this.target === 'mongoDb') await this.collection.createIndex({ group_id: 1 });
    else if (!['redis', 'valkey', 'clickHouse'].includes(this.target))
      await this.sql('CREATE INDEX records_group_id ON records(group_id)').then(() =>
        this.sql(
          this.target === 'mySql' || this.target === 'mariaDb'
            ? 'ANALYZE TABLE records'
            : 'ANALYZE records',
          [],
          this.target === 'mySql' || this.target === 'mariaDb',
        ),
      );
  }
  async digest() {
    if (this.target === 'mongoDb') {
      const [r] = await this.collection
        .aggregate(
          [
            {
              $group: {
                _id: null,
                n: { $sum: 1 },
                ids: { $sum: '$_id' },
                amount: { $sum: '$amount_cents' },
              },
            },
          ],
          { maxTimeMS: 600000 },
        )
        .toArray();
      return r ? [Number(r.n), Number(r.ids), Number(r.amount)] : [0, 0, 0];
    }
    if (this.target === 'redis' || this.target === 'valkey') {
      const script =
        "local r=redis.call('SCAN',ARGV[1],'MATCH',ARGV[2]..'*','COUNT',1000); local n=0; local ids=0; local a=0; for _,k in ipairs(r[2]) do n=n+1; ids=ids+tonumber(string.sub(k,string.len(ARGV[2])+1)); a=a+tonumber(redis.call('HGET',k,'amount_cents')); end; return {r[1],n,ids,a}";
      let cursor = '0',
        sums = [0, 0, 0];
      do {
        const r = await this.db.eval(script, { keys: [], arguments: [cursor, this.prefix] });
        cursor = String(r[0]);
        for (let i = 0; i < 3; i++) sums[i] += Number(r[i + 1]);
      } while (cursor !== '0');
      return sums;
    }
    if (this.target === 'clickHouse')
      return (
        await this.query(
          'SELECT count(),sum(id),sum(amount_cents) FROM records FORMAT TabSeparated',
        )
      )
        .trim()
        .split('\t')
        .map(Number);
    const result = (
      await this.sql(
        'SELECT COUNT(*) AS n,COALESCE(SUM(id),0) AS ids,COALESCE(SUM(amount_cents),0) AS amount FROM records',
        [],
        true,
      )
    )[0];
    return [result.n, result.ids, result.amount].map(Number);
  }
  async count() {
    if (this.target === 'mongoDb') return this.collection.countDocuments({});
    if (this.target === 'redis' || this.target === 'valkey') return this.db.dbSize();
    if (this.target === 'clickHouse')
      return Number((await this.query('SELECT count() FROM records FORMAT TabSeparated')).trim());
    return Number(Object.values((await this.sql('SELECT COUNT(*) FROM records', [], true))[0])[0]);
  }
  async metadata() {
    const t = this.target,
      settings = {
        connections: 'one worker thread and connection per logical client',
        build: 'Release',
        generator: 'splitmix64-ascii-v1',
        gc_metrics: 'not measured; null',
      };
    let serverVersion, driver;
    if (['sqlite', 'postgreSql', 'mySql', 'mariaDb'].includes(t)) {
      serverVersion = String(
        Object.values(
          (
            await this.sql(
              t === 'sqlite' ? 'SELECT sqlite_version()' : 'SELECT VERSION()',
              [],
              true,
            )
          )[0],
        )[0],
      );
      driver =
        t === 'sqlite'
          ? 'node:sqlite / ' + process.versions.sqlite
          : (t === 'postgreSql' ? 'pg' : 'mysql2') +
            ' / ' +
            require((t === 'postgreSql' ? 'pg' : 'mysql2') + '/package.json').version;
      const keys =
        t === 'sqlite'
          ? ['journal_mode', 'synchronous', 'cache_size']
          : t === 'postgreSql'
            ? ['synchronous_commit', 'fsync', 'shared_buffers', 'max_connections']
            : ['innodb_flush_log_at_trx_commit', 'innodb_buffer_pool_size', 'max_connections'];
      for (const key of keys)
        settings[key] = String(
          Object.values(
            (
              await this.sql(
                t === 'sqlite'
                  ? 'PRAGMA ' + key
                  : t === 'postgreSql'
                    ? 'SHOW ' + key
                    : 'SELECT @@GLOBAL.' + key,
                [],
                true,
              )
            )[0],
          )[0],
        );
      settings.batch_transport =
        t === 'sqlite'
          ? 'prepared INSERT in transaction'
          : t === 'postgreSql'
            ? 'binary COPY'
            : 'multi-row parameterized INSERT';
      settings.prepared_queries = 'true';
    } else if (t === 'mongoDb') {
      serverVersion = (await this.db.command({ buildInfo: 1 })).version;
      driver = 'mongodb / ' + require('mongodb/package.json').version;
      Object.assign(settings, {
        write_concern: 'w=1,j=true',
        retry_writes: 'false',
        topology: 'standalone',
      });
    } else if (t === 'redis' || t === 'valkey') {
      const info = await this.db.info('server');
      serverVersion =
        info.match(/valkey_version:([^\r\n]+)/)?.[1] ??
        info.match(/redis_version:([^\r\n]+)/)?.[1] ??
        'unknown';
      driver = 'node-redis / ' + require('redis/package.json').version;
      Object.assign(settings, {
        durability_profile: 'memory cache: AOF and RDB disabled',
        batch_transport: 'pipelined HSET; non-atomic',
      });
    } else {
      serverVersion = (await this.query('SELECT version() FORMAT TabSeparated')).trim();
      driver = 'Node.js fetch / undici ' + process.versions.undici;
      Object.assign(settings, {
        transport: 'HTTP / JSONEachRow / uncompressed',
        insert: 'synchronous HTTP; async_insert=0',
        table: 'MergeTree ORDER BY id; no unique constraint',
      });
    }
    return {
      engine: 'node',
      runtime: 'Node.js ' + process.version + ' / V8 ' + process.versions.v8,
      driver,
      serverVersion,
      settings,
    };
  }
  async execute(operation, key, row, batch) {
    if (operation === 'BatchInsert') {
      await this.batch(batch);
      return batch.length;
    }
    const t = this.target;
    let affected;
    if (t === 'mongoDb') {
      const c = this.collection;
      if (operation === 'PointRead') {
        const r = await c.findOne({ _id: key }, { maxTimeMS: 10000 });
        if (!r || r._id !== key || typeof r.payload !== 'string')
          throw Error('Invalid MongoDB row');
        return 1;
      }
      if (operation === 'RangeRead') {
        const r = await c
          .find({ _id: { $gte: key } })
          .sort({ _id: 1 })
          .limit(100)
          .maxTimeMS(10000)
          .toArray();
        if (!r.length) throw Error('Empty range');
        return r.length;
      }
      if (operation === 'Aggregate') {
        const r = await c
          .aggregate(
            [
              { $match: { group_id: (key - 1) % 100 } },
              { $group: { _id: '$group_id', n: { $sum: 1 }, amount: { $sum: '$amount_cents' } } },
            ],
            { maxTimeMS: 10000 },
          )
          .toArray();
        if (!r.length) throw Error('Empty aggregate');
        return r.length;
      }
      if (operation === 'Insert') {
        await c.insertOne(document(row));
        return 1;
      }
      affected =
        operation === 'Update'
          ? (await c.updateOne({ _id: key }, { $inc: { version: 1 } })).matchedCount
          : (await c.deleteOne({ _id: key })).deletedCount;
    } else if (t === 'redis' || t === 'valkey') {
      const name = this.prefix + key;
      if (operation === 'PointRead') {
        if (Object.keys(await this.db.hGetAll(name)).length !== 4)
          throw Error('Incomplete Redis hash');
        return 1;
      }
      if (operation === 'Update') {
        await this.db.hIncrBy(name, 'version', 1);
        return 1;
      }
      if (operation === 'Insert') {
        await this.batch([row]);
        return 1;
      }
      affected = await this.db.del(name);
    } else if (t === 'clickHouse') {
      const query =
        operation === 'RangeRead'
          ? `SELECT id,group_id,amount_cents,version,payload FROM records WHERE id>=${key} ORDER BY id LIMIT 100`
          : operation === 'Aggregate'
            ? `SELECT group_id,count(),sum(amount_cents) FROM records WHERE group_id=${(key - 1) % 100} GROUP BY group_id`
            : `SELECT count(),sum(amount_cents),avg(amount_cents) FROM records WHERE id>=${key} AND id<${key + Math.max(100, Math.floor(this.run.rows / 100))}`;
      const lines = (await this.query(query + ' SETTINGS max_execution_time=10 FORMAT JSONEachRow'))
        .trim()
        .split('\n')
        .filter(Boolean);
      for (const line of lines) JSON.parse(line);
      if (!lines.length) throw Error('Empty ClickHouse response');
      return lines.length;
    } else {
      const p = (i) => (t === 'postgreSql' ? '$' + i : '?');
      const queries = {
        PointRead: `SELECT id,group_id,amount_cents,version,payload FROM records WHERE id=${p(1)}`,
        RangeRead: `SELECT id,group_id,amount_cents,version,payload FROM records WHERE id>=${p(1)} ORDER BY id LIMIT 100`,
        Aggregate: `SELECT group_id,COUNT(*),SUM(amount_cents) FROM records WHERE group_id=${p(1)} GROUP BY group_id`,
        Update: `UPDATE records SET version=version+1 WHERE id=${p(1)}`,
        Delete: `DELETE FROM records WHERE id=${p(1)}`,
        Insert: `INSERT INTO records VALUES(${[1, 2, 3, 4, 5].map(p).join(',')})`,
      };
      const read = ['PointRead', 'RangeRead', 'Aggregate'].includes(operation);
      const r = await this.sql(
        queries[operation],
        operation === 'Insert' ? values(row) : [operation === 'Aggregate' ? (key - 1) % 100 : key],
        read,
        operation,
      );
      if (read) {
        if (!r.length || (operation === 'PointRead' && (r.length !== 1 || Number(r[0].id) !== key)))
          throw Error('Invalid SQL response');
        for (const value of r)
          if (operation !== 'Aggregate' && typeof value.payload !== 'string')
            throw Error('Payload was not read');
        return r.length;
      }
      affected = r;
    }
    if (affected !== 1) throw Error('Write did not affect one record');
    return affected;
  }
  async close() {
    if (this.target === 'mongoDb') await this.client?.close();
    else if (this.target === 'redis' || this.target === 'valkey') {
      if (this.db?.isOpen) await this.db.quit();
    } else if (this.target === 'sqlite') this.db?.close();
    else await this.db?.end();
  }
}
