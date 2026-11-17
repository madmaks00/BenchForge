package main

import (
	"bufio"
	"context"
	"database/sql"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"math/big"
	"os"
	"strconv"
	"strings"
	"sync/atomic"
	"time"
)

type sqlField struct {
	Name, Type, Generator, Prefix, Reference, SampleColumn string
	Min, Max                                               json.Number
	Length, Scale, SampleSize                              int
	Choices                                                []string
	Constant                                               *string
	Nullable                                               bool
	NullPercent                                            float64
}

type sqlCommand struct {
	SQL   string
	Names []string
}
type sqlStep struct {
	Result, CaptureType       string
	CaptureAs, ExpectedScalar *string
	MinRows, MaxRows          *int64
}
type sqlOperation struct {
	Definition struct {
		ID, Name, Isolation   string
		Weight                uint64
		ReadOnly, Transaction bool
		Parameters            []sqlField
		Steps                 []sqlStep
	}
	Commands []sqlCommand
}
type sqlCheck struct {
	Definition struct {
		Name, Expected, Comparison string
		Before, After              bool
	}
	Command sqlCommand
}
type sqlDataset struct {
	Definition struct {
		Name    string
		Columns []sqlField
	}
	Rows      int64
	Seed      string
	Insert    sqlCommand
	InputFile string
}
type sqlProgram struct {
	Fingerprint             string
	Drop, Schema, AfterLoad []string
	Datasets                []sqlDataset
	Operations              []sqlOperation
	Checks                  []sqlCheck
	Units                   []struct{ Phase, Operation string }
}
type sqlWorkload struct {
	Name, Mode                              string
	ResetBetweenOperations, PrepareCommands bool
	CommandTimeoutSeconds                   int
}
type sqlValueContext struct{ sequence, client, key int64 }
type reservoir struct {
	values       []any
	column, size int
	seen         uint64
	random       random
}
type customState struct {
	run         *runCase
	program     *sqlProgram
	rows        map[string]int64
	samples     map[string]*reservoir
	sequence    atomic.Int64
	completed   int64
	byOperation map[string]int64
}
type customSession struct {
	state    *customState
	db       *sql.DB
	client   int64
	options  []sqlOperation
	mixed    bool
	weight   uint64
	random   random
	details  map[string]*customDetail
	prepared map[string]*sql.Stmt
}
type customDetail struct {
	id, name              string
	success, rows, errors int64
	service, end          *histogram
}

func sqlStableSeed(seed uint64, name string, row int64) uint64 {
	hash := uint64(14695981039346656037)
	for _, b := range []byte(name) {
		hash = (hash ^ uint64(b)) * 1099511628211
	}
	return seed ^ hash ^ uint64(row)*0xd6e8feb86659fd93
}
func sampleKey(f sqlField) string {
	return fmt.Sprintf("%s/%s/%d", f.Reference, f.SampleColumn, f.SampleSize)
}

func integerRange(rng *random, min, max int64) int64 {
	width := uint64(max) - uint64(min) + 1
	value := rng.next()
	if width != 0 {
		value %= width
	}
	return int64(uint64(min) + value)
}

