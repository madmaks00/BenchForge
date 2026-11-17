package dev.benchforge;

import static dev.benchforge.Portable.*;

import com.fasterxml.jackson.databind.JsonNode;
import java.io.*;
import java.net.*;
import java.net.http.*;
import java.sql.*;
import java.time.Duration;
import java.util.*;

/** Native JDBC / HTTP adapters. Each logical client owns its connection. */
final class ExtendedDatabase implements AutoCloseable {
  static final Set<String> TARGETS =
      Set.of("sqlServer", "duckDb", "oracle", "cockroachDb", "elasticsearch", "openSearch");
  final JsonNode run, ep;
  final String target;
  Connection sql;
  HttpClient http;
  final Map<String, PreparedStatement> statements = new HashMap<>();

  ExtendedDatabase(JsonNode run) throws Exception {
    this.run = run;
    ep = run.path("endpoint");
    target = run.path("target").asText();
    String address = ep.path("host").asText() + ":" + ep.path("port").asInt();
    String database = ep.path("database").asText();
    if (!target.equals("duckDb") && !database.matches("[A-Za-z][A-Za-z0-9_]*"))
      throw new IOException("Invalid owned database name");
    Properties p = new Properties();
    p.setProperty("user", ep.path("username").asText());
    p.setProperty("password", ep.path("password").asText());
    try {
      switch (target) {
        case "duckDb" -> {
          p.clear();
          p.setProperty("memory_limit", run.path("config").path("memoryMb").asInt(512) + "MB");
          p.setProperty(
              "threads",
              "" + Math.max(1, (int) Math.ceil(run.path("config").path("cpuLimit").asDouble(2))));
          sql = DriverManager.getConnection("jdbc:duckdb:" + ep.path("filePath").asText(), p);
        }
        case "sqlServer" -> {
          String url =
              "jdbc:sqlserver://"
                  + address
                  + ";encrypt=false;loginTimeout=15;socketTimeout=600000;databaseName=";
          try (var admin = DriverManager.getConnection(url + "master", p);
              var s = admin.createStatement()) {
            s.execute("IF DB_ID('" + database + "') IS NULL CREATE DATABASE [" + database + "]");
            s.execute("ALTER DATABASE [" + database + "] SET RECOVERY SIMPLE");
            s.execute("ALTER DATABASE [" + database + "] SET DELAYED_DURABILITY=DISABLED");
          }
          sql = DriverManager.getConnection(url + database, p);
          command("SET QUOTED_IDENTIFIER ON; SET NOCOUNT OFF; SET XACT_ABORT ON");
        }
        case "oracle" -> {
          String url = "jdbc:oracle:thin:@//" + address + "/" + database;
          var adminProperties = new Properties();
          adminProperties.putAll(p);
          adminProperties.setProperty("user", "SYSTEM");
          try (var admin = DriverManager.getConnection(url, adminProperties);
              var s = admin.createStatement()) {
            boolean missing;
            try (var r = s.executeQuery("SELECT COUNT(*) FROM all_users WHERE username='DBLAB'")) {
              r.next();
              missing = r.getInt(1) == 0;
            }
            if (missing) {
              s.execute(
                  "CREATE USER DBLAB IDENTIFIED BY \""
                      + ep.path("password").asText().replace("\"", "\"\"")
                      + "\"");
              s.execute("GRANT CREATE SESSION,CREATE TABLE,UNLIMITED TABLESPACE TO DBLAB");
            }
          }
          sql = DriverManager.getConnection(url, p);
        }
        case "cockroachDb" -> {
          p.setProperty("sslmode", "disable");
          p.setProperty("connectTimeout", "15");
          p.setProperty("socketTimeout", "600");
          try (var admin =
                  DriverManager.getConnection("jdbc:postgresql://" + address + "/defaultdb", p);
              var s = admin.createStatement()) {
            s.execute("CREATE DATABASE IF NOT EXISTS \"" + database + "\"");
          }
          sql = DriverManager.getConnection("jdbc:postgresql://" + address + "/" + database, p);
        }
        default -> {
          http = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(15)).build();
          request("GET", "/", null, false);
          request("GET", "/_cluster/health?wait_for_status=yellow&timeout=5s", null, false);
        }
      }
    } catch (Exception e) {
      close();
      throw e;
    }
  }

  void command(String text) throws SQLException {
    try (var s = sql.createStatement()) {
      s.setQueryTimeout(600);
      s.execute(text);
    }
  }

  long[] scalars(String text) throws SQLException {
    try (var s = sql.createStatement();
        var r = s.executeQuery(text)) {
      if (!r.next()) throw new SQLException("Missing scalar row");
      long[] v = new long[r.getMetaData().getColumnCount()];
      for (int i = 0; i < v.length; i++) v[i] = r.getLong(i + 1);
      return v;
    }
  }

  JsonNode request(String method, String path, Object body, boolean missing) throws Exception {
    String text =
        body == null ? "" : body instanceof String s ? s : Worker.JSON.writeValueAsString(body);
    var b =
        HttpRequest.newBuilder(
                URI.create(
                    "http://" + ep.path("host").asText() + ":" + ep.path("port").asInt() + path))
            .timeout(Duration.ofSeconds(600))
            .header(
                "Content-Type",
                path.startsWith("/_bulk") ? "application/x-ndjson" : "application/json");
    var response =
        http.send(
            b.method(
                    method,
                    text.isEmpty()
                        ? HttpRequest.BodyPublishers.noBody()
                        : HttpRequest.BodyPublishers.ofString(text))
                .build(),
            HttpResponse.BodyHandlers.ofString());
    if (missing && response.statusCode() == 404) return Worker.JSON.createObjectNode();
    if (response.statusCode() < 200 || response.statusCode() >= 300)
      throw new IOException(
          "Search HTTP "
              + response.statusCode()
              + ": "
              + response.body().substring(0, Math.min(350, response.body().length())));
    var result = Worker.JSON.readTree(response.body());
    if (result.path("timed_out").asBoolean() || result.path("_shards").path("failed").asInt() > 0)
      throw new IOException("Search returned incomplete shards");
    return result;
  }

  Map<String, Object> document(Row r) {
    var text = new StringBuilder("benchforge group" + r.groupId());
    for (int i = 0; i < r.payload().length(); i += 16)
      text.append(' ').append(r.payload(), i, Math.min(i + 16, r.payload().length()));
    return map(
        "id",
        r.id(),
        "group_id",
        r.groupId(),
        "amount_cents",
        r.amountCents(),
        "version",
        r.version(),
        "payload",
        r.payload(),
        "search_text",
        text.toString());
  }

  void reset() throws Exception {
    if (http != null) {
      request("DELETE", "/records", null, true);
      request(
          "PUT",
          "/records",
          map(
              "settings",
              map(
                  "number_of_shards",
                  1,
                  "number_of_replicas",
                  0,
                  "refresh_interval",
                  "-1",
                  "translog.durability",
                  "request"),
              "mappings",
              map(
                  "dynamic",
                  "strict",
                  "properties",
                  map(
                      "id",
                      map("type", "long"),
                      "group_id",
                      map("type", "integer"),
                      "amount_cents",
                      map("type", "long"),
                      "version",
                      map("type", "long"),
                      "payload",
                      map("type", "keyword", "index", false, "doc_values", false),
                      "search_text",
                      map("type", "text", "analyzer", "standard")))),
          false);
      return;
    }
    if (target.equals("oracle")) {
      command(
          "BEGIN EXECUTE IMMEDIATE 'DROP TABLE records PURGE'; EXCEPTION WHEN OTHERS THEN IF"
              + " SQLCODE != -942 THEN RAISE; END IF; END;");
      command(
          "CREATE TABLE records(id NUMBER(19) PRIMARY KEY,group_id NUMBER(10) NOT NULL,amount_cents"
              + " NUMBER(19) NOT NULL,version NUMBER(19) NOT NULL,payload CLOB NOT NULL)");
    } else {
      command("DROP TABLE IF EXISTS records");
      command(
          "CREATE TABLE records(id BIGINT PRIMARY KEY,group_id INTEGER NOT NULL,amount_cents BIGINT"
              + " NOT NULL,version BIGINT NOT NULL,payload "
              + (target.equals("sqlServer") ? "NVARCHAR(MAX)" : "TEXT")
              + " NOT NULL)");
    }
  }

  void bind(PreparedStatement s, Row r) throws SQLException {
    s.setLong(1, r.id());
    s.setInt(2, r.groupId());
    s.setLong(3, r.amountCents());
    s.setLong(4, r.version());
    if (target.equals("oracle")) s.setClob(5, new StringReader(r.payload()), r.payload().length());
    else s.setString(5, r.payload());
  }

  void batch(List<Row> rows) throws Exception {
    if (http != null) {
      var body = new StringBuilder();
      for (var r : rows)
        body.append(
                Worker.JSON.writeValueAsString(
                    map("create", map("_index", "records", "_id", "" + r.id()))))
            .append('\n')
            .append(Worker.JSON.writeValueAsString(document(r)))
            .append('\n');
      var result = request("POST", "/_bulk?refresh=false", body.toString(), false);
      if (result.path("errors").asBoolean() || result.path("items").size() != rows.size())
        throw new IOException("Bulk insert failed");
      for (var item : result.path("items"))
        if (item.path("create").path("status").asInt() != 201 || item.path("create").has("error"))
          throw new IOException("Bulk item failed");
      return;
    }
    sql.setAutoCommit(false);
    try (var s =
        sql.prepareStatement(
            "INSERT INTO records(id,group_id,amount_cents,version,payload) VALUES(?,?,?,?,?)")) {
      for (var r : rows) {
        bind(s, r);
        if (s.executeUpdate() != 1) throw new SQLException("Insert did not affect one record");
      }
      sql.commit();
    } catch (Exception e) {
      sql.rollback();
      throw e;
    } finally {
      sql.setAutoCommit(true);
    }
  }

  void prepare() throws Exception {
    if (http != null) {
      request("PUT", "/records/_settings", map("index", map("refresh_interval", "1s")), false);
      request("POST", "/records/_refresh", null, false);
      return;
    }
    command("CREATE INDEX records_group_id ON records(group_id)");
    command(
        switch (target) {
          case "oracle" -> "BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER,'RECORDS'); END;";
          case "sqlServer" -> "UPDATE STATISTICS records WITH FULLSCAN";
          case "cockroachDb" -> "CREATE STATISTICS records_stats FROM records";
          default -> "ANALYZE records";
        });
  }

  long[] digest() throws Exception {
    if (http == null)
      return scalars(
          "SELECT COUNT(*),COALESCE(SUM(id),0),COALESCE(SUM(amount_cents),0) FROM records");
    request("POST", "/records/_refresh", null, false);
    var r =
        request(
            "POST",
            "/records/_search",
            map(
                "size",
                0,
                "track_total_hits",
                true,
                "aggs",
                map(
                    "ids",
                    map("sum", map("field", "id")),
                    "amount",
                    map("sum", map("field", "amount_cents")))),
            false);
    return new long[] {
      r.path("hits").path("total").path("value").asLong(),
      r.path("aggregations").path("ids").path("value").asLong(),
      r.path("aggregations").path("amount").path("value").asLong()
    };
  }

  long count() throws Exception {
    if (http == null) return scalars("SELECT COUNT(*) FROM records")[0];
    request("POST", "/records/_refresh", null, false);
    return request("GET", "/records/_count", null, false).path("count").asLong();
  }

  Object metadata() throws Exception {
    var settings =
        map(
            "generator",
            GENERATOR,
            "connections",
            "one native connection/client; Java virtual threads",
            "batch_transport",
            http == null
                ? "parameterized row INSERTs in transaction"
                : "NDJSON bulk create; non-atomic; each item checked",
            "prepared_queries",
            http == null ? "JDBC PreparedStatement" : "HTTP JSON",
            "retries",
            "none");
    String version, driver;
    if (http != null) {
      version = request("GET", "/", null, false).path("version").path("number").asText();
      driver = "JDK java.net.http / JSON";
      settings.put("search_corpus", "payload-terms16-v1");
      settings.put("durability_profile", "translog=request; refresh=1s; 1 primary; 0 replicas");
    } else {
      var m = sql.getMetaData();
      version = m.getDatabaseProductVersion();
      driver = m.getDriverName() + " / " + m.getDriverVersion();
      settings.put(
          "isolation",
          target.equals("cockroachDb") ? "SERIALIZABLE; single-node" : "driver/server default");
    }
    return map(
        "engine",
        "java",
        "runtime",
        System.getProperty("java.runtime.version"),
        "driver",
        driver,
        "serverVersion",
        version,
        "settings",
        settings);
  }

  long execute(String operation, long key, Row row, List<Row> batch) throws Exception {
    if (operation.equals("BatchInsert")) {
      batch(batch);
      return batch.size();
    }
    if (http != null) return search(operation, key, row);
    String text =
        switch (operation) {
          case "PointRead" ->
              "SELECT id,group_id,amount_cents,version,payload FROM records WHERE id=?";
          case "RangeRead" ->
              "SELECT "
                  + (target.equals("sqlServer") ? "TOP (100) " : "")
                  + "id,group_id,amount_cents,version,payload FROM records WHERE id>=? ORDER BY id"
                  + (target.equals("sqlServer")
                      ? ""
                      : target.equals("oracle") ? " FETCH FIRST 100 ROWS ONLY" : " LIMIT 100");
          case "Aggregate" ->
              "SELECT group_id,COUNT(*),SUM(amount_cents) FROM records WHERE group_id=? GROUP BY"
                  + " group_id";
          case "AnalyticalScan" ->
              "SELECT COUNT(*),SUM(amount_cents),AVG(amount_cents) FROM records WHERE id>=? AND"
                  + " id<?";
          case "Update" -> "UPDATE records SET version=version+1 WHERE id=?";
          case "Delete" -> "DELETE FROM records WHERE id=?";
          case "Insert" ->
              "INSERT INTO records(id,group_id,amount_cents,version,payload) VALUES(?,?,?,?,?)";
          default -> throw new IOException("Unsupported SQL operation");
        };
    var s = statements.get(operation);
    if (s == null || s.isClosed()) {
      s = sql.prepareStatement(text);
      s.setQueryTimeout(10);
      statements.put(operation, s);
    }
    if (operation.equals("Insert")) bind(s, row);
    else {
      s.setLong(1, operation.equals("Aggregate") ? (key - 1) % 100 : key);
      if (operation.equals("AnalyticalScan"))
        s.setLong(2, key + Math.max(100, run.path("rows").asLong() / 100));
    }
    if (Set.of("PointRead", "RangeRead", "Aggregate", "AnalyticalScan").contains(operation)) {
      long n = 0, previous = key - 1;
      try (var r = s.executeQuery()) {
        while (r.next()) {
          if (operation.equals("PointRead") || operation.equals("RangeRead")) {
            long id = r.getLong(1);
            String payload = r.getString(5);
            if (id <= previous
                || operation.equals("PointRead") && id != key
                || payload == null
                || payload.length() != run.path("config").path("payloadBytes").asInt(256))
              throw new IOException("Invalid ID or payload");
            previous = id;
          } else {
            r.getLong(1);
            r.getLong(2);
            r.getBigDecimal(3);
          }
          n++;
        }
      }
      if (n == 0 || operation.equals("PointRead") && n != 1)
        throw new IOException("Invalid SQL response");
      return n;
    }
    long affected = s.executeUpdate();
    if (affected != 1) throw new IOException("Write did not affect one record");
    return affected;
  }

  long search(String op, long key, Row row) throws Exception {
    String path = "/records/_doc/" + key;
    if (op.equals("PointRead")) {
      var r = request("GET", path, null, false);
      if (!r.path("found").asBoolean()
          || r.path("_source").path("id").asLong() != key
          || r.path("_source").path("payload").asText().length()
              != run.path("config").path("payloadBytes").asInt(256))
        throw new IOException("Invalid document");
      return 1;
    }
    if (Set.of("Update", "Insert", "Delete").contains(op)) {
      var r =
          switch (op) {
            case "Update" ->
                request(
                    "POST",
                    "/records/_update/" + key + "?refresh=false&retry_on_conflict=0",
                    map("script", map("source", "ctx._source.version += 1", "lang", "painless")),
                    false);
            case "Insert" ->
                request(
                    "PUT", "/records/_create/" + row.id() + "?refresh=false", document(row), false);
            default -> request("DELETE", path + "?refresh=false", null, false);
          };
      if (!r.path("result")
          .asText()
          .equals(
              switch (op) {
                case "Update" -> "updated";
                case "Insert" -> "created";
                default -> "deleted";
              })) throw new IOException("Search write failed");
      return 1;
    }
    if (op.equals("Aggregate")) {
      var r =
          request(
              "POST",
              "/records/_search",
              map(
                  "size",
                  0,
                  "track_total_hits",
                  true,
                  "query",
                  map("term", map("group_id", (key - 1) % 100)),
                  "aggs",
                  map("amount", map("sum", map("field", "amount_cents")))),
              false);
      if (r.path("hits").path("total").path("value").asLong() == 0)
        throw new IOException("Empty aggregate");
      r.path("aggregations").path("amount").path("value").asDouble();
      return 1;
    }
    String term =
        op.equals("FullTextSearch")
            ? Portable.row(
                    key,
                    run.path("config").path("seed").asLong(42),
                    run.path("config").path("payloadBytes").asInt(256))
                .payload()
                .substring(0, 16)
            : null;
    Object body =
        term != null
            ? map("size", 10, "query", map("match", map("search_text", term)))
            : map(
                "size",
                100,
                "query",
                map("range", map("id", map("gte", key))),
                "sort",
                List.of(map("id", "asc")));
    var hits = request("POST", "/records/_search", body, false).path("hits").path("hits");
    if (hits.isEmpty()) throw new IOException("Empty search response");
    long previous = key - 1;
    for (var hit : hits) {
      var r = hit.path("_source");
      long id = r.path("id").asLong();
      if (r.path("payload").asText().length() != run.path("config").path("payloadBytes").asInt(256)
          || term != null
              && !Arrays.asList(r.path("search_text").asText().toLowerCase(Locale.ROOT).split(" "))
                  .contains(term.toLowerCase(Locale.ROOT))
          || term == null && id <= previous) throw new IOException("Invalid search response");
      previous = id;
    }
    return hits.size();
  }

  public void close() {
    for (var s : statements.values())
      try {
        s.close();
      } catch (Exception ignored) {
      }
    try {
      if (sql != null) sql.close();
    } catch (Exception ignored) {
    }
    if (http != null) http.close();
  }
}
