package main

import "math"

const generator = "splitmix64-ascii-v1"
const alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"

type random struct{ state uint64 }

func (r *random) next() uint64 {
	r.state += 0x9e3779b97f4a7c15
	z := r.state
	z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9
	z = (z ^ (z >> 27)) * 0x94d049bb133111eb
	return z ^ (z >> 31)
}
func (r *random) key(count int64, hotspot bool) int64 {
	if !hotspot {
		return int64(r.next()%uint64(count)) + 1
	}
	cutoff := count / 5
	if cutoff < 1 {
		cutoff = 1
	}
	if r.next()%100 < 80 {
		return int64(r.next()%uint64(cutoff)) + 1
	}
	return cutoff + int64(r.next()%uint64(count-cutoff)) + 1
}

type dataRow struct {
	ID      int64  `json:"id"`
	Group   int32  `json:"groupId"`
	Amount  int64  `json:"amountCents"`
	Version int64  `json:"version"`
	Payload string `json:"payload"`
}

func createRow(id int64, seed uint64, size int) dataRow {
	rng := random{seed ^ (uint64(id) * 0xd6e8feb86659fd93)}
	amount := int64(rng.next()%1000000) + 1
	payload := make([]byte, size)
	for i := range payload {
		payload[i] = alphabet[rng.next()%62]
	}
	return dataRow{id, int32((id - 1) % 100), amount, 0, string(payload)}
}

type histogram struct {
	Count    int64         `json:"count"`
	Sum      float64       `json:"sumMicroseconds"`
	Max      float64       `json:"maxMicroseconds"`
	Overflow int64         `json:"overflow"`
	Buckets  map[int]int64 `json:"buckets"`
}

func newHistogram() *histogram { return &histogram{Buckets: make(map[int]int64)} }
func (h *histogram) add(us float64) {
	v := uint64(math.Max(1, math.Ceil(us)))
	if v >= 1<<32 {
		h.Overflow++
		v = (1 << 32) - 1
	}
	e := 0
	for x := v; x > 1; x >>= 1 {
		e++
	}
	bucket := e*128 + int(((v-(1<<e))*128)>>e)
	h.Buckets[bucket]++
	h.Count++
	h.Sum += us
	h.Max = math.Max(h.Max, us)
}
func (h *histogram) merge(o *histogram) {
	for b, c := range o.Buckets {
		h.Buckets[b] += c
	}
	h.Count += o.Count
	h.Sum += o.Sum
	h.Max = math.Max(h.Max, o.Max)
	h.Overflow += o.Overflow
}
func (h *histogram) percentile(q float64) any {
	if h.Count == 0 {
		return nil
	}
	rank := int64(math.Ceil(float64(h.Count) * q))
	var seen int64
	for b := 0; b < 4096; b++ {
		seen += h.Buckets[b]
		if seen >= rank {
			basis := uint64(1) << (b >> 7)
			upper := basis + (uint64(b&127)+1)*basis/128
			if ((uint64(b&127)+1)*basis)%128 != 0 {
				upper++
			}
			return float64(upper-1) / 1000
		}
	}
	return nil
}
func (h *histogram) snapshot() map[string]any {
	var mean any
	if h.Count > 0 {
		mean = h.Sum / float64(h.Count) / 1000
	}
	return map[string]any{"count": h.Count, "sumMicroseconds": h.Sum, "maxMicroseconds": h.Max, "overflow": h.Overflow, "buckets": h.Buckets, "format": "binary128-us-v1", "meanMs": mean, "p50Ms": h.percentile(.5), "p95Ms": h.percentile(.95), "p99Ms": h.percentile(.99)}
}
