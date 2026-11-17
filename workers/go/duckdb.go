package main

// The official DuckDB C API is loaded directly. No CGO compiler or language bridge.
import (
	"context"
	"database/sql"
	"database/sql/driver"
	"errors"
	"fmt"
	"github.com/ebitengine/purego"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"sync"
	"unsafe"
)

type duckResult struct {
	columns, rows, changed        uint64
	columnData, message, internal uintptr
}
type duckAPI struct {
	open           func(string, *uintptr) int32
	close          func(*uintptr)
	connect        func(uintptr, *uintptr) int32
	disconnect     func(*uintptr)
	prepare        func(uintptr, string, *uintptr) int32
	prepareError   func(uintptr) uintptr
	destroyPrepare func(*uintptr)
	bindNull       func(uintptr, uint64) int32
	bindInt        func(uintptr, uint64, int64) int32
	bindText       func(uintptr, uint64, string) int32
	execute        func(uintptr, *duckResult) int32
	destroy        func(*duckResult)
	resultError    func(*duckResult) uintptr
	rowCount       func(*duckResult) uint64
	columnCount    func(*duckResult) uint64
	columnName     func(*duckResult, uint64) uintptr
	rowsChanged    func(*duckResult) uint64
	valueText      func(*duckResult, uint64, uint64) uintptr
	valueNull      func(*duckResult, uint64, uint64) bool
	free           func(uintptr)
}

var duck struct {
	sync.Mutex
	api     *duckAPI
	handles map[string]*duckHandle
}

type duckHandle struct {
	value uintptr
	refs  int
}
type duckDriver struct{}
type duckConn struct {
	handle *duckHandle
	path   string
	value  uintptr
}

func init() { sql.Register("benchforge-duckdb", duckDriver{}) }
func cText(address uintptr) string {
	if address == 0 {
		return ""
	}
	n := 0
	for *(*byte)(unsafe.Pointer(address + uintptr(n))) != 0 {
		n++
	}
	return string(unsafe.Slice((*byte)(unsafe.Pointer(address)), n))
}
func loadDuck() error {
	if duck.api != nil {
		return nil
	}
	exe, err := os.Executable()
	if err != nil {
		return err
	}
	name := map[string]string{"windows": "duckdb.dll", "linux": "libduckdb.so", "darwin": "libduckdb.dylib"}[runtime.GOOS]
	symbol, err := openDuckLibrary(filepath.Join(filepath.Dir(exe), "native", name))
	if err != nil {
		return err
	}
	a := new(duckAPI)
	bindings := []struct {
		name string
		fn   any
	}{
		{"duckdb_open", &a.open}, {"duckdb_close", &a.close}, {"duckdb_connect", &a.connect}, {"duckdb_disconnect", &a.disconnect},
		{"duckdb_prepare", &a.prepare}, {"duckdb_prepare_error", &a.prepareError}, {"duckdb_destroy_prepare", &a.destroyPrepare},
		{"duckdb_bind_null", &a.bindNull}, {"duckdb_bind_int64", &a.bindInt}, {"duckdb_bind_varchar", &a.bindText}, {"duckdb_execute_prepared", &a.execute},
		{"duckdb_destroy_result", &a.destroy}, {"duckdb_result_error", &a.resultError}, {"duckdb_row_count", &a.rowCount},
		{"duckdb_column_count", &a.columnCount}, {"duckdb_column_name", &a.columnName}, {"duckdb_rows_changed", &a.rowsChanged},
		{"duckdb_value_varchar", &a.valueText}, {"duckdb_value_is_null", &a.valueNull}, {"duckdb_free", &a.free},
	}
	for _, b := range bindings {
		address, err := symbol(b.name)
		if err != nil {
			return err
		}
		purego.RegisterFunc(b.fn, address)
	}
	duck.api = a
	duck.handles = map[string]*duckHandle{}
	return nil
}
func (duckDriver) Open(path string) (driver.Conn, error) {
	duck.Lock()
	defer duck.Unlock()
	if err := loadDuck(); err != nil {
		return nil, err
	}
	h := duck.handles[path]
	if h == nil {
		h = &duckHandle{}
		if duck.api.open(path, &h.value) != 0 {
			if h.value != 0 {
				duck.api.close(&h.value)
			}
			return nil, errors.New("DuckDB could not open owned file")
		}
		duck.handles[path] = h
	}
	c := &duckConn{handle: h, path: path}
	if duck.api.connect(h.value, &c.value) != 0 {
		return nil, errors.New("DuckDB connect failed")
	}
	h.refs++
	return c, nil
}
func (c *duckConn) Close() error {
	duck.Lock()
	defer duck.Unlock()
	if c.value != 0 {
		duck.api.disconnect(&c.value)
		c.handle.refs--
		if c.handle.refs == 0 {
			duck.api.close(&c.handle.value)
			delete(duck.handles, c.path)
		}
	}
	return nil
}
func (c *duckConn) Prepare(text string) (driver.Stmt, error) { return &duckStmt{c: c, text: text}, nil }
func (c *duckConn) Begin() (driver.Tx, error) {
	_, err := c.ExecContext(context.Background(), "BEGIN", nil)
	return &duckTx{c}, err
}

