package dev.benchforge;

import static dev.benchforge.Portable.*;

import com.fasterxml.jackson.databind.*;
import java.io.*;
import java.lang.management.ManagementFactory;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import oshi.SystemInfo;
import oshi.software.os.OSProcess;

public final class Worker {
  static final ObjectMapper JSON = new ObjectMapper();
  static final AtomicBoolean CANCELLED = new AtomicBoolean();
  static final List<String> SCENARIOS =
      List.of(
          "PointRead",
          "RangeRead",
          "Aggregate",
          "AnalyticalScan",
          "Update",
          "Mixed",
          "Insert",
          "BatchInsert",
          "Delete",
          "FullTextSearch");
  static JsonNode run;

  static synchronized void emit(String type, Object data) throws IOException {
    System.out.println(
        JSON.writeValueAsString(
            map(
                "protocolVersion",
                1,
                "type",
                type,
                "runId",
                run == null ? "" : run.path("runId").asText(),
                "data",
                data)));
    System.out.flush();
  }

  static void check() throws InterruptedException {
    if (CANCELLED.get()) throw new InterruptedException("Cancelled");
  }

  static List<String> capabilities(String target) {
    return switch (target) {
      case "clickHouse" -> List.of("RangeRead", "Aggregate", "AnalyticalScan", "BatchInsert");
      case "redis", "valkey" ->
          List.of("PointRead", "Update", "Mixed", "Insert", "BatchInsert", "Delete");
      case "duckDb" -> SCENARIOS.stream().filter(s -> !s.equals("FullTextSearch")).toList();
      case "elasticsearch", "openSearch" ->
          SCENARIOS.stream().filter(s -> !s.equals("AnalyticalScan")).toList();
      default ->
          SCENARIOS.stream()
              .filter(s -> !s.equals("AnalyticalScan") && !s.equals("FullTextSearch"))
              .toList();
    };
  }

  static Map<String, Object> base(String phase) {
    return map(
        "engine",
        "java",
        "target",
        run.path("target").asText(),
        "datasetRows",
        run.path("rows").asLong(),
        "clients",
        run.path("clients").asInt(),
        "repeat",
        run.path("repeat").asInt(),
        "phase",
        phase);
  }

  static int batchSize() {
    var config = run.path("config");
    return Math.min(
        config.path("batchSize").asInt(1000),
        Math.max(1, 8 * 1024 * 1024 / (config.path("payloadBytes").asInt(256) + 128)));
  }

  static final class Counts {
    volatile long success, errors;
    long rows, dropped, finished;
    final Histogram service = new Histogram(), end = new Histogram(), failed = new Histogram();
    final Map<String, Long> examples = new HashMap<>();
  }

  static List<Database> sessions(int count) throws Exception {
    var result = new ArrayList<Database>();
    try {
      for (int i = 0; i < count; i++) {
        check();
        result.add(new Database(run));
      }
      return result;
    } catch (Exception error) {
      result.forEach(Database::close);
      throw error;
    }
  }

  static double rss(OSProcess process) {
    if (process == null) throw new IllegalStateException("Could not read process memory");
    process.updateAttributes();
    return process.getResidentSetSize() / 1048576.;
  }

