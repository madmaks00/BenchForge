package dev.benchforge;

import java.util.*;

final class Portable {
  static final String GENERATOR = "splitmix64-ascii-v1";
  static final String ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

  static Map<String, Object> map(Object... entries) {
    var result = new LinkedHashMap<String, Object>();
    for (int i = 0; i < entries.length; i += 2) result.put((String) entries[i], entries[i + 1]);
    return result;
  }

  static final class Random {
    long state;

    Random(long seed) {
      state = seed;
    }

    long next() {
      state += 0x9e3779b97f4a7c15L;
      long z = state;
      z = (z ^ (z >>> 30)) * 0xbf58476d1ce4e5b9L;
      z = (z ^ (z >>> 27)) * 0x94d049bb133111ebL;
      return z ^ (z >>> 31);
    }

    long key(long count, boolean hotspot) {
      if (!hotspot) return Long.remainderUnsigned(next(), count) + 1;
      long cutoff = Math.max(1, count / 5);
      return Long.remainderUnsigned(next(), 100) < 80
          ? Long.remainderUnsigned(next(), cutoff) + 1
          : cutoff + Long.remainderUnsigned(next(), count - cutoff) + 1;
    }
  }

  record Row(long id, int groupId, long amountCents, long version, String payload) {}

  static Row row(long id, long seed, int size) {
    var rng = new Random(seed ^ id * 0xd6e8feb86659fd93L);
    long amount = Long.remainderUnsigned(rng.next(), 1_000_000) + 1;
    var payload = new StringBuilder(size);
    for (int i = 0; i < size; i++)
      payload.append(ALPHABET.charAt((int) Long.remainderUnsigned(rng.next(), 62)));
    return new Row(id, (int) ((id - 1) % 100), amount, 0, payload.toString());
  }

  static final class Histogram {
    long count, overflow;
    double sum, max;
    final TreeMap<Integer, Long> buckets = new TreeMap<>();

    void add(double us) {
      long value = Math.max(1, (long) Math.ceil(us));
      if (value >= 1L << 32) {
        overflow++;
        value = (1L << 32) - 1;
      }
      int exponent = 63 - Long.numberOfLeadingZeros(value),
          bucket = exponent * 128 + (int) (((value - (1L << exponent)) * 128) >> exponent);
      buckets.merge(bucket, 1L, Long::sum);
      count++;
      sum += us;
      max = Math.max(max, us);
    }

    void merge(Histogram h) {
      h.buckets.forEach((b, n) -> buckets.merge(b, n, Long::sum));
      count += h.count;
      overflow += h.overflow;
      sum += h.sum;
      max = Math.max(max, h.max);
    }

    Double percentile(double q) {
      if (count == 0) return null;
      long rank = Math.max(1, (long) Math.ceil(count * q)), seen = 0;
      for (var entry : buckets.entrySet()) {
        seen += entry.getValue();
        if (seen >= rank) {
          long basis = 1L << (entry.getKey() >> 7);
          long upper = basis + (((entry.getKey() & 127) + 1) * basis + 127) / 128 - 1;
          return upper / 1000.;
        }
      }
      throw new IllegalStateException("Histogram count mismatch");
    }

    Map<String, Object> snapshot() {
      return map(
          "count",
          count,
          "sumMicroseconds",
          sum,
          "maxMicroseconds",
          max,
          "overflow",
          overflow,
          "buckets",
          buckets,
          "format",
          "binary128-us-v1",
          "meanMs",
          count > 0 ? sum / count / 1000 : null,
          "p50Ms",
          percentile(.5),
          "p95Ms",
          percentile(.95),
          "p99Ms",
          percentile(.99));
    }
  }

  static Object vectors() {
    var rng = new Random(0);
    var histogram = new Histogram();
    for (double value : new double[] {.1, 1, 1.1, 127.5, 128, 129.2, 1024, 4294967297.})
      histogram.add(value);
    return map(
        "generator",
        GENERATOR,
        "random",
        List.of(
            String.format("%016x", rng.next()),
            String.format("%016x", rng.next()),
            String.format("%016x", rng.next())),
        "rows",
        List.of(row(1, 42, 16), row(1000, 42, 16), row(7, -1, 16)),
        "histogram",
        histogram.snapshot());
  }
}
