package main

import (
	"bufio"
	"encoding/json"
	"fmt"
	"math"
	"os"
	"os/signal"
	"runtime"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/shirou/gopsutil/v4/process"
)

type endpoint struct {
	Host     string `json:"host"`
	Port     int    `json:"port"`
	Username string `json:"username"`
	Password string `json:"password"`
	Database string `json:"database"`
	FilePath string `json:"filePath"`
}
type stage struct {
	Name    string  `json:"name"`
	Clients int     `json:"clients"`
	Seconds float64 `json:"seconds"`
	Rate    *int    `json:"operationsPerSecond"`
}
type plan struct {
	Stages []stage `json:"stages"`
}
type config struct {
	Duration  float64  `json:"durationSeconds"`
	Warmup    float64  `json:"warmupSeconds"`
	Payload   int      `json:"payloadBytes"`
	Batch     int      `json:"batchSize"`
	Seed      uint64   `json:"seed"`
	Hotspot   bool     `json:"hotspot"`
	LoadModel string   `json:"loadModel"`
	Rate      int      `json:"targetOpsPerSecond"`
	MaxLag    float64  `json:"maxStartLagMs"`
	Scenarios []string `json:"scenarios"`
	Plan      *plan    `json:"plan"`
}

func (c *config) UnmarshalJSON(data []byte) error {
	type plain config
	value := plain{Duration: 5, Warmup: 2, Payload: 256, Batch: 1000, Seed: 42, LoadModel: "closedLoop", Rate: 1000, MaxLag: 20}
	if err := json.Unmarshal(data, &value); err != nil {
		return err
	}
	*c = config(value)
	return nil
}

type runCase struct {
	RunID    string   `json:"runId"`
	Target   string   `json:"target"`
	Rows     int64    `json:"rows"`
	Clients  int      `json:"clients"`
	Repeat   int      `json:"repeat"`
	Config   config   `json:"config"`
	Endpoint endpoint `json:"endpoint"`
	Engine   string   `json:"engine"`
}
type command struct {
	Protocol int      `json:"protocolVersion"`
	Type     string   `json:"type"`
	Run      *runCase `json:"run"`
}

var stop atomic.Bool
var output sync.Mutex
var scenarios = []string{"PointRead", "RangeRead", "Aggregate", "AnalyticalScan", "Update", "Mixed", "Insert", "BatchInsert", "Delete"}

func capabilities(target string) []string {
	switch target {
	case "clickHouse":
		return []string{"RangeRead", "Aggregate", "AnalyticalScan", "BatchInsert"}
	case "redis", "valkey":
		return []string{"PointRead", "Update", "Mixed", "Insert", "BatchInsert", "Delete"}
	default:
		return []string{"PointRead", "RangeRead", "Aggregate", "Update", "Mixed", "Insert", "BatchInsert", "Delete"}
	}
}
func contains(items []string, s string) bool {
	for _, v := range items {
		if v == s {
			return true
		}
	}
	return false
}
func runtimeVersion() string { return runtime.Version() + " / " + runtime.GOOS + "-" + runtime.GOARCH }
func emit(run *runCase, kind string, data any) {
	id := ""
	if run != nil {
		id = run.RunID
	}
	output.Lock()
	defer output.Unlock()
	json.NewEncoder(os.Stdout).Encode(map[string]any{"protocolVersion": 1, "type": kind, "runId": id, "data": data})
}
func base(run *runCase, phase string) map[string]any {
	return map[string]any{"engine": "go", "target": run.Target, "datasetRows": run.Rows, "clients": run.Clients, "repeat": run.Repeat, "phase": phase}
}
func check() error {
	if stop.Load() {
		return fmt.Errorf("cancelled")
	}
	return nil
}
func batchSize(c config) int { return min(c.Batch, max(1, 8*1024*1024/(c.Payload+128))) }
func selfTest() {
	rng := random{0}
	h := newHistogram()
	for _, v := range []float64{.1, 1, 1.1, 127.5, 128, 129.2, 1024, 4294967297} {
		h.add(v)
	}
	json.NewEncoder(os.Stdout).Encode(map[string]any{"generator": generator, "random": []string{fmt.Sprintf("%016x", rng.next()), fmt.Sprintf("%016x", rng.next()), fmt.Sprintf("%016x", rng.next())}, "rows": []dataRow{createRow(1, 42, 16), createRow(1000, 42, 16), createRow(7, ^uint64(0), 16)}, "histogram": h.snapshot()})
}