func normalizeSqlValue(value any, kind, target string) (any, error) {
	if value == nil {
		return nil, nil
	}
	if t, ok := value.(time.Time); ok {
		value = t.UTC().Format("2006-01-02 15:04:05.000000")
	}
	if data, ok := value.([]byte); ok {
		if target == "oracle" && kind == "guid" && len(data) == 16 {
			return hex.EncodeToString(data), nil
		}
		value = string(data)
	}
	text := fmt.Sprint(value)
	switch kind {
	case "boolean":
		switch strings.ToLower(text) {
		case "1", "true":
			return "1", nil
		case "0", "false":
			return "0", nil
		default:
			return nil, fmt.Errorf("invalid boolean %q", text)
		}
	case "int32", "int64":
		bits := 64
		if kind == "int32" {
			bits = 32
		}
		num, err := strconv.ParseInt(text, 10, bits)
		if err != nil {
			return nil, err
		}
		return strconv.FormatInt(num, 10), nil
	case "dateTime":
		for _, layout := range []string{time.RFC3339Nano, "2006-01-02 15:04:05.999999999", "2006-01-02T15:04:05.999999999", "2006-01-02"} {
			if t, err := time.Parse(layout, text); err == nil {
				return t.UTC().Format("2006-01-02 15:04:05.000000"), nil
			}
		}
		return nil, fmt.Errorf("invalid date %q", text)
	case "guid":
		raw, err := hex.DecodeString(strings.ReplaceAll(text, "-", ""))
		if err != nil || len(raw) != 16 {
			return nil, fmt.Errorf("invalid GUID %q", text)
		}
		if target == "oracle" {
			if len(text) == 36 {
				binary.LittleEndian.PutUint32(raw[:4], binary.BigEndian.Uint32(raw[:4]))
				binary.LittleEndian.PutUint16(raw[4:6], binary.BigEndian.Uint16(raw[4:6]))
				binary.LittleEndian.PutUint16(raw[6:8], binary.BigEndian.Uint16(raw[6:8]))
			}
			return hex.EncodeToString(raw), nil
		}
		return fmt.Sprintf("%x-%x-%x-%x-%x", raw[:4], raw[4:6], raw[6:8], raw[8:10], raw[10:]), nil
	}
	return text, nil
}

func decimalRat(value string) (*big.Rat, error) {
	r, ok := new(big.Rat).SetString(value)
	if !ok {
		return nil, fmt.Errorf("invalid numeric value %q", value)
	}
	return r, nil
}
func roundedDecimal(value *big.Rat, scale int) string {
	multiple := new(big.Int).Exp(big.NewInt(10), big.NewInt(int64(scale)), nil)
	num := new(big.Int).Mul(value.Num(), multiple)
	q, r := new(big.Int), new(big.Int)
	q.QuoRem(num, value.Denom(), r)
	twice := new(big.Int).Lsh(r.Abs(r), 1)
	order := twice.Cmp(value.Denom())
	if order > 0 || order == 0 && q.Bit(0) == 1 {
		q.Add(q, big.NewInt(int64(num.Sign())))
	}
	return new(big.Rat).SetFrac(q, multiple).FloatString(scale)
}

