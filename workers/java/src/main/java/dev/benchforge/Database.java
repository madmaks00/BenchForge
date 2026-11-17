package dev.benchforge;

import static dev.benchforge.Portable.*;

import com.fasterxml.jackson.databind.JsonNode;
import com.mongodb.*;
import com.mongodb.client.*;
import com.mongodb.client.model.*;
import java.io.*;
import java.net.*;
import java.net.http.*;
import java.nio.charset.StandardCharsets;
import java.sql.*;
import java.time.Duration;
import java.util.*;
import java.util.concurrent.TimeUnit;
import org.bson.Document;
import org.postgresql.PGConnection;
import redis.clients.jedis.Jedis;

final class Database implements AutoCloseable {
  final JsonNode run, ep;
  final String target, prefix;
  ExtendedDatabase extended;
  Connection sql;
  MongoClient mongo;
  MongoDatabase mongoDb;
  MongoCollection<Document> collection;
  Jedis redis;
  HttpClient http;
  final Map<String, PreparedStatement> statements = new HashMap<>();

  Database(JsonNode run) throws Exception {
    this.run = run;
    ep = run.path("endpoint");
    target = run.path("target").asText();
    prefix = "dblab:" + run.path("runId").asText() + ":";
    String address = ep.path("host").asText() + ":" + ep.path("port").asInt(),
        database = ep.path("database").asText();
    if (ExtendedDatabase.TARGETS.contains(target)) {
      extended = new ExtendedDatabase(run);
      return;
    }
    var properties = new Properties();
    properties.setProperty("user", ep.path("username").asText());
    properties.setProperty("password", ep.path("password").asText());
    try {
      switch (target) {
        case "sqlite" -> {
          sql = DriverManager.getConnection("jdbc:sqlite:" + ep.path("filePath").asText());
          try (var command = sql.createStatement()) {
            command.execute("PRAGMA journal_mode=WAL");
            command.execute("PRAGMA synchronous=FULL");
            command.execute("PRAGMA busy_timeout=10000");
          }
        }
        case "postgreSql" -> {
          properties.setProperty("connectTimeout", "5");
          properties.setProperty("socketTimeout", "600");
          properties.setProperty("options", "-c synchronous_commit=on -c statement_timeout=10000");
          properties.setProperty("prepareThreshold", "1");
          sql =
              DriverManager.getConnection(
                  "jdbc:postgresql://" + address + "/" + database, properties);
        }
        case "mySql" -> {
          properties.setProperty("connectTimeout", "5000");
          properties.setProperty("socketTimeout", "15000");
          properties.setProperty("sslMode", "DISABLED");
          properties.setProperty("allowPublicKeyRetrieval", "true");
          properties.setProperty("useServerPrepStmts", "true");
          properties.setProperty("rewriteBatchedStatements", "true");
          sql = DriverManager.getConnection("jdbc:mysql://" + address + "/" + database, properties);
        }
        case "mariaDb" -> {
          properties.setProperty("connectTimeout", "5000");
          properties.setProperty("socketTimeout", "15000");
          properties.setProperty("useServerPrepStmts", "true");
          sql =
              DriverManager.getConnection("jdbc:mariadb://" + address + "/" + database, properties);
        }
        case "mongoDb" -> {
          var settings =
              MongoClientSettings.builder()
                  .applyToClusterSettings(
                      builder ->
                          builder
                              .hosts(
                                  List.of(
                                      new ServerAddress(
                                          ep.path("host").asText(), ep.path("port").asInt())))
                              .serverSelectionTimeout(5, TimeUnit.SECONDS))
                  .credential(
                      MongoCredential.createCredential(
                          ep.path("username").asText(),
                          "admin",
                          ep.path("password").asText().toCharArray()))
                  .retryWrites(false)
                  .writeConcern(
                      WriteConcern.W1.withJournal(true).withWTimeout(10, TimeUnit.SECONDS))
                  .applyToSocketSettings(
                      builder ->
                          builder
                              .connectTimeout(5, TimeUnit.SECONDS)
                              .readTimeout(600, TimeUnit.SECONDS))
                  .applyToConnectionPoolSettings(
                      builder -> builder.maxSize(Math.max(4, run.path("clients").asInt() + 2)))
                  .build();
          mongo = MongoClients.create(settings);
          mongoDb = mongo.getDatabase(database);
          collection = mongoDb.getCollection("records");
          mongoDb.runCommand(new Document("ping", 1));
        }
        case "redis", "valkey" -> {
          redis = new Jedis(ep.path("host").asText(), ep.path("port").asInt(), 15000);
          redis.auth(ep.path("password").asText());
          redis.ping();
        }
        case "clickHouse" -> {
          http = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(5)).build();
          query("SELECT 1", "");
        }
        default -> throw new IllegalArgumentException("Unsupported target");
      }
    } catch (Exception error) {
      close();
      throw error;
    }
  }

  String query(String query, String body) throws Exception {
    String url =
        "http://"
            + ep.path("host").asText()
            + ":"
            + ep.path("port").asInt()
            + "/?database="
            + URLEncoder.encode(ep.path("database").asText(), StandardCharsets.UTF_8)
            + "&query="
            + URLEncoder.encode(query, StandardCharsets.UTF_8);
    String credentials =
        Base64.getEncoder()
            .encodeToString(
                (ep.path("username").asText() + ":" + ep.path("password").asText())
                    .getBytes(StandardCharsets.UTF_8));
    var request =
        HttpRequest.newBuilder(URI.create(url))
            .timeout(Duration.ofSeconds(600))
            .header("Authorization", "Basic " + credentials)
            .header("Content-Type", "text/plain")
            .POST(HttpRequest.BodyPublishers.ofString(body))
            .build();
    var response = http.send(request, HttpResponse.BodyHandlers.ofString());
    if (response.statusCode() != 200)
      throw new IOException(
          "ClickHouse HTTP "
              + response.statusCode()
              + ": "
              + response.body().substring(0, Math.min(350, response.body().length())));
    return response.body();
  }

  void command(String text) throws SQLException {
    try (var statement = sql.createStatement()) {
      statement.setQueryTimeout(600);
      statement.execute(text);
    }
  }

  void reset() throws Exception {
    if (extended != null) {
      extended.reset();
      return;
    }
    if (mongo != null) {
      collection.drop();
      mongoDb.createCollection("records");
    } else if (redis != null) {
    } else if (http != null) {
      query("DROP TABLE IF EXISTS records", "");
      query(
          "CREATE TABLE records (id Int64,group_id Int32,amount_cents Int64,version Int64,payload"
              + " String) ENGINE=MergeTree ORDER BY id",
          "");
    } else {
      command("DROP TABLE IF EXISTS records");
      command(
          "CREATE TABLE records (id BIGINT PRIMARY KEY,group_id INTEGER NOT NULL,amount_cents"
              + " BIGINT NOT NULL,version BIGINT NOT NULL,payload "
              + (target.equals("mySql") || target.equals("mariaDb") ? "LONGTEXT" : "TEXT")
              + " NOT NULL)");
    }
  }

  static Document doc(Row row) {
    return new Document("_id", row.id())
        .append("group_id", row.groupId())
        .append("amount_cents", row.amountCents())
        .append("version", row.version())
        .append("payload", row.payload());
  }

  static Map<String, String> fields(Row row) {
    return Map.of(
        "group_id",
        Integer.toString(row.groupId()),
        "amount_cents",
        Long.toString(row.amountCents()),
        "version",
        Long.toString(row.version()),
        "payload",
        row.payload());
  }

  static void bind(PreparedStatement statement, Row row) throws SQLException {
    statement.setLong(1, row.id());
    statement.setInt(2, row.groupId());
    statement.setLong(3, row.amountCents());
    statement.setLong(4, row.version());
    statement.setString(5, row.payload());
  }

  void batch(List<Row> rows) throws Exception {
    if (extended != null) {
      extended.batch(rows);
      return;
    }
    if (mongo != null) {
      collection.insertMany(rows.stream().map(Database::doc).toList());
      return;
    }
    if (redis != null) {
      try (var pipeline = redis.pipelined()) {
        for (var row : rows) pipeline.hset(prefix + row.id(), fields(row));
        pipeline.sync();
      }
      return;
    }
    if (http != null) {
      var body = new StringBuilder();
      for (var row : rows)
        body.append(
                Worker.JSON.writeValueAsString(
                    map(
                        "id",
                        row.id(),
                        "group_id",
                        row.groupId(),
                        "amount_cents",
                        row.amountCents(),
                        "version",
                        row.version(),
                        "payload",
                        row.payload())))
            .append('\n');
      query("INSERT INTO records FORMAT JSONEachRow", body.toString());
      return;
    }
    if (target.equals("postgreSql")) {
      var copy =
          sql.unwrap(PGConnection.class)
              .getCopyAPI()
              .copyIn(
                  "COPY records(id,group_id,amount_cents,version,payload) FROM STDIN (FORMAT"
                      + " BINARY)");
      try {
        var buffer = new ByteArrayOutputStream();
        var output = new DataOutputStream(buffer);
        output.write(new byte[] {'P', 'G', 'C', 'O', 'P', 'Y', '\n', (byte) 255, '\r', '\n', 0});
        output.writeInt(0);
        output.writeInt(0);
        for (var row : rows) {
          output.writeShort(5);
          output.writeInt(8);
          output.writeLong(row.id());
          output.writeInt(4);
          output.writeInt(row.groupId());
          output.writeInt(8);
          output.writeLong(row.amountCents());
          output.writeInt(8);
          output.writeLong(row.version());
          byte[] payload = row.payload().getBytes(StandardCharsets.UTF_8);
          output.writeInt(payload.length);
          output.write(payload);
          if (buffer.size() > 65536) {
            copy.writeToCopy(buffer.toByteArray(), 0, buffer.size());
            buffer.reset();
          }
        }
        output.writeShort(-1);
        copy.writeToCopy(buffer.toByteArray(), 0, buffer.size());
        copy.endCopy();
      } catch (Exception error) {
        if (copy.isActive()) copy.cancelCopy();
        throw error;
      }
      return;
    }
    if (target.equals("sqlite")) {
      sql.setAutoCommit(false);
      try (var statement = sql.prepareStatement("INSERT INTO records VALUES(?,?,?,?,?)")) {
        for (var row : rows) {
          bind(statement, row);
          statement.executeUpdate();
        }
        sql.commit();
      } catch (Exception error) {
        sql.rollback();
        throw error;
      } finally {
        sql.setAutoCommit(true);
      }
      return;
    }
    String query =
        "INSERT INTO records VALUES "
            + String.join(",", Collections.nCopies(rows.size(), "(?,?,?,?,?)"));
    try (var statement = sql.prepareStatement(query)) {
      int index = 1;
      for (var row : rows) {
        statement.setLong(index++, row.id());
        statement.setInt(index++, row.groupId());
        statement.setLong(index++, row.amountCents());
        statement.setLong(index++, row.version());
        statement.setString(index++, row.payload());
      }
      statement.executeUpdate();
    }
  }

  void prepare() throws Exception {
    if (extended != null) {
      extended.prepare();
      return;
    }
    if (mongo != null) collection.createIndex(new Document("group_id", 1));
    else if (sql != null) {
      command("CREATE INDEX records_group_id ON records(group_id)");
      command(
          target.equals("mySql") || target.equals("mariaDb")
              ? "ANALYZE TABLE records"
              : "ANALYZE records");
    }
  }

  long[] digest() throws Exception {
    if (extended != null) {
      return extended.digest();
    }
    if (mongo != null) {
      var result =
          collection
              .aggregate(
                  List.of(
                      new Document(
                          "$group",
                          new Document("_id", null)
                              .append("n", new Document("$sum", 1))
                              .append("ids", new Document("$sum", "$_id"))
                              .append("amount", new Document("$sum", "$amount_cents")))))
              .first();
      return result == null
          ? new long[3]
          : new long[] {
            ((Number) result.get("n")).longValue(),
            ((Number) result.get("ids")).longValue(),
            ((Number) result.get("amount")).longValue()
          };
    }
    if (redis != null) {
      String script =
          "local r=redis.call('SCAN',ARGV[1],'MATCH',ARGV[2]..'*','COUNT',1000); local n=0; local"
              + " ids=0; local a=0; for _,k in ipairs(r[2]) do n=n+1;"
              + " ids=ids+tonumber(string.sub(k,string.len(ARGV[2])+1));"
              + " a=a+tonumber(redis.call('HGET',k,'amount_cents')); end; return {r[1],n,ids,a}";
      String cursor = "0";
      long[] sums = new long[3];
      do {
        var result = (List<?>) redis.eval(script, List.of(), List.of(cursor, prefix));
        cursor = result.get(0).toString();
        for (int i = 0; i < 3; i++) sums[i] += ((Number) result.get(i + 1)).longValue();
      } while (!cursor.equals("0"));
      return sums;
    }
    if (http != null) {
      var fields =
          query("SELECT count(),sum(id),sum(amount_cents) FROM records FORMAT TabSeparated", "")
              .trim()
              .split("\t");
      return new long[] {
        Long.parseLong(fields[0]), Long.parseLong(fields[1]), Long.parseLong(fields[2])
      };
    }
    try (var statement = sql.createStatement();
        var rows =
            statement.executeQuery(
                "SELECT COUNT(*),COALESCE(SUM(id),0),COALESCE(SUM(amount_cents),0) FROM records")) {
      rows.next();
      return new long[] {rows.getLong(1), rows.getLong(2), rows.getLong(3)};
    }
  }

  long count() throws Exception {
    if (extended != null) {
      return extended.count();
    }
    if (mongo != null) return collection.countDocuments();
    if (redis != null) return redis.dbSize();
    if (http != null)
      return Long.parseLong(query("SELECT count() FROM records FORMAT TabSeparated", "").trim());
    try (var statement = sql.createStatement();
        var rows = statement.executeQuery("SELECT COUNT(*) FROM records")) {
      rows.next();
      return rows.getLong(1);
    }
  }

  Object metadata() throws Exception {
    if (extended != null) {
      return extended.metadata();
    }
    var settings = new LinkedHashMap<String, String>();
    settings.put("build", "Release");
    settings.put("generator", GENERATOR);
    settings.put("connections", "one connection per logical client; Java virtual threads");
    String version, driver;
    if (sql != null) {
      var info = sql.getMetaData();
      driver = info.getDriverName() + " / " + info.getDriverVersion();
      try (var statement = sql.createStatement();
          var result =
              statement.executeQuery(
                  target.equals("sqlite") ? "SELECT sqlite_version()" : "SELECT VERSION()")) {
        result.next();
        version = result.getString(1);
      }
      String[] keys =
          target.equals("sqlite")
              ? new String[] {"journal_mode", "synchronous", "cache_size"}
              : target.equals("postgreSql")
                  ? new String[] {
                    "synchronous_commit", "fsync", "shared_buffers", "max_connections"
                  }
                  : new String[] {
                    "innodb_flush_log_at_trx_commit", "innodb_buffer_pool_size", "max_connections"
                  };
      for (var key : keys) {
        try (var statement = sql.createStatement();
            var result =
                statement.executeQuery(
                    target.equals("sqlite")
                        ? "PRAGMA " + key
                        : target.equals("postgreSql") ? "SHOW " + key : "SELECT @@GLOBAL." + key)) {
          result.next();
          settings.put(key, result.getString(1));
        }
      }
      settings.put(
          "batch_transport",
          target.equals("postgreSql")
              ? "binary COPY"
              : target.equals("sqlite")
                  ? "prepared INSERT in transaction"
                  : "multi-row parameterized INSERT");
      settings.put("prepared_queries", "true");
    } else if (mongo != null) {
      version = mongoDb.runCommand(new Document("buildInfo", 1)).getString("version");
      driver = "mongodb-driver-sync 5.5.1";
      settings.put("write_concern", "w=1,j=true");
      settings.put("retry_writes", "false");
      settings.put("topology", "standalone");
    } else if (redis != null) {
      version = "unknown";
      for (String line : redis.info("server").split("\n")) {
        if (line.startsWith("redis_version:") || line.startsWith("valkey_version:"))
          version = line.substring(line.indexOf(':') + 1).trim();
      }
      driver = "Jedis 6.0.0";
      settings.put("durability_profile", "memory cache: AOF and RDB disabled");
      settings.put("batch_transport", "pipelined HSET; non-atomic");
    } else {
      version = query("SELECT version() FORMAT TabSeparated", "").trim();
      driver = "JDK java.net.http.HttpClient";
      settings.put("transport", "HTTP / JSONEachRow / uncompressed");
      settings.put("insert", "synchronous HTTP; async_insert=0");
      settings.put("table", "MergeTree ORDER BY id; no unique constraint");
    }
    return map(
        "engine",
        "java",
        "runtime",
        System.getProperty("java.runtime.version") + " / " + System.getProperty("java.vm.name"),
        "driver",
        driver,
        "serverVersion",
        version,
        "settings",
        settings);
  }

  long execute(String operation, long key, Row row, List<Row> batch) throws Exception {
    if (extended != null) {
      return extended.execute(operation, key, row, batch);
    }
    if (operation.equals("BatchInsert")) {
      batch(batch);
      return batch.size();
    }
    if (mongo != null) {
      var filter = new Document("_id", key);
      switch (operation) {
        case "PointRead" -> {
          var result = collection.find(filter).maxTime(10, TimeUnit.SECONDS).first();
          if (result == null
              || ((Number) result.get("_id")).longValue() != key
              || !(result.get("payload") instanceof String))
            throw new IOException("Invalid MongoDB row");
          return 1;
        }
        case "RangeRead" -> {
          var rows =
              collection
                  .find(new Document("_id", new Document("$gte", key)))
                  .sort(new Document("_id", 1))
                  .limit(100)
                  .maxTime(10, TimeUnit.SECONDS)
                  .into(new ArrayList<>());
          if (rows.isEmpty()) throw new IOException("Empty range");
          return rows.size();
        }
        case "Aggregate" -> {
          var rows =
              collection
                  .aggregate(
                      List.of(
                          new Document("$match", new Document("group_id", (key - 1) % 100)),
                          new Document(
                              "$group",
                              new Document("_id", "$group_id")
                                  .append("n", new Document("$sum", 1))
                                  .append("amount", new Document("$sum", "$amount_cents")))))
                  .maxTime(10, TimeUnit.SECONDS)
                  .into(new ArrayList<>());
          if (rows.isEmpty()) throw new IOException("Empty aggregate");
          return rows.size();
        }
        case "Insert" -> {
          collection.insertOne(doc(row));
          return 1;
        }
        case "Update" -> {
          if (collection
                  .updateOne(filter, new Document("$inc", new Document("version", 1L)))
                  .getMatchedCount()
              != 1) throw new IOException("Missing update");
          return 1;
        }
        case "Delete" -> {
          if (collection.deleteOne(filter).getDeletedCount() != 1)
            throw new IOException("Missing delete");
          return 1;
        }
      }
    }
    if (redis != null) {
      String name = prefix + key;
      switch (operation) {
        case "PointRead" -> {
          if (redis.hgetAll(name).size() != 4) throw new IOException("Incomplete Redis hash");
          return 1;
        }
        case "Update" -> {
          redis.hincrBy(name, "version", 1);
          return 1;
        }
        case "Insert" -> {
          redis.hset(prefix + row.id(), fields(row));
          return 1;
        }
        case "Delete" -> {
          if (redis.del(name) != 1) throw new IOException("Missing delete");
          return 1;
        }
      }
    }
    if (http != null) {
      String query =
          switch (operation) {
            case "RangeRead" ->
                "SELECT id,group_id,amount_cents,version,payload FROM records WHERE id>="
                    + key
                    + " ORDER BY id LIMIT 100";
            case "Aggregate" ->
                "SELECT group_id,count(),sum(amount_cents) FROM records WHERE group_id="
                    + (key - 1) % 100
                    + " GROUP BY group_id";
            default ->
                "SELECT count(),sum(amount_cents),avg(amount_cents) FROM records WHERE id>="
                    + key
                    + " AND id<"
                    + (key + Math.max(100, run.path("rows").asLong() / 100));
          };
      long count = 0;
      for (var line :
          query(query + " SETTINGS max_execution_time=10 FORMAT JSONEachRow", "").split("\n")) {
        if (line.isBlank()) continue;
        Worker.JSON.readTree(line);
        count++;
      }
      if (count == 0) throw new IOException("Empty ClickHouse response");
      return count;
    }
    String query =
        switch (operation) {
          case "PointRead" ->
              "SELECT id,group_id,amount_cents,version,payload FROM records WHERE id=?";
          case "RangeRead" ->
              "SELECT id,group_id,amount_cents,version,payload FROM records WHERE id>=? ORDER BY id"
                  + " LIMIT 100";
          case "Aggregate" ->
              "SELECT group_id,COUNT(*),SUM(amount_cents) FROM records WHERE group_id=? GROUP BY"
                  + " group_id";
          case "Update" -> "UPDATE records SET version=version+1 WHERE id=?";
          case "Delete" -> "DELETE FROM records WHERE id=?";
          case "Insert" -> "INSERT INTO records VALUES(?,?,?,?,?)";
          default -> throw new IllegalArgumentException(operation);
        };
    var statement = statements.get(operation);
    if (statement == null) {
      statement = sql.prepareStatement(query);
      statement.setQueryTimeout(10);
      statements.put(operation, statement);
    }
    if (operation.equals("Insert")) bind(statement, row);
    else statement.setLong(1, operation.equals("Aggregate") ? (key - 1) % 100 : key);
    if (List.of("PointRead", "RangeRead", "Aggregate").contains(operation)) {
      try (var rows = statement.executeQuery()) {
        long count = 0;
        while (rows.next()) {
          if (operation.equals("Aggregate")) {
            rows.getLong(2);
            rows.getLong(3);
          } else {
            long id = rows.getLong(1);
            rows.getString(5);
            if (operation.equals("PointRead") && id != key) throw new IOException("Invalid ID");
          }
          count++;
        }
        if (count == 0 || operation.equals("PointRead") && count != 1)
          throw new IOException("Invalid SQL response");
        return count;
      }
    }
    long affected = statement.executeUpdate();
    if (affected != 1) throw new IOException("Write did not affect one record");
    return affected;
  }

  @Override
  public void close() {
    if (extended != null) {
      extended.close();
      return;
    }
    for (var statement : statements.values())
      try {
        statement.close();
      } catch (Exception ignored) {
      }
    try {
      if (sql != null) sql.close();
    } catch (Exception ignored) {
    }
    if (mongo != null) mongo.close();
    if (redis != null) redis.close();
    if (http != null) http.close();
  }
}
