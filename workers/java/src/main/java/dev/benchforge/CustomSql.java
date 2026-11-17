package dev.benchforge;

import static dev.benchforge.Portable.map;
import static dev.benchforge.Worker.*;

import com.fasterxml.jackson.databind.JsonNode;
import java.io.BufferedReader;
import java.math.BigDecimal;
import java.math.RoundingMode;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.sql.*;
import java.time.*;
import java.time.format.DateTimeFormatter;
import java.util.*;
import java.util.concurrent.atomic.AtomicLong;

final class CustomSql {
  record Context(long sequence, int client, long key) {}

  private static final DateTimeFormatter TIMESTAMP =
      DateTimeFormatter.ofPattern("uuuu-MM-dd HH:mm:ss.SSSSSS", Locale.ROOT);
  private final JsonNode program = run.path("sqlProgram");
  private final JsonNode config = run.path("config");
  private final JsonNode workload = config.path("customWorkload");
  private final String target = run.path("target").asText();
  private final long rows = run.path("rows").asLong(), seed = config.path("seed").longValue();
  private final Map<String, Long> tableRows = new TreeMap<>(String.CASE_INSENSITIVE_ORDER);
  private final Map<String, Reservoir> samples = new HashMap<>();
  private final Map<String, Long> completedByOperation = new TreeMap<>(String.CASE_INSENSITIVE_ORDER);
  private final AtomicLong sequence = new AtomicLong();
  private long completed;

  CustomSql() {
    if (program.isMissingNode() || program.isNull())
      throw new IllegalArgumentException("Compiled SQL plan missing. Update BenchForge.");
    for (var table : program.path("datasets"))
      tableRows.put(table.path("definition").path("name").asText(), table.path("rows").asLong());
  }

  private static long seedFor(long seed, String name) {
    long hash = 0xcbf29ce484222325L;
    for (byte value : name.getBytes(StandardCharsets.UTF_8))
      hash = (hash ^ (value & 0xffL)) * 0x100000001b3L;
    return seed ^ hash;
  }

  private static String sampleKey(JsonNode definition) {
    return definition.path("reference").asText() + "/" + definition.path("sampleColumn").asText("id")
        + "/" + definition.path("sampleSize").asInt(10000);
  }

  private final class Reservoir {
    final List<String> values = new ArrayList<>();
    final int limit, column;
    final Portable.Random random;
    long seen;
    Reservoir(JsonNode definition, int column) {
      limit = definition.path("sampleSize").asInt(10000);
      this.column = column;
      random = new Portable.Random(seedFor(seed, sampleKey(definition)));
    }

    void add(String value) {
      if (value == null) return;
      seen++;
      if (values.size() < limit) values.add(value);
      else {
        long slot = Long.remainderUnsigned(random.next(), seen);
        if (slot < limit) values.set((int) slot, value);
      }
    }
  }

  private static long integer(Portable.Random rng, long min, long max) {
    long width = max - min + 1;
    return min + (width == 0 ? rng.next() : Long.remainderUnsigned(rng.next(), width));
  }