func (s *customState) generate(f sqlField, c sqlValueContext, rng *random) (any, error) {
	if f.NullPercent > 0 && float64(rng.next()%1000000) < f.NullPercent*10000 {
		return nil, nil
	}
	var value any
	switch f.Generator {
	case "sequence", "integer", "timestamp":
		lo, err := f.Min.Int64()
		if err != nil {
			return nil, err
		}
		if f.Generator == "sequence" {
			value = lo + c.sequence - 1
			break
		}
		hi, err := f.Max.Int64()
		if err != nil {
			return nil, err
		}
		n := integerRange(rng, lo, hi)
		value = n
		if f.Generator == "timestamp" {
			value = time.Unix(n, 0).UTC()
		}
	case "decimal":
		lo, err := decimalRat(f.Min.String())
		if err != nil {
			return nil, err
		}
		hi, err := decimalRat(f.Max.String())
		if err != nil {
			return nil, err
		}
		fraction := big.NewRat(int64(rng.next()>>11), 9007199254740992)
		value = roundedDecimal(new(big.Rat).Add(lo, hi.Sub(hi, lo).Mul(hi, fraction)), f.Scale)
	case "text":
		var b strings.Builder
		seq := strconv.FormatInt(c.sequence, 10)
		b.WriteString(strings.NewReplacer("{row}", seq, "{sequence}", seq, "{key}", strconv.FormatInt(c.key, 10), "{client}", strconv.FormatInt(c.client, 10)).Replace(f.Prefix))
		const alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
		for i := 0; i < f.Length; i++ {
			b.WriteByte(alphabet[rng.next()%62])
		}
		value = b.String()
	case "choice":
		value = f.Choices[rng.next()%uint64(len(f.Choices))]
	case "reference":
		count := s.rows[strings.ToLower(f.Reference)]
		if count < 1 {
			return nil, fmt.Errorf("empty reference table %q", f.Reference)
		}
		value = integerRange(rng, 1, count)
	case "guid":
		first, second := rng.next(), rng.next()
		if s.run.Target == "oracle" {
			return fmt.Sprintf("%016x%016x", reverse64(first), reverse64(second)), nil
		}
		var tail [8]byte
		binary.LittleEndian.PutUint64(tail[:], second)
		value = fmt.Sprintf("%08x-%04x-%04x-%x-%x", uint32(first), uint16(first>>32), uint16(first>>48), tail[:2], tail[2:])
	case "boolean":
		value = rng.next()%2 == 0
	case "constant":
		if f.Constant != nil {
			value = *f.Constant
		}
	case "key":
		value = c.key
	case "client":
		value = c.client
	case "sample":
		pool := s.samples[sampleKey(f)]
		if pool == nil || len(pool.values) == 0 {
			return nil, fmt.Errorf("no sample values for %s", sampleKey(f))
		}
		value = pool.values[rng.next()%uint64(len(pool.values))]
	default:
		return nil, fmt.Errorf("unknown generator %q", f.Generator)
	}
	return normalizeSqlValue(value, f.Type, s.run.Target)
}
func reverse64(value uint64) uint64 {
	var buffer [8]byte
	binary.LittleEndian.PutUint64(buffer[:], value)
	return binary.BigEndian.Uint64(buffer[:])
}

func (s *customState) args(command sqlCommand, values map[string]any, ctx sqlValueContext) ([]any, error) {
	args := make([]any, len(command.Names))
	for i, name := range command.Names {
		key := strings.ToLower(name)
		value, ok := values[key]
		if !ok {
			var n int64
			switch key {
			case "key":
				n = ctx.key
			case "sequence":
				n = s.run.Rows + ctx.sequence
			case "client":
				n = ctx.client
			case "baserows":
				n = s.run.Rows
			case "seed":
				n = int64(s.run.Config.Seed)
			case "completed":
				n = s.completed
			default:
				if strings.HasPrefix(key, "rows_") {
					n, ok = s.rows[key[5:]]
				} else if strings.HasPrefix(key, "completed_") {
					n = s.byOperation[key[10:]]
					ok = true
				}
				if !ok {
					return nil, fmt.Errorf("unknown parameter @%s", name)
				}
			}
			value = strconv.FormatInt(n, 10)
		}
		args[i] = value
		if s.run.Target == "sqlServer" {
			args[i] = sql.Named(fmt.Sprintf("p%d", i+1), value)
		}
	}
	return args, nil
}

func customConnection(d *database) (*sql.DB, error) {
	db := d.db
	if d.extra != nil {
		db = d.extra.db
	}
	if db == nil {
		return nil, fmt.Errorf("target has no SQL connection")
	}
	if d.run.Target == "oracle" {
		if _, err := db.Exec("ALTER SESSION SET NLS_NUMERIC_CHARACTERS = '.,'"); err != nil {
			return nil, err
		}
	}
	if d.run.Target == "sqlite" {
		if _, err := db.Exec("PRAGMA foreign_keys=ON"); err != nil {
			return nil, err
		}
	}
	return db, nil
}

type sqlQuerier interface {
	ExecContext(context.Context, string, ...any) (sql.Result, error)
	QueryContext(context.Context, string, ...any) (*sql.Rows, error)
}

