export const GENERATOR = 'splitmix64-ascii-v1';
const MASK = (1n << 64n) - 1n,
  ALPHABET = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz';
export class Random {
  constructor(seed) {
    this.state = BigInt(seed) & MASK;
  }
  next() {
    this.state = (this.state + 0x9e3779b97f4a7c15n) & MASK;
    let z = this.state;
    z = ((z ^ (z >> 30n)) * 0xbf58476d1ce4e5b9n) & MASK;
    z = ((z ^ (z >> 27n)) * 0x94d049bb133111ebn) & MASK;
    return z ^ (z >> 31n);
  }
  key(count, hotspot) {
    if (!hotspot) return Number(this.next() % BigInt(count)) + 1;
    const cutoff = Math.max(1, Math.floor(count / 5));
    return this.next() % 100n < 80n
      ? Number(this.next() % BigInt(cutoff)) + 1
      : cutoff + Number(this.next() % BigInt(count - cutoff)) + 1;
  }
}
export function row(id, seed, size) {
  const rng = new Random(BigInt(seed) ^ ((BigInt(id) * 0xd6e8feb86659fd93n) & MASK));
  const amountCents = Number(rng.next() % 1000000n) + 1;
  let payload = '';
  for (let i = 0; i < size; i++) payload += ALPHABET[Number(rng.next() % 62n)];
  return { id, groupId: (id - 1) % 100, amountCents, version: 0, payload };
}
export class Histogram {
  constructor() {
    this.buckets = {};
    this.count = 0;
    this.sumMicroseconds = 0;
    this.maxMicroseconds = 0;
    this.overflow = 0;
  }
  add(us) {
    let v = Math.max(1, Math.ceil(us));
    if (v >= 4294967296) {
      this.overflow++;
      v = 4294967295;
    }
    const e = Math.floor(Math.log2(v)),
      bucket = e * 128 + Math.floor(((v - 2 ** e) * 128) / 2 ** e);
    this.buckets[bucket] = (this.buckets[bucket] || 0) + 1;
    this.count++;
    this.sumMicroseconds += us;
    this.maxMicroseconds = Math.max(this.maxMicroseconds, us);
  }
  merge(h) {
    for (const [b, c] of Object.entries(h.buckets)) this.buckets[b] = (this.buckets[b] || 0) + c;
    this.count += h.count;
    this.sumMicroseconds += h.sumMicroseconds;
    this.maxMicroseconds = Math.max(this.maxMicroseconds, h.maxMicroseconds);
    this.overflow += h.overflow;
  }
  percentile(q) {
    if (!this.count) return null;
    const rank = Math.max(1, Math.ceil(this.count * q));
    let seen = 0;
    for (const b of Object.keys(this.buckets)
      .map(Number)
      .sort((a, b) => a - b)) {
      seen += this.buckets[b];
      if (seen >= rank) {
        const basis = 2 ** Math.floor(b / 128),
          offset = b % 128;
        return (basis + Math.ceil(((offset + 1) * basis) / 128) - 1) / 1000;
      }
    }
    throw Error('Histogram count mismatch');
  }
  snapshot() {
    return {
      count: this.count,
      sumMicroseconds: this.sumMicroseconds,
      maxMicroseconds: this.maxMicroseconds,
      overflow: this.overflow,
      buckets: this.buckets,
      format: 'binary128-us-v1',
      meanMs: this.count ? this.sumMicroseconds / this.count / 1000 : null,
      p50Ms: this.percentile(0.5),
      p95Ms: this.percentile(0.95),
      p99Ms: this.percentile(0.99),
    };
  }
}
export function vectors() {
  const rng = new Random(0),
    hist = new Histogram();
  for (const value of [0.1, 1, 1.1, 127.5, 128, 129.2, 1024, 4294967297]) hist.add(value);
  return {
    generator: GENERATOR,
    random: Array.from({ length: 3 }, () => rng.next().toString(16).padStart(16, '0')),
    rows: [row(1, 42, 16), row(1000, 42, 16), row(7, MASK, 16)],
    histogram: hist.snapshot(),
  };
}
