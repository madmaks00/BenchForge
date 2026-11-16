use serde::Serialize;
use serde_json::{json, Value};
use std::collections::BTreeMap;

pub const GENERATOR: &str = "splitmix64-ascii-v1";
pub struct Random(pub u64);
impl Random {
    pub fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9e3779b97f4a7c15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xbf58476d1ce4e5b9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94d049bb133111eb);
        z ^ (z >> 31)
    }
    pub fn key(&mut self, count: i64, hotspot: bool) -> i64 {
        if !hotspot {
            return (self.next() % count as u64) as i64 + 1;
        };
        let cutoff = 1.max(count / 5);
        if self.next() % 100 < 80 {
            (self.next() % cutoff as u64) as i64 + 1
        } else {
            cutoff + (self.next() % (count - cutoff) as u64) as i64 + 1
        }
    }
}
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Row {
    pub id: i64,
    pub group_id: i32,
    pub amount_cents: i64,
    pub version: i64,
    pub payload: String,
}
pub fn row(id: i64, seed: u64, size: usize) -> Row {
    let mut rng = Random(seed ^ (id as u64).wrapping_mul(0xd6e8feb86659fd93));
    let amount_cents = (rng.next() % 1000000) as i64 + 1;
    let alphabet = b"0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    let payload = (0..size)
        .map(|_| alphabet[(rng.next() % 62) as usize] as char)
        .collect();
    Row {
        id,
        group_id: ((id - 1) % 100) as i32,
        amount_cents,
        version: 0,
        payload,
    }
}
#[derive(Default, Clone)]
pub struct Histogram {
    pub count: i64,
    pub sum: f64,
    pub max: f64,
    pub overflow: i64,
    pub buckets: BTreeMap<usize, i64>,
}
impl Histogram {
    pub fn add(&mut self, us: f64) {
        let mut v = us.ceil().max(1.) as u64;
        if v >= 1 << 32 {
            self.overflow += 1;
            v = (1 << 32) - 1
        };
        let e = 63 - v.leading_zeros() as usize;
        let bucket = e * 128 + (((v - (1 << e)) * 128) >> e) as usize;
        *self.buckets.entry(bucket).or_default() += 1;
        self.count += 1;
        self.sum += us;
        self.max = self.max.max(us);
    }
    pub fn merge(&mut self, other: &Self) {
        for (b, c) in &other.buckets {
            *self.buckets.entry(*b).or_default() += c
        }
        self.count += other.count;
        self.sum += other.sum;
        self.max = self.max.max(other.max);
        self.overflow += other.overflow;
    }
    pub fn percentile(&self, q: f64) -> Option<f64> {
        if self.count == 0 {
            return None;
        };
        let rank = (self.count as f64 * q).ceil() as i64;
        let mut seen = 0;
        for (b, c) in &self.buckets {
            seen += c;
            if seen >= rank {
                let basis = 1u64 << (b >> 7);
                return Some(
                    (basis + (((b & 127) as u64 + 1) * basis + 127) / 128 - 1) as f64 / 1000.,
                );
            }
        }
        None
    }
    pub fn snapshot(&self) -> Value {
        json!({"count":self.count,"sumMicroseconds":self.sum,"maxMicroseconds":self.max,"overflow":self.overflow,"buckets":self.buckets,"format":"binary128-us-v1","meanMs":if self.count>0{Some(self.sum/self.count as f64/1000.)}else{None},"p50Ms":self.percentile(0.5),"p95Ms":self.percentile(0.95),"p99Ms":self.percentile(0.99)})
    }
}
pub fn vectors() -> Value {
    let mut rng = Random(0);
    let mut h = Histogram::default();
    for us in [0.1, 1., 1.1, 127.5, 128., 129.2, 1024., 4294967297.] {
        h.add(us)
    }
    json!({"generator":GENERATOR,"random":[format!("{:016x}",rng.next()),format!("{:016x}",rng.next()),format!("{:016x}",rng.next())],"rows":[row(1,42,16),row(1000,42,16),row(7,u64::MAX,16)],"histogram":h.snapshot()})
}