func sqlResult(ctx context.Context, connection sqlQuerier, stmt *sql.Stmt, cmd string, values []any, mode string) (any, int64, error) {
	if mode == "nonQuery" {
		var result sql.Result
		var err error
		if stmt == nil {
			result, err = connection.ExecContext(ctx, cmd, values...)
		} else {
			result, err = stmt.ExecContext(ctx, values...)
		}
		if err != nil {
			return nil, 0, err
		}
		count, err := result.RowsAffected()
		return nil, max(0, count), err
	}
	var rows *sql.Rows
	var err error
	if stmt == nil {
		rows, err = connection.QueryContext(ctx, cmd, values...)
	} else {
		rows, err = stmt.QueryContext(ctx, values...)
	}
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()
	var count int64
	for {
		columns, err := rows.Columns()
		if err != nil {
			return nil, 0, err
		}
		cells := make([]any, len(columns))
		pointers := make([]any, len(columns))
		for i := range cells {
			pointers[i] = &cells[i]
		}
		for rows.Next() {
			if err = rows.Scan(pointers...); err != nil {
				return nil, 0, err
			}
			if mode == "scalar" {
				if len(cells) == 0 || cells[0] == nil {
					return nil, 0, nil
				}
				return cells[0], 1, nil
			}
			count++
		}
		if !rows.NextResultSet() {
			break
		}
	}
	return nil, count, rows.Err()
}

func (s *customState) checkData(db *sql.DB, before bool) error {
	for _, item := range s.program.Checks {
		d := item.Definition
		if before && !d.Before || !before && !d.After {
			continue
		}
		args, err := s.args(item.Command, nil, sqlValueContext{})
		if err != nil {
			return err
		}
		ctx, cancel := context.WithTimeout(background, time.Duration(s.run.Config.Custom.CommandTimeoutSeconds)*time.Second)
		actual, _, err := sqlResult(ctx, db, nil, item.Command.SQL, args, "scalar")
		cancel()
		if err != nil {
			return err
		}
		if bytes, ok := actual.([]byte); ok {
			actual = string(bytes)
		}
		left, err := decimalRat(fmt.Sprint(actual))
		if err != nil {
			return err
		}
		expected := d.Expected
		if expected == "baseRows" {
			expected = fmt.Sprint(s.run.Rows)
		} else if strings.HasPrefix(expected, "rows:") {
			expected = fmt.Sprint(s.rows[strings.ToLower(expected[5:])])
		}
		right, err := decimalRat(expected)
		if err != nil {
			return err
		}
		cmp := left.Cmp(right)
		if !(d.Comparison == "atLeast" && cmp >= 0 || d.Comparison == "atMost" && cmp <= 0 || d.Comparison == "equal" && cmp == 0) {
			return fmt.Errorf("integrity check failed: %s", d.Name)
		}
	}
	return nil
}

func (s *customState) reset(db *sql.DB) error {
	s.sequence.Store(0)
	s.completed = 0
	clear(s.byOperation)
	clear(s.samples)
	if s.run.Target == "sqlite" {
		if _, err := db.Exec("PRAGMA foreign_keys=OFF"); err != nil {
			return err
		}
		defer db.Exec("PRAGMA foreign_keys=ON")
	}
	for _, group := range [][]string{s.program.Drop, s.program.Schema} {
		for _, statement := range group {
			if err := check(); err != nil {
				return err
			}
			if _, err := db.Exec(statement); err != nil {
				return err
			}
		}
	}
	return nil
}

