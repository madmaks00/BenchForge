"""BenchForge portable algorithms; all arithmetic follows protocol-v1.md."""

import math

MASK = (1 << 64) - 1
ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
GENERATOR = "splitmix64-ascii-v1"


class Random:
    def __init__(self, seed):
        self.state = seed & MASK

    def next(self):
        self.state = (self.state + 0x9E3779B97F4A7C15) & MASK
        z = self.state
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & MASK
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & MASK
        return z ^ (z >> 31)

    def key(self, count, hotspot):
        if not hotspot:
            return self.next() % count + 1
        cutoff = max(1, count // 5)
        return (
            self.next() % cutoff + 1
            if self.next() % 100 < 80
            else cutoff + self.next() % (count - cutoff) + 1
        )


def row(identifier, seed, size):
    rng = Random(seed ^ ((identifier * 0xD6E8FEB86659FD93) & MASK))
    amount = rng.next() % 1_000_000 + 1
    return dict(
        id=identifier,
        groupId=(identifier - 1) % 100,
        amountCents=amount,
        version=0,
        payload="".join(ALPHABET[rng.next() % 62] for _ in range(size)),
    )


class Histogram:
    def __init__(self):
        self.buckets = {}
        self.count = self.overflow = 0
        self.total = self.maximum = 0.0

    def add(self, us):
        value = max(1, math.ceil(us))
        if value >= 1 << 32:
            self.overflow += 1
            value = (1 << 32) - 1
        exponent = value.bit_length() - 1
        bucket = exponent * 128 + ((value - (1 << exponent)) * 128 >> exponent)
        self.buckets[bucket] = self.buckets.get(bucket, 0) + 1
        self.count += 1
        self.total += us
        self.maximum = max(self.maximum, us)

    def merge(self, other):
        for bucket, count in other.buckets.items():
            self.buckets[bucket] = self.buckets.get(bucket, 0) + count
        self.count += other.count
        self.overflow += other.overflow
        self.total += other.total
        self.maximum = max(self.maximum, other.maximum)

    def snapshot(self):
        def percentile(q):
            rank, seen = max(1, math.ceil(self.count * q)), 0
            if not self.count:
                return None
            for bucket, count in sorted(self.buckets.items()):
                seen += count
                if seen >= rank:
                    basis, offset = 1 << (bucket >> 7), bucket & 127
                    return (basis + ((offset + 1) * basis + 127) // 128 - 1) / 1000

        return dict(
            count=self.count,
            sumMicroseconds=self.total,
            maxMicroseconds=self.maximum,
            overflow=self.overflow,
            buckets=self.buckets,
            format="binary128-us-v1",
            meanMs=self.total / self.count / 1000 if self.count else None,
            p50Ms=percentile(0.5),
            p95Ms=percentile(0.95),
            p99Ms=percentile(0.99),
        )


def vectors():
    rng, hist = Random(0), Histogram()
    for value in [0.1, 1, 1.1, 127.5, 128, 129.2, 1024, 4294967297]:
        hist.add(value)
    return dict(
        generator=GENERATOR,
        random=[f"{rng.next():016x}" for _ in range(3)],
        rows=[row(1, 42, 16), row(1000, 42, 16), row(7, MASK, 16)],
        histogram=hist.snapshot(),
    )