type counters struct {
	Success, Rows, Errors, Dropped int64
	Service, End, Failed           *histogram
	Examples                       map[string]int64
	Finished                       time.Time
}

func measure(run *runCase, sessions []*database, sequences []int64, deleted *atomic.Int64, operation string, seconds float64, clients int, rate *int, stamp map[string]any, progress bool) (map[string]any, error) {
	var start time.Time
	startSignal := make(chan struct{})
	var ready, done sync.WaitGroup
	ready.Add(clients)
	done.Add(clients)
	counts := make([]*counters, clients)
	var liveSuccess, liveErrors atomic.Int64
	planned := int64(0)
	if rate != nil {
		planned = int64(math.Floor(seconds * float64(*rate)))
	}
	config := run.Config
	number := uint64(0)
	if stamp != nil {
		number = uint64(stamp["number"].(int))
	}
	scenarioIndex := 0
	for i, s := range scenarios {
		if s == operation {
			scenarioIndex = i
		}
	}
	for index := 0; index < clients; index++ {
		c := &counters{Service: newHistogram(), End: newHistogram(), Failed: newHistogram(), Examples: map[string]int64{}}
		counts[index] = c
		go func(index int) {
			defer done.Done()
			ready.Done()
			<-startSignal
			rng := random{config.Seed ^ uint64(index+1)*104729 ^ uint64(run.Repeat) ^ number*32452843}
			sequence, slot := sequences[index], int64(index)
			deadline := start.Add(time.Duration(seconds * float64(time.Second)))
			for !stop.Load() && time.Now().Before(deadline) {
				scheduled := time.Now()
				if rate != nil {
					if slot >= planned {
						break
					}
					scheduled = start.Add(time.Duration(float64(slot) / float64(*rate) * float64(time.Second)))
					lag := time.Since(scheduled).Seconds() - config.MaxLag/1000
					if lag > 0 {
						skipped := min(max(int64(1), int64(math.Floor(lag*float64(*rate)/float64(clients)))+1), (planned-1-slot)/int64(clients)+1)
						c.Dropped += skipped
						slot += skipped * int64(clients)
						continue
					}
					for time.Now().Before(scheduled) && !stop.Load() {
						time.Sleep(time.Millisecond)
					}
					if stop.Load() || !time.Now().Before(deadline) {
						break
					}
					if time.Since(scheduled).Seconds() > config.MaxLag/1000 {
						c.Dropped++
						slot += int64(clients)
						continue
					}
				}
				selected := operation
				if operation == "Mixed" {
					selected = "Update"
					if rng.next()%100 < 80 {
						selected = "PointRead"
					}
				}
				key := rng.key(run.Rows, config.Hotspot)
				var one *dataRow
				var batch []dataRow
				if selected == "Insert" || selected == "BatchInsert" {
					size := 1
					if selected == "BatchInsert" {
						size = batchSize(config)
					}
					first := int64(scenarioIndex+1)*1000000000000 + (sequence*int64(run.Clients)+int64(index))*int64(size) + 1
					if selected == "Insert" {
						value := createRow(first, config.Seed, config.Payload)
						one = &value
					} else {
						batch = make([]dataRow, size)
						for i := range batch {
							batch[i] = createRow(first+int64(i), config.Seed, config.Payload)
						}
					}
				}
				if selected == "Delete" {
					key = sequence*int64(clients) + int64(index) + 1
					if stamp != nil {
						key = deleted.Add(1)
					}
					if key > run.Rows {
						break
					}
				}
				before := time.Now()
				affected, err := sessions[index].execute(selected, key, one, batch)
				finish := time.Now()
				if err == nil {
					c.Success++
					c.Rows += affected
					liveSuccess.Add(1)
					c.Service.add(math.Max(1, finish.Sub(before).Seconds()*1e6))
					origin := before
					if rate != nil {
						origin = scheduled
					}
					c.End.add(math.Max(1, finish.Sub(origin).Seconds()*1e6))
				} else {
					c.Errors++
					liveErrors.Add(1)
					c.Failed.add(math.Max(1, finish.Sub(before).Seconds()*1e6))
					message := err.Error()
					if run.Endpoint.Password != "" {
						message = strings.ReplaceAll(message, run.Endpoint.Password, "[redacted]")
					}
					if len(message) > 250 {
						message = message[:250]
					}
					c.Examples[message]++
				}
				sequence++
				slot += int64(clients)
				if c.Errors >= 50 {
					break
				}
			}
			if rate != nil && slot < planned && !stop.Load() {
				c.Dropped += (planned-1-slot)/int64(clients) + 1
			}
			sequences[index] = sequence
			c.Finished = time.Now()
		}(index)
	}
	ready.Wait()
	p, err := process.NewProcess(int32(os.Getpid()))
	if err != nil {
		return nil, err
	}
	cpuBefore, err := p.Times()
	if err != nil {
		return nil, err
	}
	memory, err := p.MemoryInfo()
	if err != nil {
		return nil, err
	}
	rss := float64(memory.RSS) / 1048576
	var gcBefore runtime.MemStats
	runtime.ReadMemStats(&gcBefore)
	start = time.Now()
	close(startSignal)
	finished := make(chan struct{})
	go func() { done.Wait(); close(finished) }()
	sample := time.NewTicker(250 * time.Millisecond)
	defer sample.Stop()
	reportAt := start.Add(time.Second)
loop:
	for {
		select {
		case <-finished:
			break loop
		case <-sample.C:
			if memory, e := p.MemoryInfo(); e == nil {
				rss = math.Max(rss, float64(memory.RSS)/1048576)
			}
			if progress && time.Now().After(reportAt) {
				var stageName any
				if stamp != nil {
					stageName = stamp["name"]
				}
				emit(run, "progress", map[string]any{"phase": operation, "stage": stageName, "clients": clients, "targetSeconds": seconds, "successful": liveSuccess.Load(), "errors": liveErrors.Load(), "elapsedSeconds": time.Since(start).Seconds()})
				reportAt = time.Now().Add(time.Second)
			}
		}
	}
	end := start
	service, e2e, failed := newHistogram(), newHistogram(), newHistogram()
	var success, errors, dropped, affected int64
	examples := map[string]int64{}
	for _, c := range counts {
		if c.Finished.After(end) {
			end = c.Finished
		}
		service.merge(c.Service)
		e2e.merge(c.End)
		failed.merge(c.Failed)
		success += c.Success
		errors += c.Errors
		dropped += c.Dropped
		affected += c.Rows
		for key, value := range c.Examples {
			examples[key] += value
		}
	}
	elapsed := math.Max(1e-6, end.Sub(start).Seconds())
	cpuAfter, err := p.Times()
	if err != nil {
		return nil, err
	}
	memory, err = p.MemoryInfo()
	if err != nil {
		return nil, err
	}
	rss = math.Max(rss, float64(memory.RSS)/1048576)
	var gcAfter runtime.MemStats
	runtime.ReadMemStats(&gcAfter)
	result := base(run, operation)
	result["stage"] = stamp
	result["clients"] = clients
	result["unit"] = "ops/s"
	if operation == "BatchInsert" {
		result["unit"] = "rows/s"
	}
	result["elapsedSeconds"] = elapsed
	result["successfulOperations"] = success
	result["successfulRows"] = affected
	result["errors"] = errors
	result["dropped"] = dropped
	result["plannedOperations"] = success + errors + dropped
	if rate != nil && !stop.Load() {
		result["plannedOperations"] = planned
	}
	result["operationsPerSecond"] = float64(success) / elapsed
	if contains([]string{"Insert", "BatchInsert", "Update", "Delete"}, operation) {
		result["rowsPerSecond"] = float64(affected) / elapsed
	}
	result["serviceLatency"] = service.snapshot()
	result["endToEndLatency"] = e2e.snapshot()
	result["errorLatency"] = failed.snapshot()
	result["client"] = map[string]any{"cpuPercent": (cpuAfter.Total() - cpuBefore.Total()) / elapsed * 100, "rssEndMb": float64(memory.RSS) / 1048576, "rssSampledMaxMb": rss, "allocatedBytes": gcAfter.TotalAlloc - gcBefore.TotalAlloc, "gen0Collections": nil, "gen1Collections": nil, "gen2Collections": nil, "gcPauseMs": float64(gcAfter.PauseTotalNs-gcBefore.PauseTotalNs) / 1e6}
	keys := make([]string, 0, len(examples))
	for key := range examples {
		keys = append(keys, key)
	}
	sort.Slice(keys, func(i, j int) bool { return examples[keys[i]] > examples[keys[j]] })
	limited := map[string]int64{}
	for _, key := range keys[:min(8, len(keys))] {
		limited[key] = examples[key]
	}
	result["errorExamples"] = limited
	result["cancelled"] = stop.Load()
	return result, nil
}
func openSessions(run *runCase, count int) ([]*database, error) {
	sessions := make([]*database, count)
	var wg sync.WaitGroup
	limit := make(chan struct{}, 32)
	var failure error
	var mutex sync.Mutex
	for i := range sessions {
		wg.Add(1)
		go func(index int) {
			defer wg.Done()
			limit <- struct{}{}
			defer func() { <-limit }()
			if stop.Load() {
				return
			}
			d, err := openDatabase(run)
			if err != nil {
				mutex.Lock()
				if failure == nil {
					failure = err
				}
				mutex.Unlock()
			} else {
				sessions[index] = d
			}
		}(i)
	}
	wg.Wait()
	if failure != nil || stop.Load() {
		for _, d := range sessions {
			if d != nil {
				d.close()
			}
		}
		if failure == nil {
			failure = fmt.Errorf("cancelled")
		}
		return nil, failure
	}
	return sessions, nil
}
func closeSessions(sessions []*database) {
	for _, d := range sessions {
		d.close()
	}
}
func benchmark(run *runCase) error {
	var db *database
	var err error
	deadline := time.Now().Add(90 * time.Second)
	for {
		if err = check(); err != nil {
			return err
		}
		db, err = openDatabase(run)
		if err == nil {
			break
		}
		if run.Target == "sqlite" || time.Now().After(deadline) {
			return err
		}
		time.Sleep(500 * time.Millisecond)
	}
	defer db.close()
	metadata, err := db.metadata()
	if err != nil {
		return err
	}
	metadata["settings"].(map[string]string)["effective_batch_size"] = fmt.Sprint(batchSize(run.Config))
	emit(run, "metadata", metadata)
	if err = db.reset(); err != nil {
		return err
	}
	emit(run, "phase_start", map[string]any{"phase": "bulk_load", "targetSeconds": 0})
	start := time.Now()
	var generation float64
	var amount int64
	batch := batchSize(run.Config)
	reportAt := start.Add(time.Second)
	for first := int64(1); first <= run.Rows; first += int64(batch) {
		if err = check(); err != nil {
			return err
		}
		before := time.Now()
		rows := make([]dataRow, min(int64(batch), run.Rows-first+1))
		for i := range rows {
			rows[i] = createRow(first+int64(i), run.Config.Seed, run.Config.Payload)
		}
		generation += time.Since(before).Seconds()
		if err = db.batch(rows); err != nil {
			return err
		}
		for _, row := range rows {
			amount += row.Amount
		}
		if time.Now().After(reportAt) {
			emit(run, "progress", map[string]any{"phase": "bulk_load", "loaded": first + int64(len(rows)) - 1, "total": run.Rows})
			reportAt = time.Now().Add(time.Second)
		}
	}
	elapsed := math.Max(1e-6, time.Since(start).Seconds())
	if err = check(); err != nil {
		return err
	}
	digest, err := db.digest()
	if err != nil {
		return err
	}
	expected := [3]int64{run.Rows, run.Rows * (run.Rows + 1) / 2, amount}
	if digest != expected {
		return fmt.Errorf("dataset digest mismatch: %v != %v", digest, expected)
	}
	result := base(run, "bulk_load")
	result["unit"] = "rows/s"
	result["elapsedSeconds"] = elapsed
	result["successfulRows"] = run.Rows
	result["successfulOperations"] = (run.Rows + int64(batch) - 1) / int64(batch)
	result["rowsPerSecond"] = float64(run.Rows) / elapsed
	result["generationSeconds"] = generation
	result["verified"] = true
	emit(run, "result", result)
	if !contains([]string{"redis", "valkey", "clickHouse"}, run.Target) {
		if err = check(); err != nil {
			return err
		}
		emit(run, "phase_start", map[string]any{"phase": "index_build", "targetSeconds": 0})
		before := time.Now()
		if err = db.prepare(); err != nil {
			return err
		}
		result = base(run, "index_build")
		result["unit"] = "seconds"
		result["elapsedSeconds"] = time.Since(before).Seconds()
		result["successfulOperations"] = 1
		result["verified"] = true
		emit(run, "result", result)
	}
	requested := make([]string, len(run.Config.Scenarios))
	for i, s := range run.Config.Scenarios {
		requested[i] = strings.ToUpper(s[:1]) + s[1:]
	}
	supported := capabilities(run.Target)
	for _, s := range requested {
		if !contains(supported, s) {
			emit(run, "unsupported", map[string]any{"scenario": s, "message": "Not supported by this database profile"})
		}
	}
	operations := []string{}
	for _, s := range scenarios {
		if !contains(supported, s) {
			continue
		}
		if len(requested) > 0 {
			if contains(requested, s) {
				operations = append(operations, s)
			}
		} else if run.Config.Plan == nil || s == supported[0] {
			operations = append(operations, s)
		}
	}
	if run.Config.Warmup > 0 {
		count := run.Clients
		if run.Config.Plan != nil {
			count = run.Config.Plan.Stages[0].Clients
		}
		sessions, e := openSessions(run, count)
		if e != nil {
			return e
		}
		var warmupRate *int
		if run.Config.Plan == nil && run.Config.LoadModel == "constantRate" {
			warmupRate = &run.Config.Rate
		}
		warmup, e := measure(run, sessions, make([]int64, count), &atomic.Int64{}, supported[0], run.Config.Warmup, count, warmupRate, nil, false)
		closeSessions(sessions)
		if e != nil {
			return e
		}
		if warmup["errors"].(int64) > 0 {
			return fmt.Errorf("warmup errors")
		}
	}
	expectedCount := run.Rows
	for _, operation := range operations {
		if err = check(); err != nil {
			return err
		}
		sessions := []*database{}
		sequences := make([]int64, run.Clients)
		deleted := &atomic.Int64{}
		stages := []stage{{Clients: run.Clients, Seconds: run.Config.Duration}}
		if run.Config.LoadModel == "constantRate" {
			stages[0].Rate = &run.Config.Rate
		}
		if run.Config.Plan != nil {
			stages = run.Config.Plan.Stages
		}
		err = func() error {
			defer func() { closeSessions(sessions) }()
			for index, stage := range stages {
				if err := check(); err != nil {
					return err
				}
				if len(sessions) < stage.Clients {
					more, err := openSessions(run, stage.Clients-len(sessions))
					if err != nil {
						return err
					}
					sessions = append(sessions, more...)
				}
				var stamp map[string]any
				if run.Config.Plan != nil {
					stamp = map[string]any{"number": index + 1, "name": stage.Name, "targetSeconds": stage.Seconds, "operationsPerSecond": stage.Rate}
				}
				emit(run, "phase_start", map[string]any{"phase": operation, "targetSeconds": stage.Seconds, "stage": stamp, "clients": stage.Clients})
				result, err := measure(run, sessions, sequences, deleted, operation, stage.Seconds, stage.Clients, stage.Rate, stamp, true)
				if err != nil {
					return err
				}
				emit(run, "phase_end", map[string]any{})
				if operation == "Insert" || operation == "BatchInsert" {
					expectedCount += result["successfulRows"].(int64)
				}
				if operation == "Delete" {
					expectedCount -= result["successfulRows"].(int64)
				}
				actual, err := db.count()
				if err != nil {
					return err
				}
				result["verified"] = actual == expectedCount && result["successfulOperations"].(int64) > 0 && !stop.Load()
				result["cancelled"] = stop.Load()
				emit(run, "result", result)
				if actual != expectedCount {
					return fmt.Errorf("count mismatch: %d != %d", actual, expectedCount)
				}
				if run.Config.Plan == nil && result["successfulOperations"].(int64) == 0 && !stop.Load() {
					return fmt.Errorf("no successful operations")
				}
			}
			return nil
		}()
		if err != nil {
			return err
		}
	}
	return nil
}
func main() {
	if len(os.Args) > 1 && os.Args[1] == "--self-test" {
		selfTest()
		return
	}
	emit(nil, "hello", map[string]any{"engine": "go", "runtime": runtimeVersion(), "generator": generator, "histogram": "binary128-us-v1"})
	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 65536), 1048577)
	var run *runCase
	code := 1
	defer func() {
		if value := recover(); value != nil {
			message := fmt.Sprint(value)
			if run != nil && run.Endpoint.Password != "" {
				message = strings.ReplaceAll(message, run.Endpoint.Password, "[redacted]")
			}
			emit(run, "failed", map[string]any{"message": message})
			os.Exit(1)
		}
	}()
	if !scanner.Scan() {
		emit(nil, "failed", map[string]any{"message": "Missing run command"})
		os.Exit(1)
	}
	var c command
	err := json.Unmarshal([]byte(strings.TrimPrefix(scanner.Text(), "\ufeff")), &c)
	if err == nil && (c.Protocol != 1 || c.Type != "run" || c.Run == nil) {
		err = fmt.Errorf("expected protocol v1 run")
	}
	if err == nil {
		run = c.Run
		if run.Rows < 100 || run.Clients < 1 || run.Config.Payload < 16 || batchSize(run.Config) < 1 || run.Engine != "" && run.Engine != "go" {
			err = fmt.Errorf("invalid run")
		}
	}
	if err == nil {
		go func() {
			for scanner.Scan() {
				var next command
				if json.Unmarshal(scanner.Bytes(), &next) != nil {
					stop.Store(true)
					return
				}
				if next.Protocol == 1 && next.Type == "cancel" {
					stop.Store(true)
					return
				}
			}
			stop.Store(true)
		}()
		signals := make(chan os.Signal, 1)
		signal.Notify(signals, os.Interrupt)
		go func() { <-signals; stop.Store(true) }()
		err = benchmark(run)
	}
	if err == nil || stop.Load() {
		emit(run, "completed", map[string]any{"cancelled": stop.Load()})
		code = 0
		if stop.Load() {
			code = 2
		}
	} else {
		message := err.Error()
		if run != nil && run.Endpoint.Password != "" {
			message = strings.ReplaceAll(message, run.Endpoint.Password, "[redacted]")
		}
		emit(run, "failed", map[string]any{"message": message})
	}
	os.Exit(code)
}