func (s *customState) load(db *sql.DB) (int64, error) {
	var loaded int64
	for _, table := range s.program.Datasets {
		count, err := s.loadTable(db, table)
		loaded += count
		if err != nil {
			return loaded, err
		}
	}
	return loaded, nil
}
func (s *customState) loadTable(db *sql.DB, table sqlDataset) (int64, error) {
	var pools []*reservoir
	for _, op := range s.program.Operations {
		for _, f := range op.Definition.Parameters {
			key := sampleKey(f)
			if f.Generator != "sample" || f.Reference != table.Definition.Name || s.samples[key] != nil {
				continue
			}
			for index, column := range table.Definition.Columns {
				if column.Name == f.SampleColumn {
					pool := &reservoir{column: index, size: f.SampleSize, random: random{state: sqlStableSeed(s.run.Config.Seed, key, 0)}}
					s.samples[key] = pool
					pools = append(pools, pool)
					break
				}
			}
		}
	}
	var scanner *bufio.Scanner
	if table.InputFile != "" {
		f, err := os.Open(table.InputFile)
		if err != nil {
			return 0, err
		}
		defer f.Close()
		scanner = bufio.NewScanner(f)
		scanner.Buffer(make([]byte, 65536), 64*1024*1024)
	}
	seed, err := strconv.ParseUint(table.Seed, 10, 64)
	if err != nil {
		return 0, err
	}
	stmt, err := db.Prepare(table.Insert.SQL)
	if err != nil {
		return 0, err
	}
	defer stmt.Close()
	var tx *sql.Tx
	defer func() {
		if tx != nil {
			tx.Rollback()
		}
	}()
	var loaded int64
	var insert *sql.Stmt
	for row := int64(1); row <= table.Rows; row++ {
		if err = check(); err != nil {
			return loaded, err
		}
		if tx == nil {
			tx, err = db.Begin()
			if err != nil {
				return loaded, err
			}
			insert = tx.Stmt(stmt)
		}
		var values []any
		if scanner != nil {
			if !scanner.Scan() {
				if scanner.Err() != nil {
					return loaded, scanner.Err()
				}
				return loaded, fmt.Errorf("input row count changed")
			}
			if err = json.Unmarshal(scanner.Bytes(), &values); err != nil {
				return loaded, err
			}
		} else {
			rng := random{state: seed ^ uint64(row)*0xd6e8feb86659fd93}
			for _, column := range table.Definition.Columns {
				value, err := s.generate(column, sqlValueContext{sequence: row, key: row}, &rng)
				if err != nil {
					return loaded, err
				}
				values = append(values, value)
			}
		}
		if len(values) != len(table.Definition.Columns) {
			return loaded, fmt.Errorf("input column count changed")
		}
		args := make([]any, len(values))
		for i, v := range values {
			if v == nil && !table.Definition.Columns[i].Nullable {
				return loaded, fmt.Errorf("NULL in required field %s", table.Definition.Columns[i].Name)
			}
			args[i] = v
			if s.run.Target == "sqlServer" {
				args[i] = sql.Named(fmt.Sprintf("p%d", i+1), v)
			}
		}
		if _, err = insert.Exec(args...); err != nil {
			return loaded, err
		}
		for _, pool := range pools {
			value := values[pool.column]
			if value == nil {
				continue
			}
			pool.seen++
			if len(pool.values) < pool.size {
				pool.values = append(pool.values, value)
			} else if slot := pool.random.next() % pool.seen; slot < uint64(pool.size) {
				pool.values[slot] = value
			}
		}
		loaded++
		if row%int64(s.run.Config.Batch) == 0 || row == table.Rows {
			if err = tx.Commit(); err != nil {
				return loaded, err
			}
			tx = nil
		}
	}
	return loaded, nil
}

func (s *customState) sessions(list []*database, count int, selected string, warm bool) ([]*database, error) {
	for len(list) < count {
		if err := check(); err != nil {
			return list, err
		}
		d, err := openDatabase(s.run)
		if err != nil {
			return list, err
		}
		db, err := customConnection(d)
		if err != nil {
			d.close()
			return list, err
		}
		client := int64(len(list) + 1)
		session := &customSession{state: s, db: db, client: client, mixed: selected == "", random: random{state: sqlStableSeed(s.run.Config.Seed^uint64(s.run.Repeat), "client", client)}, details: map[string]*customDetail{}, prepared: map[string]*sql.Stmt{}}
		for _, op := range s.program.Operations {
			if (selected == "" || op.Definition.ID == selected) && (!warm || op.Definition.ReadOnly) {
				session.options = append(session.options, op)
				session.weight += op.Definition.Weight
			}
		}
		d.custom = session
		list = append(list, d)
	}
	return list, nil
}