  private String generate(JsonNode field, Context context, Portable.Random rng) {
    double nullPercent = field.path("nullPercent").asDouble();
    if (nullPercent > 0 && Long.remainderUnsigned(rng.next(), 1000000) < nullPercent * 10000) return null;
    var min = field.path("min");
    var max = field.path("max");
    Object value = switch (field.path("generator").asText("sequence")) {
      case "sequence" -> Math.addExact(min.asLong(1), context.sequence() - 1);
      case "integer" -> integer(rng, min.asLong(1), max.asLong(1000));
      case "decimal" -> {
        var lo = min.isNumber() ? min.decimalValue() : BigDecimal.ONE;
        var hi = max.isNumber() ? max.decimalValue() : BigDecimal.valueOf(1000);
        yield lo.add(hi.subtract(lo).multiply(BigDecimal.valueOf(rng.next() >>> 11))
            .divide(BigDecimal.valueOf(9007199254740992L)))
            .setScale(field.path("scale").asInt(2), RoundingMode.HALF_EVEN);
      }
      case "text" -> {
        var out = new StringBuilder(field.path("prefix").asText()
            .replace("{row}", Long.toString(context.sequence()))
            .replace("{sequence}", Long.toString(context.sequence()))
            .replace("{key}", Long.toString(context.key()))
            .replace("{client}", Integer.toString(context.client())));
        for (int i = 0; i < field.path("length").asInt(32); i++)
          out.append(Portable.ALPHABET.charAt((int) Long.remainderUnsigned(rng.next(), 62)));
        yield out.toString();
      }
      case "choice" -> {
        var choices = field.path("choices");
        yield choices.get((int) Long.remainderUnsigned(rng.next(), choices.size())).asText();
      }
      case "reference" -> integer(rng, 1, tableRows.get(field.path("reference").asText()));
      case "guid" -> {
        byte[] bytes = ByteBuffer.allocate(16).order(ByteOrder.LITTLE_ENDIAN)
            .putLong(rng.next()).putLong(rng.next()).array();
        if (target.equals("oracle")) yield HexFormat.of().formatHex(bytes);
        ByteBuffer buffer = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN);
        yield "%08x-%04x-%04x-%s-%s".formatted(buffer.getInt(), buffer.getShort() & 0xffff,
            buffer.getShort() & 0xffff, HexFormat.of().formatHex(bytes, 8, 10),
            HexFormat.of().formatHex(bytes, 10, 16));
      }
      case "timestamp" -> LocalDateTime.ofEpochSecond(integer(rng, min.asLong(1), max.asLong(1000)), 0, ZoneOffset.UTC);
      case "boolean" -> (rng.next() & 1) == 0;
      case "constant" -> field.hasNonNull("constant") ? field.get("constant").asText() : null;
      case "key" -> context.key();
      case "client" -> context.client();
      case "sample" -> {
        var source = samples.get(sampleKey(field));
        if (source == null || source.values.isEmpty()) throw new IllegalArgumentException("No non-null sample values");
        yield source.values.get((int) Long.remainderUnsigned(rng.next(), source.values.size()));
      }
      default -> throw new IllegalArgumentException("Unknown SQL value generator");
    };
    return typedText(value, field.path("type").asText("int64"));
  }

  private String typedText(Object value, String type) {
    if (value == null) return null;
    String text = value.toString();
    return switch (type) {
      case "int64" -> Long.toString(new BigDecimal(text).setScale(0, RoundingMode.HALF_EVEN).longValueExact());
      case "int32" -> Integer.toString(new BigDecimal(text).setScale(0, RoundingMode.HALF_EVEN).intValueExact());
      case "decimal" -> new BigDecimal(text).toPlainString();
      case "double" -> Double.toString(Double.parseDouble(text));
      case "boolean" -> {
        if (text.equals("1") || text.equalsIgnoreCase("true")) yield "1";
        if (text.equals("0") || text.equalsIgnoreCase("false")) yield "0";
        throw new IllegalArgumentException("Invalid boolean: " + text);
      }
      case "dateTime" -> {
        LocalDateTime time;
        if (value instanceof LocalDateTime local) time = local;
        else if (value instanceof Timestamp timestamp) time = timestamp.toLocalDateTime();
        else {
          String iso = text.replace(' ', 'T');
          try { time = OffsetDateTime.parse(iso).withOffsetSameInstant(ZoneOffset.UTC).toLocalDateTime(); }
          catch (java.time.format.DateTimeParseException e) { time = LocalDateTime.parse(iso); }
        }
        yield TIMESTAMP.format(time);
      }
      case "guid" -> {
        if (target.equals("oracle")) {
          if (value instanceof byte[] bytes) yield HexFormat.of().formatHex(bytes);
          if (text.matches("[0-9a-fA-F]{32}")) yield text;
          UUID guid = UUID.fromString(text);
          long high = guid.getMostSignificantBits();
          yield HexFormat.of().formatHex(ByteBuffer.allocate(16).order(ByteOrder.LITTLE_ENDIAN)
              .putInt((int)(high >>> 32)).putShort((short)(high >>> 16)).putShort((short)high)
              .order(ByteOrder.BIG_ENDIAN).putLong(guid.getLeastSignificantBits()).array());
        }
        yield UUID.fromString(text).toString();
      }
      default -> text;
    };
  }

  private String builtin(String name, Context ctx) {
    String key = name.toLowerCase(Locale.ROOT);
    long value = switch (key) {
      case "key" -> ctx.key();
      case "sequence" -> Math.addExact(rows, ctx.sequence());
      case "client" -> ctx.client();
      case "baserows" -> rows;
      case "seed" -> seed;
      case "completed" -> completed;
      default -> {
        if (key.startsWith("rows_") && tableRows.containsKey(key.substring(5))) yield tableRows.get(key.substring(5));
        if (key.startsWith("completed_")) yield completedByOperation.getOrDefault(key.substring(10), 0L);
        throw new IllegalArgumentException("Unknown SQL parameter @" + name);
      }
    };
    return Long.toString(value);
  }

  private Connection connection(Database db) throws SQLException {
    Connection conn = db.extended == null ? db.sql : db.extended.sql;
    if (conn == null) throw new IllegalArgumentException("The selected target has no JDBC SQL adapter");
    if (target.equals("sqlite")) exec(conn, "PRAGMA foreign_keys=ON");
    if (target.equals("oracle")) exec(conn, "ALTER SESSION SET NLS_NUMERIC_CHARACTERS = '.,'");
    return conn;
  }

  private static void exec(Connection conn, String sql) throws SQLException {
    try (var stmt = conn.createStatement()) {
      stmt.setQueryTimeout(600);
      stmt.execute(sql);
    }
  }

  private PreparedStatement prepare(Connection conn, JsonNode command, int seconds) throws SQLException {
    var prepared = conn.prepareStatement(command.path("sql").asText());
    try {
      prepared.setQueryTimeout(seconds);
      return prepared;
    } catch (SQLException e) {
      prepared.close();
      throw e;
    }
  }

  private int commandTimeout() { return workload.path("commandTimeoutSeconds").asInt(10); }

  private static void bind(PreparedStatement stmt, List<String> values) throws SQLException {
    for (int i = 0; i < values.size(); i++) stmt.setString(i + 1, values.get(i));
  }

  record Answer(Object scalar, long rows) {}

  private static Answer execute(PreparedStatement stmt, String result) throws SQLException {
    if (result.equals("nonQuery")) return new Answer(null, Math.max(0, stmt.executeUpdate()));
    Object scalar = null;
    long rows = 0;
    try (var resultSet = stmt.executeQuery()) {
      if (result.equals("scalar")) {
        if (resultSet.next()) scalar = resultSet.getObject(1);
        return new Answer(scalar, scalar == null ? 0 : 1);
      }
      int columns = resultSet.getMetaData().getColumnCount();
      while (resultSet.next()) {
        for (int column = 1; column <= columns; column++) resultSet.getObject(column);
        rows++;
      }
    }
    return new Answer(null, rows);
  }

  private List<String> arguments(JsonNode command, Map<String, String> generated, Context ctx) {
    var args = new ArrayList<String>();
    for (var parameter : command.path("names")) {
      String name = parameter.asText();
      args.add(generated.containsKey(name) ? generated.get(name) : builtin(name, ctx));
    }
    return args;
  }

  private void checks(Connection conn, boolean before) throws SQLException {
    for (var spec : program.path("checks")) {
      var check = spec.path("definition");
      if (!check.path(before ? "before" : "after").asBoolean(true)) continue;
      var command = spec.path("command");
      try (var stmt = prepare(conn, command, commandTimeout())) {
        bind(stmt, arguments(command, Map.of(), new Context(0, 0, 0)));
        Object value = execute(stmt, "scalar").scalar();
        if (value == null) throw new SQLException("Integrity check returned NULL: " + check.path("name").asText());
        String expected = check.path("expected").asText();
        var right = expected.equals("baseRows") ? BigDecimal.valueOf(rows) :
            expected.startsWith("rows:") ? BigDecimal.valueOf(tableRows.get(expected.substring(5))) : new BigDecimal(expected);
        int difference = new BigDecimal(value.toString()).compareTo(right);
        boolean ok = switch (check.path("comparison").asText("equal")) {
          case "atLeast" -> difference >= 0;
          case "atMost" -> difference <= 0;
          default -> difference == 0;
        };
        if (!ok) throw new SQLException("Integrity check failed: " + check.path("name").asText());
      }
    }
  }

  private long schema(Connection conn) throws Exception {
    sequence.set(0);
    completed = 0;
    completedByOperation.clear();
    samples.clear();
    if (target.equals("sqlite")) exec(conn, "PRAGMA foreign_keys=OFF");
    try {
      for (String name : List.of("drop", "schema")) for (var sql : program.path(name)) {
        check();
        exec(conn, sql.asText());
      }
    } finally {
      if (target.equals("sqlite")) exec(conn, "PRAGMA foreign_keys=ON");
    }
    return 0;
  }

  private long load(Connection conn) throws Exception {
    long loaded = 0;
    int batch = config.path("batchSize").asInt(1000);
    for (var table : program.path("datasets")) {
      var definition = table.path("definition");
      var columns = definition.path("columns");
      String tableName = definition.path("name").asText();
      var reservoirs = new ArrayList<Reservoir>();
      for (var op : program.path("operations")) for (var parameter : op.path("definition").path("parameters")) {
        if (!parameter.path("generator").asText().equals("sample") || !parameter.path("reference").asText().equals(tableName)) continue;
        String key = sampleKey(parameter);
        if (samples.containsKey(key)) continue;
        for (int i = 0; i < columns.size(); i++) if (columns.get(i).path("name").asText().equals(parameter.path("sampleColumn").asText("id"))) {
          var reservoir = new Reservoir(parameter, i);
          samples.put(key, reservoir);
          reservoirs.add(reservoir);
          break;
        }
      }
      var input = table.path("inputFile");
      long tableSeed = Long.parseUnsignedLong(table.path("seed").asText());
      try (BufferedReader reader = input.isTextual() ? Files.newBufferedReader(Path.of(input.asText())) : null;
           var stmt = prepare(conn, table.path("insert"), 600)) {
        conn.setAutoCommit(false);
        try {
          for (long index = 1; index <= table.path("rows").asLong(); index++) {
            check();
            var values = new ArrayList<String>(columns.size());
            if (reader == null) {
              var rng = new Portable.Random(tableSeed ^ (index * 0xd6e8feb86659fd93L));
              var context = new Context(index, 0, index);
              for (var column : columns) values.add(generate(column, context, rng));
            } else {
              var line = reader.readLine();
              if (line == null) throw new IllegalStateException("Input row count changed");
              for (var value : JSON.readTree(line)) values.add(value.isNull() ? null : value.asText());
            }
            for (int col = 0; col < columns.size(); col++)
              if (values.get(col) == null && !columns.get(col).path("nullable").asBoolean())
                throw new IllegalArgumentException("NULL in required column " + columns.get(col).path("name").asText());
            bind(stmt, values);
            stmt.executeUpdate();
            for (var reservoir : reservoirs) reservoir.add(values.get(reservoir.column));
            loaded++;
            if (index % batch == 0) conn.commit();
          }
          conn.commit();
        } catch (Exception e) {
          conn.rollback();
          throw e;
        } finally {
          conn.setAutoCommit(true);
        }
      }
    }
    return loaded;
  }

  static final class Detail {
    final String id, name;
    long success, rows, errors;
    final Portable.Histogram service = new Portable.Histogram(), end = new Portable.Histogram();
    Detail(JsonNode definition) { this(definition.path("id").asText(), definition.path("name").asText()); }
    Detail(String id, String name) { this.id = id; this.name = name; }
    void merge(Detail other) {
      success += other.success;
      rows += other.rows;
      errors += other.errors;
      service.merge(other.service);
      end.merge(other.end);
    }
    Map<String, Object> result() {
      return map("id", id, "name", name, "successfulOperations", success, "successfulRows", rows,
          "errors", errors, "serviceLatency", service.snapshot(), "endToEndLatency", end.snapshot());
    }
  }

  final class Session {
    private final Connection conn;
    private final int client;
    private final List<JsonNode> choices;
    private final long totalWeight;
    private final Portable.Random random;
    private final Map<String, PreparedStatement> prepared = new HashMap<>();
    final Map<String, Detail> details = new LinkedHashMap<>();
    long scheduled;

    Session(Database database, int client, String selected, boolean warm) throws SQLException {
      conn = connection(database);
      this.client = client;
      var options = new ArrayList<JsonNode>();
      long weight = 0;
      for (var op : program.path("operations")) {
        var definition = op.path("definition");
        if (selected != null && !definition.path("id").asText().equals(selected)
            || warm && !definition.path("readOnly").asBoolean(true)) continue;
        options.add(op);
        weight += definition.path("weight").asInt(1);
      }
      choices = options;
      totalWeight = weight;
      random = new Portable.Random(seedFor(seed ^ run.path("repeat").asLong(), "client") ^ (client * 0xd6e8feb86659fd93L));
    }

    long execute(long key) throws Exception {
      long started = System.nanoTime();
      var spec = choices.getFirst();
      if (choices.size() > 1) {
        long choice = Long.remainderUnsigned(random.next(), totalWeight);
        for (var option : choices) {
          choice -= option.path("definition").path("weight").asInt(1);
          if (choice < 0) { spec = option; break; }
        }
      }
      var definition = spec.path("definition");
      var detail = details.computeIfAbsent(definition.path("id").asText(), ignored -> new Detail(definition));
      var ctx = new Context(sequence.incrementAndGet(), client, key);
      boolean transaction = definition.path("transaction").asBoolean();
      boolean began = false;
      int previousIsolation = Connection.TRANSACTION_NONE;
      try {
        var values = new TreeMap<String, String>(String.CASE_INSENSITIVE_ORDER);
        for (var parameter : definition.path("parameters"))
          values.put(parameter.path("name").asText(), generate(parameter, ctx, random));
        if (transaction) {
          int isolation = switch (definition.path("isolation").asText("default")) {
            case "readCommitted" -> Connection.TRANSACTION_READ_COMMITTED;
            case "repeatableRead" -> Connection.TRANSACTION_REPEATABLE_READ;
            case "serializable" -> Connection.TRANSACTION_SERIALIZABLE;
            default -> Connection.TRANSACTION_NONE;
          };
          if (isolation != Connection.TRANSACTION_NONE) {
            previousIsolation = conn.getTransactionIsolation();
            conn.setTransactionIsolation(isolation);
          }
          conn.setAutoCommit(false);
          began = true;
        }
        long affected = 0;
        int i = 0;
        for (var step : definition.path("steps")) {
          var command = spec.path("commands").get(i++);
          List<String> args = arguments(command, values, ctx);
          Answer answer;
          if (workload.path("prepareCommands").asBoolean(true)) {
            String sql = command.path("sql").asText();
            var stmt = prepared.get(sql);
            if (stmt == null) {
              stmt = prepare(conn, command, commandTimeout());
              prepared.put(sql, stmt);
            }
            bind(stmt, args);
            answer = CustomSql.execute(stmt, step.path("result").asText("rows"));
          } else {
            try (var stmt = prepare(conn, command, commandTimeout())) {
              bind(stmt, args);
              answer = CustomSql.execute(stmt, step.path("result").asText("rows"));
            }
          }
          if (step.hasNonNull("expectedScalar") && !valueMatches(answer.scalar(), step.get("expectedScalar").asText()))
            throw new SQLException("Unexpected scalar result");
          if (step.hasNonNull("minRows") && answer.rows() < step.get("minRows").asLong()
              || step.hasNonNull("maxRows") && answer.rows() > step.get("maxRows").asLong())
            throw new SQLException("Unexpected SQL row count: " + answer.rows());
          if (step.hasNonNull("captureAs"))
            values.put(step.get("captureAs").asText(), typedText(answer.scalar(), step.path("captureType").asText("int64")));
          affected += answer.rows();
        }
        if (began) conn.commit();
        long finished = System.nanoTime();
        detail.success++;
        detail.rows += affected;
        detail.service.add(Math.max(1, (finished - started) / 1000.));
        detail.end.add(Math.max(1, (finished - scheduled) / 1000.));
        return affected;
      } catch (Exception e) {
        detail.errors++;
        if (began) try { conn.rollback(); } catch (SQLException rollback) { e.addSuppressed(rollback); }
        throw e;
      } finally {
        if (began) conn.setAutoCommit(true);
        if (previousIsolation != Connection.TRANSACTION_NONE) conn.setTransactionIsolation(previousIsolation);
      }
    }
  }

  private static boolean valueMatches(Object value, String expected) {
    if (value == null) return expected.isEmpty();
    if (expected.equals(value.toString())) return true;
    try { return new BigDecimal(value.toString()).compareTo(new BigDecimal(expected)) == 0; }
    catch (NumberFormatException e) { return false; }
  }

  @FunctionalInterface private interface Preparation { long run() throws Exception; }

  private void setup(String phase, boolean load, Preparation action) throws Exception {
    emit("phase_start", map("phase", phase, "targetSeconds", 0));
    long start = System.nanoTime(), count;
    try { count = action.run(); }
    catch (Exception error) {
      emit("phase_end", map());
      if (!CANCELLED.get()) {
        var failed = base(phase);
        failed.putAll(map("unit", "seconds", "errors", 1, "verified", false, "errorExamples", map(error.toString(), 1)));
        emit("result", failed);
      }
      throw error;
    }
    emit("phase_end", map());
    double elapsed = Math.max(.000001, (System.nanoTime() - start) / 1e9);
    var result = base(phase);
    result.putAll(map("unit", load ? "rows/s" : "seconds", "elapsedSeconds", elapsed, "verified", true,
        "successfulOperations", 1, "successfulRows", count, "rowsPerSecond", load ? count / elapsed : null));
    emit("result", result);
  }

  private void addSessions(List<Database> sessions, int count, String selected, boolean warm) throws Exception {
    while (sessions.size() < count) {
      check();
      var database = new Database(run);
      try {
        database.custom = new Session(database, sessions.size() + 1, selected, warm);
        sessions.add(database);
      } catch (Exception error) {
        database.close();
        throw error;
      }
    }
  }

  private List<JsonNode> stages() {
    var plan = config.path("plan");
    if (plan.has("stages")) {
      var stages = new ArrayList<JsonNode>();
      plan.path("stages").forEach(stages::add);
      return stages;
    }
    return List.of(JSON.valueToTree(map("name", "Load", "clients", run.path("clients").asInt(),
        "seconds", config.path("durationSeconds").asDouble(5), "operationsPerSecond",
        config.path("loadModel").asText().equals("constantRate") ? config.path("targetOpsPerSecond").asInt(1000) : null)));
  }

  @SuppressWarnings("unchecked")
  void benchmark(Database database) throws Exception {
    var conn = connection(database);
    var metadata = (Map<String, Object>) database.metadata();
    var settings = (Map<String, String>) metadata.get("settings");
    settings.put("workload_name", workload.path("name").asText());
    settings.put("workload_sha256", program.path("fingerprint").asText());
    settings.put("generator", "custom-splitmix64-v1");
    settings.put("parameter_binding", "nullable text with typed SQL casts");
    settings.put("prepared_queries", workload.path("prepareCommands").asBoolean(true) ? "JDBC statements reused" : "JDBC statements per operation");
    emit("metadata", metadata);
    boolean prepared = false, planned = config.path("plan").has("stages");
    var stages = stages();
    for (var unit : program.path("units")) {
      check();
      String phase = unit.path("phase").asText();
      String selected = unit.hasNonNull("operation") ? unit.path("operation").asText() : null;
      String prefix = "custom/setup/" + (selected == null ? "mix" : selected) + "/";
      if (!prepared || workload.path("resetBetweenOperations").asBoolean(true)) {
        setup(prefix + "schema", false, () -> schema(conn));
        setup(prefix + "load", true, () -> load(conn));
        setup(prefix + "indexes", false, () -> { for (var sql : program.path("afterLoad")) exec(conn, sql.asText()); return 0; });
        prepared = true;
      }
      boolean before = false;
      for (var spec : program.path("checks")) before |= spec.path("definition").path("before").asBoolean(true);
      if (before) setup(prefix + "before", false, () -> { checks(conn, true); return 0; });
      boolean reading = false;
      for (var op : program.path("operations")) {
        var definition = op.path("definition");
        reading |= definition.path("readOnly").asBoolean(true) && (selected == null || selected.equals(definition.path("id").asText()));
      }
      int peak = run.path("clients").asInt();
      double warmup = config.path("warmupSeconds").asDouble(2);
      if (reading && warmup > 0) {
        var warm = new ArrayList<Database>();
        int count = stages.getFirst().path("clients").asInt();
        try {
          addSessions(warm, count, selected, true);
          var result = measure(warm, new long[peak], new AtomicLong(), phase, warmup, count, null, null, false);
          if (((Number) result.get("errors")).longValue() > 0) throw new SQLException("Errors during custom workload warmup");
        } finally { warm.forEach(Database::close); }
      }
      var sessions = new ArrayList<Database>();
      var sequences = new long[peak];
      try {
        for (int index = 0; index < stages.size(); index++) {
          check();
          var stage = stages.get(index);
          int count = stage.path("clients").asInt();
          double seconds = stage.path("seconds").asDouble();
          Integer rate = stage.hasNonNull("operationsPerSecond") ? stage.path("operationsPerSecond").asInt() : null;
          var stamp = planned ? map("number", index + 1, "name", stage.path("name").asText(),
              "targetSeconds", seconds, "operationsPerSecond", rate) : null;
          addSessions(sessions, count, selected, false);
          sessions.forEach(session -> session.custom.details.clear());
          emit("phase_start", map("phase", phase, "targetSeconds", seconds, "stage", stamp, "clients", count));
          var result = measure(sessions, sequences, new AtomicLong(), phase, seconds, count, rate, stamp, true);
          emit("phase_end", map());
          var details = new LinkedHashMap<String, Detail>();
          for (int i = 0; i < count; i++) for (var detail : sessions.get(i).custom.details.values())
            details.computeIfAbsent(detail.id, id -> new Detail(id, detail.name)).merge(detail);
          for (var detail : details.values()) completedByOperation.merge(detail.id, detail.success, Long::sum);
          result.put("operationBreakdown", details.values().stream().map(Detail::result).toList());
          long success = ((Number) result.get("successfulOperations")).longValue();
          completed += success;
          result.put("verified", success > 0 && !CANCELLED.get());
          if (!CANCELLED.get()) {
            try { checks(conn, false); }
            catch (Exception error) {
              result.put("verified", false);
              emit("result", result);
              throw error;
            }
          }
          emit("result", result);
          check();
        }
      } finally { sessions.forEach(Database::close); }
    }
  }
}