type duckTx struct{ c *duckConn }

func (t *duckTx) Commit() error {
	_, e := t.c.ExecContext(context.Background(), "COMMIT", nil)
	return e
}
func (t *duckTx) Rollback() error {
	_, e := t.c.ExecContext(context.Background(), "ROLLBACK", nil)
	return e
}
func (c *duckConn) run(text string, args []driver.NamedValue) ([][]driver.Value, []string, int64, error) {
	a := duck.api
	var stmt uintptr
	status := a.prepare(c.value, text, &stmt)
	if stmt != 0 {
		defer a.destroyPrepare(&stmt)
	}
	if status != 0 {
		return nil, nil, 0, fmt.Errorf("DuckDB prepare: %s", cText(a.prepareError(stmt)))
	}
	for i, arg := range args {
		var status int32
		switch v := arg.Value.(type) {
		case nil:
			status = a.bindNull(stmt, uint64(i+1))
		case int64:
			status = a.bindInt(stmt, uint64(i+1), v)
		case string:
			status = a.bindText(stmt, uint64(i+1), v)
		default:
			return nil, nil, 0, fmt.Errorf("Unsupported DuckDB parameter %T", v)
		}
		if status != 0 {
			return nil, nil, 0, errors.New("DuckDB bind failed")
		}
	}
	var result duckResult
	defer a.destroy(&result)
	if a.execute(stmt, &result) != 0 {
		return nil, nil, 0, fmt.Errorf("DuckDB execute: %s", cText(a.resultError(&result)))
	}
	n, m := a.rowCount(&result), a.columnCount(&result)
	columns := make([]string, m)
	for i := range columns {
		columns[i] = cText(a.columnName(&result, uint64(i)))
	}
	rows := make([][]driver.Value, n)
	for i := range rows {
		rows[i] = make([]driver.Value, m)
		for j := range rows[i] {
			if a.valueNull(&result, uint64(j), uint64(i)) {
				continue
			}
			p := a.valueText(&result, uint64(j), uint64(i))
			rows[i][j] = cText(p)
			a.free(p)
		}
	}
	return rows, columns, int64(a.rowsChanged(&result)), nil
}
func (c *duckConn) ExecContext(ctx context.Context, text string, args []driver.NamedValue) (driver.Result, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	_, _, n, e := c.run(text, args)
	return driver.RowsAffected(n), e
}
func (c *duckConn) QueryContext(ctx context.Context, text string, args []driver.NamedValue) (driver.Rows, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	rows, columns, _, err := c.run(text, args)
	return &duckRows{rows: rows, columns: columns}, err
}

type duckStmt struct {
	c    *duckConn
	text string
}

func (s *duckStmt) Close() error  { return nil }
func (s *duckStmt) NumInput() int { return -1 }
func named(values []driver.Value) []driver.NamedValue {
	r := make([]driver.NamedValue, len(values))
	for i, v := range values {
		r[i] = driver.NamedValue{Ordinal: i + 1, Value: v}
	}
	return r
}
func (s *duckStmt) Exec(args []driver.Value) (driver.Result, error) {
	return s.c.ExecContext(context.Background(), s.text, named(args))
}
func (s *duckStmt) Query(args []driver.Value) (driver.Rows, error) {
	return s.c.QueryContext(context.Background(), s.text, named(args))
}

type duckRows struct {
	rows    [][]driver.Value
	columns []string
	index   int
}

func (r *duckRows) Columns() []string { return r.columns }
func (r *duckRows) Close() error      { r.rows = nil; return nil }
func (r *duckRows) Next(dest []driver.Value) error {
	if r.index >= len(r.rows) {
		return io.EOF
	}
	copy(dest, r.rows[r.index])
	r.index++
	return nil
}