func newCustomDetail(id, name string) *customDetail {
	return &customDetail{id: id, name: name, service: newHistogram(), end: newHistogram()}
}
func (client *customSession) execute(key int64, scheduled time.Time) (affected int64, err error) {
	started := time.Now()
	op := client.options[0]
	if client.mixed {
		choice := client.random.next() % client.weight
		for _, candidate := range client.options {
			if choice < candidate.Definition.Weight {
				op = candidate
				break
			}
			choice -= candidate.Definition.Weight
		}
	}
	d := op.Definition
	detail := client.details[d.ID]
	if detail == nil {
		detail = newCustomDetail(d.ID, d.Name)
		client.details[d.ID] = detail
	}
	defer func() {
		if err != nil {
			detail.errors++
			return
		}
		detail.success++
		detail.rows += affected
		end := time.Now()
		detail.service.add(end.Sub(started).Seconds() * 1e6)
		detail.end.add(end.Sub(scheduled).Seconds() * 1e6)
	}()
	s := client.state
	c := sqlValueContext{sequence: s.sequence.Add(1), client: client.client, key: key}
	values := make(map[string]any, len(d.Parameters))
	for _, p := range d.Parameters {
		value, err := s.generate(p, c, &client.random)
		if err != nil {
			return 0, err
		}
		values[strings.ToLower(p.Name)] = value
	}
	var tx *sql.Tx
	var conn sqlQuerier = client.db
	timeout := time.Duration(s.run.Config.Custom.CommandTimeoutSeconds) * time.Second
	ctx, cancel := context.WithTimeout(background, timeout*time.Duration(max(1, len(d.Steps)+1)))
	defer cancel()
	if d.Transaction {
		level := map[string]sql.IsolationLevel{"readCommitted": sql.LevelReadCommitted, "repeatableRead": sql.LevelRepeatableRead, "serializable": sql.LevelSerializable}[d.Isolation]
		if s.run.Target == "sqlite" || s.run.Target == "duckDb" {
			if level != sql.LevelDefault && level != sql.LevelSerializable {
				return 0, fmt.Errorf("unsupported isolation %s", d.Isolation)
			}
			level = sql.LevelDefault
		}
		tx, err = client.db.BeginTx(ctx, &sql.TxOptions{Isolation: level})
		if err != nil {
			return 0, err
		}
		defer tx.Rollback()
		conn = tx
	}
	for i, step := range d.Steps {
		cmd := op.Commands[i]
		args, err := s.args(cmd, values, c)
		if err != nil {
			return 0, err
		}
		var stmt *sql.Stmt
		// go-ora's reusable query cursor can become invalid after a fetch completes.
		// Keep bound parameters and let the Oracle driver prepare the active request.
		if s.run.Config.Custom.PrepareCommands && tx == nil && s.run.Target != "oracle" {
			stmt = client.prepared[cmd.SQL]
			if stmt == nil {
				stmt, err = client.db.PrepareContext(ctx, cmd.SQL)
				if err != nil {
					return 0, err
				}
				client.prepared[cmd.SQL] = stmt
			}
		}
		stepCtx, stepCancel := context.WithTimeout(ctx, timeout)
		scalar, count, err := sqlResult(stepCtx, conn, stmt, cmd.SQL, args, step.Result)
		stepCancel()
		if err != nil {
			return 0, err
		}
		if bytes, ok := scalar.([]byte); ok && step.CaptureType != "guid" {
			scalar = string(bytes)
		}
		if step.ExpectedScalar != nil {
			actual := fmt.Sprint(scalar)
			if scalar == nil {
				actual = ""
			}
			if actual != *step.ExpectedScalar {
				a, e := decimalRat(actual)
				b, f := decimalRat(*step.ExpectedScalar)
				if e != nil || f != nil || a.Cmp(b) != 0 {
					return 0, fmt.Errorf("unexpected scalar result")
				}
			}
		}
		if step.MinRows != nil && count < *step.MinRows || step.MaxRows != nil && count > *step.MaxRows {
			return 0, fmt.Errorf("unexpected SQL row count")
		}
		if step.CaptureAs != nil {
			value, err := normalizeSqlValue(scalar, step.CaptureType, s.run.Target)
			if err != nil {
				return 0, err
			}
			values[strings.ToLower(*step.CaptureAs)] = value
		}
		affected += count
	}
	if tx != nil {
		err = tx.Commit()
	}
	return affected, err
}