  static Map<String, Object> measure(
      List<Database> sessions,
      long[] sequences,
      AtomicLong deleted,
      String operation,
      double seconds,
      int clients,
      Integer rate,
      Map<String, Object> stamp,
      boolean progress)
      throws Exception {
    var config = run.path("config");
    long seed = config.has("seed") ? config.path("seed").asLong() : 42;
    int payload = config.path("payloadBytes").asInt(256), peak = run.path("clients").asInt();
    long rows = run.path("rows").asLong();
    int repeat = run.path("repeat").asInt(), number = stamp == null ? 0 : (int) stamp.get("number");
    long planned = rate == null ? 0 : (long) Math.floor(seconds * rate);
    double maxLag = config.path("maxStartLagMs").asDouble(20) / 1000;
    var ready = new CountDownLatch(clients);
    var gate = new CountDownLatch(1);
    var started = new AtomicLong();
    var counts = new Counts[clients];
    try (var executor = Executors.newVirtualThreadPerTaskExecutor()) {
      var tasks = new ArrayList<Future<?>>();
      for (int index = 0; index < clients; index++) {
        final int client = index;
        var c = new Counts();
        counts[index] = c;
        tasks.add(
            executor.submit(
                () -> {
                  ready.countDown();
                  try {
                    gate.await();
                  } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                    return;
                  }
                  long start = started.get(),
                      deadline = start + (long) (seconds * 1e9),
                      sequence = sequences[client],
                      slot = client;
                  var rng =
                      new Portable.Random(
                          seed ^ (client + 1L) * 104729 ^ repeat ^ (long) number * 32452843);
                  while (!CANCELLED.get() && System.nanoTime() < deadline) {
                    long scheduled = System.nanoTime();
                    if (rate != null) {
                      if (slot >= planned) break;
                      scheduled = start + (long) (slot * 1e9 / rate);
                      double lag = (System.nanoTime() - scheduled) / 1e9 - maxLag;
                      if (lag > 0) {
                        long skipped =
                            Math.min(
                                Math.max(1, (long) Math.floor(lag * rate / clients) + 1),
                                (planned - 1 - slot) / clients + 1);
                        c.dropped += skipped;
                        slot += skipped * clients;
                        continue;
                      }
                      while (System.nanoTime() < scheduled && !CANCELLED.get())
                        try {
                          Thread.sleep(1);
                        } catch (InterruptedException e) {
                          Thread.currentThread().interrupt();
                          CANCELLED.set(true);
                        }
                      if (CANCELLED.get() || System.nanoTime() >= deadline) break;
                      if ((System.nanoTime() - scheduled) / 1e9 > maxLag) {
                        c.dropped++;
                        slot += clients;
                        continue;
                      }
                    }
                    String selected =
                        operation.equals("Mixed")
                            ? (Long.remainderUnsigned(rng.next(), 100) < 80
                                ? "PointRead"
                                : "Update")
                            : operation;
                    long key = rng.key(rows, config.path("hotspot").asBoolean());
                    Row one = null;
                    List<Row> batch = null;
                    if (selected.equals("Insert") || selected.equals("BatchInsert")) {
                      int size = selected.equals("BatchInsert") ? batchSize() : 1;
                      long first =
                          (SCENARIOS.indexOf(operation) + 1L) * 1_000_000_000_000L
                              + (sequence * peak + client) * size
                              + 1;
                      if (selected.equals("Insert")) one = row(first, seed, payload);
                      else {
                        batch = new ArrayList<>(size);
                        for (int j = 0; j < size; j++) batch.add(row(first + j, seed, payload));
                      }
                    }
                    if (selected.equals("Delete")) {
                      key =
                          stamp == null
                              ? sequence * clients + client + 1
                              : deleted.incrementAndGet();
                      if (key > rows) break;
                    }
                    long before = System.nanoTime();
                    try {
                      var session = sessions.get(client);
                      if (session.custom != null) session.custom.scheduled = rate == null ? before : scheduled;
                      long affected = session.execute(selected, key, one, batch),
                          finish = System.nanoTime();
                      c.success++;
                      c.rows += affected;
                      c.service.add(Math.max(1, (finish - before) / 1000.));
                      c.end.add(
                          Math.max(1, (finish - (rate == null ? before : scheduled)) / 1000.));
                    } catch (Exception error) {
                      c.errors++;
                      c.failed.add(Math.max(1, (System.nanoTime() - before) / 1000.));
                      String message = error.getClass().getSimpleName() + ": " + error.getMessage(),
                          password = run.path("endpoint").path("password").asText();
                      if (!password.isEmpty()) message = message.replace(password, "[redacted]");
                      message = message.substring(0, Math.min(message.length(), 250));
                      c.examples.merge(message, 1L, Long::sum);
                    }
                    sequence++;
                    slot += clients;
                    if (c.errors >= 50) break;
                  }
                  if (rate != null && slot < planned && !CANCELLED.get())
                    c.dropped += (planned - 1 - slot) / clients + 1;
                  sequences[client] = sequence;
                  c.finished = System.nanoTime();
                }));
      }
      ready.await();
      var os =
          (com.sun.management.OperatingSystemMXBean) ManagementFactory.getOperatingSystemMXBean();
      OSProcess process =
          new SystemInfo().getOperatingSystem().getProcess((int) ProcessHandle.current().pid());
      double maxRss = rss(process);
      long cpu = os.getProcessCpuTime();
      started.set(System.nanoTime());
      gate.countDown();
      long reportAt = started.get() + 1_000_000_000, sampleAt = started.get() + 250_000_000;
      while (tasks.stream().anyMatch(task -> !task.isDone())) {
        Thread.sleep(50);
        if (System.nanoTime() >= sampleAt) {
          maxRss = Math.max(maxRss, rss(process));
          sampleAt = System.nanoTime() + 250_000_000;
        }
        if (progress && System.nanoTime() >= reportAt) {
          emit(
              "progress",
              map(
                  "phase",
                  operation,
                  "stage",
                  stamp == null ? null : stamp.get("name"),
                  "clients",
                  clients,
                  "targetSeconds",
                  seconds,
                  "successful",
                  Arrays.stream(counts).mapToLong(c -> c.success).sum(),
                  "errors",
                  Arrays.stream(counts).mapToLong(c -> c.errors).sum(),
                  "elapsedSeconds",
                  (System.nanoTime() - started.get()) / 1e9));
          reportAt = System.nanoTime() + 1_000_000_000;
        }
      }
      for (var task : tasks) task.get();
      double elapsed =
          Math.max(
              .000001,
              (Arrays.stream(counts).mapToLong(c -> c.finished).max().orElse(started.get())
                      - started.get())
                  / 1e9);
      var service = new Histogram();
      var end = new Histogram();
      var failed = new Histogram();
      var examples = new HashMap<String, Long>();
      long success = 0, errors = 0, dropped = 0, affected = 0;
      for (var c : counts) {
        service.merge(c.service);
        end.merge(c.end);
        failed.merge(c.failed);
        success += c.success;
        errors += c.errors;
        dropped += c.dropped;
        affected += c.rows;
        c.examples.forEach((key, value) -> examples.merge(key, value, Long::sum));
      }
      var limited = new LinkedHashMap<String, Long>();
      examples.entrySet().stream()
          .sorted(Map.Entry.<String, Long>comparingByValue().reversed())
          .limit(8)
          .forEach(entry -> limited.put(entry.getKey(), entry.getValue()));
      double endRss = rss(process);
      maxRss = Math.max(maxRss, endRss);
      var result = base(operation);
      result.putAll(
          map(
              "stage",
              stamp,
              "clients",
              clients,
              "unit",
              operation.equals("BatchInsert") ? "rows/s" : "ops/s",
              "elapsedSeconds",
              elapsed,
              "successfulOperations",
              success,
              "successfulRows",
              affected,
              "errors",
              errors,
              "dropped",
              dropped,
              "plannedOperations",
              rate != null && !CANCELLED.get() ? planned : success + errors + dropped,
              "operationsPerSecond",
              success / elapsed,
              "rowsPerSecond",
              List.of("Insert", "BatchInsert", "Update", "Delete").contains(operation)
                  ? affected / elapsed
                  : null,
              "serviceLatency",
              service.snapshot(),
              "endToEndLatency",
              end.snapshot(),
              "errorLatency",
              failed.snapshot(),
              "client",
              map(
                  "cpuPercent",
                  (os.getProcessCpuTime() - cpu) / 1e9 / elapsed * 100,
                  "rssEndMb",
                  endRss,
                  "rssSampledMaxMb",
                  maxRss,
                  "allocatedBytes",
                  null,
                  "gen0Collections",
                  null,
                  "gen1Collections",
                  null,
                  "gen2Collections",
                  null,
                  "gcPauseMs",
                  null),
              "errorExamples",
              limited,
              "cancelled",
              CANCELLED.get()));
      return result;
    }
  }

  @SuppressWarnings("unchecked")
  static void benchmark() throws Exception {
    var config = run.path("config");
    String target = run.path("target").asText();
    long until = System.nanoTime() + 90_000_000_000L;
    Database database;
    while (true) {
      check();
      try {
        database = new Database(run);
        break;
      } catch (Exception error) {
        if (target.equals("sqlite") || System.nanoTime() >= until) throw error;
        Thread.sleep(500);
      }
    }
    try (var db = database) {
      if (config.hasNonNull("customWorkload")) { new CustomSql().benchmark(db); return; }
      var metadata = (Map<String, Object>) db.metadata();
      ((Map<String, String>) metadata.get("settings"))
          .put("effective_batch_size", Integer.toString(batchSize()));
      emit("metadata", metadata);
      db.reset();
      emit("phase_start", map("phase", "bulk_load", "targetSeconds", 0));
      long start = System.nanoTime(),
          rows = run.path("rows").asLong(),
          seed = config.has("seed") ? config.path("seed").asLong() : 42,
          amount = 0,
          reportAt = start + 1_000_000_000;
      double generation = 0;
      for (long first = 1; first <= rows; first += batchSize()) {
        check();
        long before = System.nanoTime();
        var batch = new ArrayList<Row>();
        for (long id = first; id < Math.min(first + batchSize(), rows + 1); id++)
          batch.add(row(id, seed, config.path("payloadBytes").asInt(256)));
        generation += (System.nanoTime() - before) / 1e9;
        db.batch(batch);
        for (var r : batch) amount += r.amountCents();
        if (System.nanoTime() >= reportAt) {
          emit(
              "progress",
              map("phase", "bulk_load", "loaded", first + batch.size() - 1, "total", rows));
          reportAt = System.nanoTime() + 1_000_000_000;
        }
      }
      double elapsed = Math.max(.000001, (System.nanoTime() - start) / 1e9);
      check();
      if (!Arrays.equals(db.digest(), new long[] {rows, rows * (rows + 1) / 2, amount}))
        throw new IOException("Dataset digest mismatch");
      var result = base("bulk_load");
      result.putAll(
          map(
              "unit",
              "rows/s",
              "elapsedSeconds",
              elapsed,
              "successfulRows",
              rows,
              "successfulOperations",
              (rows + batchSize() - 1) / batchSize(),
              "rowsPerSecond",
              rows / elapsed,
              "generationSeconds",
              generation,
              "verified",
              true));
      emit("result", result);
      if (!List.of("redis", "valkey", "clickHouse").contains(target)) {
        check();
        emit("phase_start", map("phase", "index_build", "targetSeconds", 0));
        long before = System.nanoTime();
        db.prepare();
        result = base("index_build");
        result.putAll(
            map(
                "unit",
                "seconds",
                "elapsedSeconds",
                (System.nanoTime() - before) / 1e9,
                "successfulOperations",
                1,
                "verified",
                true));
        emit("result", result);
      }
      var supported = capabilities(target);
      var requested = new ArrayList<String>();
      for (var value : config.path("scenarios")) {
        String name = value.asText();
        requested.add(name.substring(0, 1).toUpperCase(Locale.ROOT) + name.substring(1));
      }
      for (String name : requested)
        if (!supported.contains(name))
          emit(
              "unsupported",
              map("scenario", name, "message", "Not supported by this database profile"));
      boolean planned = config.hasNonNull("plan");
      var operations =
          SCENARIOS.stream()
              .filter(
                  s ->
                      supported.contains(s)
                          && (requested.isEmpty()
                              ? (!planned || s.equals(supported.getFirst()))
                              : requested.contains(s)))
              .sorted(Comparator.comparingInt(s -> s.equals("Delete") ? 1 : 0))
              .toList();
      double warmup = config.path("warmupSeconds").asDouble(2);
      if (warmup > 0) {
        int clients =
            planned
                ? config.path("plan").path("stages").get(0).path("clients").asInt()
                : run.path("clients").asInt();
        var sessions = sessions(clients);
        try {
          var measured =
              measure(
                  sessions,
                  new long[clients],
                  new AtomicLong(),
                  supported.getFirst(),
                  warmup,
                  clients,
                  !planned && config.path("loadModel").asText().equals("constantRate")
                      ? config.path("targetOpsPerSecond").asInt(1000)
                      : null,
                  null,
                  false);
          if (((Number) measured.get("errors")).longValue() > 0)
            throw new IOException("Warmup errors");
        } finally {
          sessions.forEach(Database::close);
        }
      }
      long expected = rows;
      for (String operation : operations) {
        check();
        var sessions = new ArrayList<Database>();
        long[] sequences = new long[run.path("clients").asInt()];
        var deleted = new AtomicLong();
        try {
          List<JsonNode> stages = new ArrayList<>();
          if (planned) config.path("plan").path("stages").forEach(stages::add);
          else
            stages.add(
                JSON.valueToTree(
                    map(
                        "name",
                        "",
                        "clients",
                        run.path("clients").asInt(),
                        "seconds",
                        config.path("durationSeconds").asDouble(5),
                        "operationsPerSecond",
                        config.path("loadModel").asText().equals("constantRate")
                            ? config.path("targetOpsPerSecond").asInt(1000)
                            : null)));
          for (int index = 0; index < stages.size(); index++) {
            check();
            var s = stages.get(index);
            int count = s.path("clients").asInt();
            if (sessions.size() < count) sessions.addAll(sessions(count - sessions.size()));
            Integer rate =
                s.hasNonNull("operationsPerSecond") ? s.path("operationsPerSecond").asInt() : null;
            Map<String, Object> stamp =
                planned
                    ? map(
                        "number",
                        index + 1,
                        "name",
                        s.path("name").asText(),
                        "targetSeconds",
                        s.path("seconds").asDouble(),
                        "operationsPerSecond",
                        rate)
                    : null;
            emit(
                "phase_start",
                map(
                    "phase",
                    operation,
                    "targetSeconds",
                    s.path("seconds").asDouble(),
                    "stage",
                    stamp,
                    "clients",
                    count));
            result =
                measure(
                    sessions,
                    sequences,
                    deleted,
                    operation,
                    s.path("seconds").asDouble(),
                    count,
                    rate,
                    stamp,
                    true);
            emit("phase_end", map());
            if (operation.equals("Insert") || operation.equals("BatchInsert"))
              expected += ((Number) result.get("successfulRows")).longValue();
            if (operation.equals("Delete"))
              expected -= ((Number) result.get("successfulRows")).longValue();
            long actual = db.count(),
                success = ((Number) result.get("successfulOperations")).longValue();
            result.put("verified", actual == expected && success > 0 && !CANCELLED.get());
            result.put("cancelled", CANCELLED.get());
            emit("result", result);
            if (actual != expected)
              throw new IOException("Count mismatch: " + actual + " != " + expected);
            if (!planned && success == 0 && !CANCELLED.get())
              throw new IOException("No successful operations");
          }
        } finally {
          sessions.forEach(Database::close);
        }
      }
    }
  }

  public static void main(String[] args) throws Exception {
    TimeZone.setDefault(TimeZone.getTimeZone("Etc/UTC"));
    System.setOut(
        new PrintStream(new FileOutputStream(FileDescriptor.out), true, StandardCharsets.UTF_8));
    System.setErr(
        new PrintStream(new FileOutputStream(FileDescriptor.err), true, StandardCharsets.UTF_8));
    if (Arrays.asList(args).contains("--self-test")) {
      System.out.println(JSON.writeValueAsString(vectors()));
      return;
    }
    emit(
        "hello",
        map(
            "engine",
            "java",
            "runtime",
            System.getProperty("java.runtime.version"),
            "generator",
            GENERATOR,
            "histogram",
            "binary128-us-v1"));
    int exit = 1;
    try {
      var input = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
      String line = input.readLine();
      if (line == null || line.length() > 1_048_576)
        throw new IOException("Invalid command length");
      var command = JSON.readTree(line.replaceFirst("^\\x{FEFF}", ""));
      if (command.path("protocolVersion").asInt() != 1
          || !command.path("type").asText().equals("run"))
        throw new IOException("Expected protocol v1 run");
      run = command.path("run");
      if (run.path("rows").asLong() < 100
          || run.path("clients").asInt() < 1
          || !run.path("engine").asText("java").equals("java"))
        throw new IOException("Invalid run");
      Thread.ofVirtual()
          .start(
              () -> {
                try {
                  String value;
                  while ((value = input.readLine()) != null) {
                    var next = JSON.readTree(value);
                    if (next.path("protocolVersion").asInt() == 1
                        && next.path("type").asText().equals("cancel")) {
                      CANCELLED.set(true);
                      return;
                    }
                  }
                  CANCELLED.set(true);
                } catch (Exception ignored) {
                  CANCELLED.set(true);
                }
              });
      benchmark();
      emit("completed", map("cancelled", CANCELLED.get()));
      exit = CANCELLED.get() ? 2 : 0;
    } catch (InterruptedException error) {
      emit("completed", map("cancelled", true));
      exit = 2;
    } catch (Exception error) {
      String message = error.getClass().getSimpleName() + ": " + error.getMessage();
      String password = run == null ? "" : run.path("endpoint").path("password").asText();
      if (!password.isEmpty()) message = message.replace(password, "[redacted]");
      emit("failed", map("message", message));
    }
    System.exit(exit);
  }
}