func (s *customState) setup(phase string, loading bool, action func() (int64, error)) error {
	emit(s.run, "phase_start", map[string]any{"phase": phase, "targetSeconds": 0})
	start := time.Now()
	count, err := action()
	emit(s.run, "phase_end", map[string]any{})
	result := base(s.run, phase)
	result["unit"] = "seconds"
	result["elapsedSeconds"] = max(.000001, time.Since(start).Seconds())
	result["verified"] = err == nil
	if err != nil {
		if stop.Load() {
			return err
		}
		result["errors"] = 1
		message := err.Error()
		if password := s.run.Endpoint.Password; password != "" {
			message = strings.ReplaceAll(message, password, "[redacted]")
		}
		result["errorExamples"] = map[string]int{message: 1}
	} else {
		result["successfulOperations"] = 1
		result["successfulRows"] = count
		if loading {
			result["unit"] = "rows/s"
			result["rowsPerSecond"] = float64(count) / result["elapsedSeconds"].(float64)
		}
	}
	emit(s.run, "result", result)
	return err
}
func benchmarkCustom(run *runCase, native *database) error {
	if run.SqlProgram == nil {
		return fmt.Errorf("compiled SQL program missing; update BenchForge")
	}
	s := &customState{run: run, program: run.SqlProgram, rows: map[string]int64{}, samples: map[string]*reservoir{}, byOperation: map[string]int64{}}
	for _, table := range s.program.Datasets {
		s.rows[strings.ToLower(table.Definition.Name)] = table.Rows
	}
	db, err := customConnection(native)
	if err != nil {
		return err
	}
	metadata, err := native.metadata()
	if err != nil {
		return err
	}
	settings := metadata["settings"].(map[string]string)
	settings["workload_name"] = run.Config.Custom.Name
	settings["workload_sha256"] = s.program.Fingerprint
	settings["generator"] = "custom-splitmix64-v1"
	settings["parameter_binding"] = "nullable text with typed SQL casts"
	if run.Target == "oracle" {
		settings["prepared_queries"] = "go-ora prepares each request"
	}
	emit(run, "metadata", metadata)
	stages := []stage{{Clients: run.Clients, Seconds: run.Config.Duration}}
	if run.Config.Plan != nil {
		stages = run.Config.Plan.Stages
	} else if run.Config.LoadModel == "constantRate" {
		stages[0].Rate = &run.Config.Rate
	}
	initialized := false
	for _, unit := range s.program.Units {
		if err = check(); err != nil {
			return err
		}
		id := unit.Operation
		if id == "" {
			id = "mix"
		}
		prefix := "custom/setup/" + id + "/"
		if !initialized || run.Config.Custom.ResetBetweenOperations {
			if err = s.setup(prefix+"schema", false, func() (int64, error) { return 0, s.reset(db) }); err != nil {
				return err
			}
			if err = s.setup(prefix+"load", true, func() (int64, error) { return s.load(db) }); err != nil {
				return err
			}
			if err = s.setup(prefix+"indexes", false, func() (int64, error) {
				for _, stmt := range s.program.AfterLoad {
					if _, err := db.Exec(stmt); err != nil {
						return 0, err
					}
				}
				return 0, nil
			}); err != nil {
				return err
			}
			initialized = true
		}
		before := false
		for _, c := range s.program.Checks {
			before = before || c.Definition.Before
		}
		if before {
			if err = s.setup(prefix+"before", false, func() (int64, error) { return 0, s.checkData(db, true) }); err != nil {
				return err
			}
		}
		read := false
		for _, op := range s.program.Operations {
			read = read || op.Definition.ReadOnly && (unit.Operation == "" || op.Definition.ID == unit.Operation)
		}
		if read && run.Config.Warmup > 0 {
			warm, err := s.sessions(nil, stages[0].Clients, unit.Operation, true)
			if err == nil {
				var result map[string]any
				result, err = measure(run, warm, make([]int64, run.Clients), &atomic.Int64{}, unit.Phase, run.Config.Warmup, stages[0].Clients, nil, nil, false)
				if err == nil && result["errors"].(int64) > 0 {
					err = fmt.Errorf("errors during custom warmup: %v", result["errorExamples"])
				}
			}
			closeCustomSessions(warm)
			if err != nil {
				return err
			}
		}
		if err = s.windows(db, unit.Phase, unit.Operation, stages); err != nil {
			return err
		}
	}
	return nil
}
func closeCustomSessions(sessions []*database) {
	for _, session := range sessions {
		session.close()
	}
}
func (s *customState) windows(db *sql.DB, phase, operation string, stages []stage) error {
	var sessions []*database
	defer func() { closeCustomSessions(sessions) }()
	sequences := make([]int64, s.run.Clients)
	for i, stage := range stages {
		var err error
		sessions, err = s.sessions(sessions, stage.Clients, operation, false)
		if err != nil {
			return err
		}
		var stamp map[string]any
		if s.run.Config.Plan != nil {
			stamp = map[string]any{"number": i + 1, "name": stage.Name, "targetSeconds": stage.Seconds, "operationsPerSecond": stage.Rate}
		}
		for _, session := range sessions {
			clear(session.custom.details)
		}
		emit(s.run, "phase_start", map[string]any{"phase": phase, "targetSeconds": stage.Seconds, "clients": stage.Clients, "stage": stamp})
		result, err := measure(s.run, sessions, sequences, &atomic.Int64{}, phase, stage.Seconds, stage.Clients, stage.Rate, stamp, true)
		emit(s.run, "phase_end", map[string]any{})
		if err != nil {
			return err
		}
		succeeded := result["successfulOperations"].(int64)
		s.completed += succeeded
		details := map[string]*customDetail{}
		for _, session := range sessions[:stage.Clients] {
			for id, d := range session.custom.details {
				t := details[id]
				if t == nil {
					t = newCustomDetail(d.id, d.name)
					details[id] = t
				}
				t.success += d.success
				t.rows += d.rows
				t.errors += d.errors
				t.service.merge(d.service)
				t.end.merge(d.end)
			}
		}
		breakdown := make([]map[string]any, 0, len(details))
		for _, op := range s.program.Operations {
			d := details[op.Definition.ID]
			if d == nil {
				continue
			}
			s.byOperation[strings.ToLower(d.id)] += d.success
			breakdown = append(breakdown, map[string]any{"id": d.id, "name": d.name, "successfulOperations": d.success, "successfulRows": d.rows, "errors": d.errors, "serviceLatency": d.service.snapshot(), "endToEndLatency": d.end.snapshot()})
		}
		result["operationBreakdown"] = breakdown
		if !stop.Load() {
			err = s.checkData(db, false)
		}
		result["verified"] = err == nil && succeeded > 0 && !stop.Load()
		emit(s.run, "result", result)
		if err != nil {
			return err
		}
		if err = check(); err != nil {
			return err
		}
	}
	return nil
}
