package main

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"net/http"
	"net/url"
	"regexp"
	"runtime"
	"runtime/debug"
	"slices"
	"strconv"
	"strings"
	"time"

	_ "github.com/microsoft/go-mssqldb"
	go_ora "github.com/sijms/go-ora/v2"
)

func extendedTarget(t string) bool {
	return t == "sqlServer" || t == "oracle" || t == "duckDb" || t == "cockroachDb" || t == "elasticsearch" || t == "openSearch"
}

type extendedDatabase struct {
	run  *runCase
	db   *sql.DB
	http *http.Client
}

func openExtended(run *runCase) (d *extendedDatabase, err error) {
	d = &extendedDatabase{run: run}
	defer func() {
		if err != nil && d != nil {
			d.close()
		}
	}()
	e, t := run.Endpoint, run.Target
	if t != "duckDb" && !ownedDatabaseName.MatchString(e.Database) {
		return nil, fmt.Errorf("invalid owned database name")
	}
	switch t {
	case "duckDb":
		d.db, err = sql.Open("benchforge-duckdb", e.FilePath)
	case "sqlServer":
		u := &url.URL{Scheme: "sqlserver", User: url.UserPassword(e.Username, e.Password), Host: fmt.Sprintf("%s:%d", e.Host, e.Port)}
		q := url.Values{"database": {"master"}, "encrypt": {"disable"}, "connection timeout": {"15"}}
		u.RawQuery = q.Encode()
		var admin *sql.DB
		admin, err = sql.Open("sqlserver", u.String())
		if err != nil {
			return
		}
		defer admin.Close()
		_, err = admin.Exec("IF DB_ID(@p1) IS NULL CREATE DATABASE ["+e.Database+"]", e.Database)
		if err != nil {
			return
		}
		q.Set("database", e.Database)
		u.RawQuery = q.Encode()
		d.db, err = sql.Open("sqlserver", u.String())
	case "oracle":
		options := map[string]string{"CONNECTION TIMEOUT": "15", "TIMEOUT": "600"}
		var admin *sql.DB
		admin, err = sql.Open("oracle", go_ora.BuildUrl(e.Host, e.Port, e.Database, "SYSTEM", e.Password, options))
		if err != nil {
			return
		}
		defer admin.Close()
		var n int
		err = admin.QueryRow("SELECT COUNT(*) FROM all_users WHERE username='DBLAB'").Scan(&n)
		if err != nil {
			return
		}
		if n == 0 {
			_, err = admin.Exec(`CREATE USER DBLAB IDENTIFIED BY "` + strings.ReplaceAll(e.Password, `"`, `""`) + `"`)
			if err != nil {
				return
			}
			_, err = admin.Exec("GRANT CREATE SESSION,CREATE TABLE,UNLIMITED TABLESPACE TO DBLAB")
			if err != nil {
				return
			}
		}
		d.db, err = sql.Open("oracle", go_ora.BuildUrl(e.Host, e.Port, e.Database, e.Username, e.Password, options))
	case "cockroachDb":
		u := &url.URL{Scheme: "postgres", User: url.UserPassword(e.Username, e.Password), Host: fmt.Sprintf("%s:%d", e.Host, e.Port), Path: "defaultdb", RawQuery: "sslmode=disable&connect_timeout=15"}
		var admin *sql.DB
		admin, err = sql.Open("pgx", u.String())
		if err != nil {
			return
		}
		defer admin.Close()
		_, err = admin.Exec(`CREATE DATABASE IF NOT EXISTS "` + e.Database + `"`)
		if err != nil {
			return
		}
		u.Path = e.Database
		d.db, err = sql.Open("pgx", u.String())
	default:
		d.http = &http.Client{Timeout: 600 * time.Second, Transport: &http.Transport{MaxIdleConnsPerHost: 2, DisableCompression: true}}
		_, err = d.request("GET", "/", nil, false)
		if err == nil {
			_, err = d.request("GET", "/_cluster/health?wait_for_status=yellow&timeout=5s", nil, false)
		}
		return
	}
	if err != nil {
		return
	}
	d.db.SetMaxOpenConns(1)
	err = d.db.Ping()
	if err != nil {
		return
	}
	if t == "duckDb" {
		_, err = d.db.Exec(fmt.Sprintf("SET memory_limit='%dMB'", run.Config.MemoryMb))
		if err == nil {
			_, err = d.db.Exec(fmt.Sprintf("SET threads=%d", max(1, int(math.Ceil(run.Config.CpuLimit)))))
		}
	}
	if t == "sqlServer" {
		_, err = d.db.Exec("ALTER DATABASE [dblab] SET RECOVERY SIMPLE; ALTER DATABASE [dblab] SET DELAYED_DURABILITY=DISABLED; SET QUOTED_IDENTIFIER ON; SET NOCOUNT OFF; SET XACT_ABORT ON")
	}
	return
}

var ownedDatabaseName = regexp.MustCompile(`^[A-Za-z][A-Za-z0-9_]*$`)

func (d *extendedDatabase) request(method, path string, body any, missing bool) (map[string]any, error) {
	var data []byte
	var err error
	if text, ok := body.(string); ok {
		data = []byte(text)
	} else if body != nil {
		data, err = json.Marshal(body)
		if err != nil {
			return nil, err
		}
	}
	e := d.run.Endpoint
	req, err := http.NewRequest(method, fmt.Sprintf("http://%s:%d%s", e.Host, e.Port, path), strings.NewReader(string(data)))
	if err != nil {
		return nil, err
	}
	content := "application/json"
	if strings.HasPrefix(path, "/_bulk") {
		content = "application/x-ndjson"
	}
	req.Header.Set("Content-Type", content)
	response, err := d.http.Do(req)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	text, err := io.ReadAll(response.Body)
	if err != nil {
		return nil, err
	}
	if missing && response.StatusCode == 404 {
		return map[string]any{}, nil
	}
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return nil, fmt.Errorf("search HTTP %d: %.350s", response.StatusCode, text)
	}
	var result map[string]any
	err = json.Unmarshal(text, &result)
	if err != nil {
		return nil, err
	}
	if result["timed_out"] == true {
		return nil, fmt.Errorf("search timed out")
	}
	if shards, ok := result["_shards"].(map[string]any); ok && shards["failed"].(float64) > 0 {
		return nil, fmt.Errorf("search returned partial shards")
	}
	return result, nil
}
func searchDoc(r dataRow) map[string]any {
	var b strings.Builder
	fmt.Fprintf(&b, "benchforge group%d", r.Group)
	for i := 0; i < len(r.Payload); i += 16 {
		b.WriteByte(' ')
		b.WriteString(r.Payload[i:min(i+16, len(r.Payload))])
	}
	return map[string]any{"id": r.ID, "group_id": r.Group, "amount_cents": r.Amount, "version": r.Version, "payload": r.Payload, "search_text": b.String()}
}
func (d *extendedDatabase) reset() error {
	if d.http != nil {
		_, err := d.request("DELETE", "/records", nil, true)
		if err != nil {
			return err
		}
		_, err = d.request("PUT", "/records", map[string]any{"settings": map[string]any{"number_of_shards": 1, "number_of_replicas": 0, "refresh_interval": "-1", "translog.durability": "request"}, "mappings": map[string]any{"dynamic": "strict", "properties": map[string]any{"id": map[string]any{"type": "long"}, "group_id": map[string]any{"type": "integer"}, "amount_cents": map[string]any{"type": "long"}, "version": map[string]any{"type": "long"}, "payload": map[string]any{"type": "keyword", "index": false, "doc_values": false}, "search_text": map[string]any{"type": "text", "analyzer": "standard"}}}}, false)
		return err
	}
	t := d.run.Target
	drop := "DROP TABLE IF EXISTS records"
	definition := "id BIGINT PRIMARY KEY,group_id INTEGER NOT NULL,amount_cents BIGINT NOT NULL,version BIGINT NOT NULL,payload TEXT NOT NULL"
	if t == "oracle" {
		drop = "BEGIN EXECUTE IMMEDIATE 'DROP TABLE records PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;"
		definition = "id NUMBER(19) PRIMARY KEY,group_id NUMBER(10) NOT NULL,amount_cents NUMBER(19) NOT NULL,version NUMBER(19) NOT NULL,payload CLOB NOT NULL"
	}
	if t == "sqlServer" {
		definition = strings.ReplaceAll(definition, "payload TEXT NOT NULL", "payload NVARCHAR(MAX) NOT NULL")
	}
	if _, err := d.db.Exec(drop); err != nil {
		return err
	}
	_, err := d.db.Exec("CREATE TABLE records(" + definition + ")")
	return err
}
func (d *extendedDatabase) p(n int) string {
	switch d.run.Target {
	case "sqlServer":
		return fmt.Sprintf("@p%d", n)
	case "oracle":
		return fmt.Sprintf(":%d", n)
	case "cockroachDb":
		return fmt.Sprintf("$%d", n)
	default:
		return "?"
	}
}
func (d *extendedDatabase) insertSQL() string {
	p := []string{}
	for i := 1; i <= 5; i++ {
		p = append(p, d.p(i))
	}
	return "INSERT INTO records(id,group_id,amount_cents,version,payload) VALUES(" + strings.Join(p, ",") + ")"
}
func (d *extendedDatabase) values(r dataRow) []any {
	var payload any = r.Payload
	if d.run.Target == "oracle" {
		payload = go_ora.Clob{String: r.Payload, Valid: true}
	}
	return []any{r.ID, r.Group, r.Amount, r.Version, payload}
}
func (d *extendedDatabase) batch(rows []dataRow) error {
	if d.http != nil {
		var b strings.Builder
		for _, r := range rows {
			a, _ := json.Marshal(map[string]any{"create": map[string]any{"_index": "records", "_id": strconv.FormatInt(r.ID, 10)}})
			v, _ := json.Marshal(searchDoc(r))
			b.Write(a)
			b.WriteByte('\n')
			b.Write(v)
			b.WriteByte('\n')
		}
		result, err := d.request("POST", "/_bulk?refresh=false", b.String(), false)
		if err != nil {
			return err
		}
		items, ok := result["items"].([]any)
		if result["errors"] == true || !ok || len(items) != len(rows) {
			return fmt.Errorf("bulk insert failed")
		}
		for _, item := range items {
			c := item.(map[string]any)["create"].(map[string]any)
			if c["status"] != float64(201) || c["error"] != nil {
				return fmt.Errorf("bulk item failed")
			}
		}
		return nil
	}
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer tx.Rollback()
	stmt, err := tx.Prepare(d.insertSQL())
	if err != nil {
		return err
	}
	defer stmt.Close()
	for _, r := range rows {
		result, err := stmt.Exec(d.values(r)...)
		if err != nil {
			return err
		}
		n, err := result.RowsAffected()
		if err != nil || n != 1 {
			return fmt.Errorf("insert did not affect one record: %v", err)
		}
	}
	return tx.Commit()
}
func (d *extendedDatabase) prepare() error {
	if d.http != nil {
		_, err := d.request("PUT", "/records/_settings", map[string]any{"index": map[string]any{"refresh_interval": "1s"}}, false)
		if err != nil {
			return err
		}
		_, err = d.request("POST", "/records/_refresh", nil, false)
		return err
	}
	if _, err := d.db.Exec("CREATE INDEX records_group_id ON records(group_id)"); err != nil {
		return err
	}
	_, err := d.db.Exec(map[string]string{"sqlServer": "UPDATE STATISTICS records WITH FULLSCAN", "oracle": "BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER,'RECORDS'); END;", "cockroachDb": "CREATE STATISTICS records_stats FROM records", "duckDb": "ANALYZE records"}[d.run.Target])
	return err
}
func (d *extendedDatabase) digest() (v [3]int64, err error) {
	if d.http == nil {
		err = d.db.QueryRow("SELECT COUNT(*),COALESCE(SUM(id),0),COALESCE(SUM(amount_cents),0) FROM records").Scan(&v[0], &v[1], &v[2])
		return
	}
	_, err = d.request("POST", "/records/_refresh", nil, false)
	if err != nil {
		return
	}
	var r map[string]any
	r, err = d.request("POST", "/records/_search", map[string]any{"size": 0, "track_total_hits": true, "aggs": map[string]any{"ids": map[string]any{"sum": map[string]any{"field": "id"}}, "amount": map[string]any{"sum": map[string]any{"field": "amount_cents"}}}}, false)
	if err != nil {
		return
	}
	a := r["aggregations"].(map[string]any)
	v = [3]int64{int64(r["hits"].(map[string]any)["total"].(map[string]any)["value"].(float64)), int64(a["ids"].(map[string]any)["value"].(float64)), int64(a["amount"].(map[string]any)["value"].(float64))}
	return
}
func (d *extendedDatabase) count() (n int64, err error) {
	if d.http == nil {
		err = d.db.QueryRow("SELECT COUNT(*) FROM records").Scan(&n)
		return
	}
	_, err = d.request("POST", "/records/_refresh", nil, false)
	if err != nil {
		return
	}
	r, e := d.request("GET", "/records/_count", nil, false)
	if e != nil {
		return 0, e
	}
	return int64(r["count"].(float64)), nil
}
func (d *extendedDatabase) metadata() (map[string]any, error) {
	var version, driver string
	settings := map[string]string{"generator": generator, "connections": "dedicated native connection/client goroutine", "batch_transport": "parameterized row INSERTs in transaction", "retries": "none"}
	if d.http != nil {
		r, e := d.request("GET", "/", nil, false)
		if e != nil {
			return nil, e
		}
		version = r["version"].(map[string]any)["number"].(string)
		driver = "net/http JSON"
		settings["batch_transport"] = "NDJSON bulk create; non-atomic; each item checked"
		settings["search_corpus"] = "payload-terms16-v1"
		settings["durability_profile"] = "translog=request; refresh=1s; 1 primary; 0 replicas"
	} else {
		query := map[string]string{"oracle": "SELECT banner FROM v$version WHERE ROWNUM=1", "sqlServer": "SELECT CAST(SERVERPROPERTY('ProductVersion') AS VARCHAR(100))"}[d.run.Target]
		if query == "" {
			query = "SELECT version()"
		}
		if err := d.db.QueryRow(query).Scan(&version); err != nil {
			return nil, err
		}
		module := map[string]string{"oracle": "github.com/sijms/go-ora/v2", "sqlServer": "github.com/microsoft/go-mssqldb", "cockroachDb": "github.com/jackc/pgx/v5", "duckDb": "github.com/ebitengine/purego"}[d.run.Target]
		driver = module
		if build, ok := debug.ReadBuildInfo(); ok {
			for _, dependency := range build.Deps {
				if dependency.Path == module {
					driver += " / " + dependency.Version
					break
				}
			}
		}
		if d.run.Target == "duckDb" {
			driver += " / DuckDB C API"
		}
		settings["isolation"] = "driver/server default"
		if d.run.Target == "cockroachDb" {
			settings["isolation"] = "SERIALIZABLE; single-node"
		}
	}
	return map[string]any{"engine": "go", "runtime": runtime.Version(), "driver": driver, "serverVersion": version, "settings": settings}, nil
}
func (d *extendedDatabase) execute(op string, key int64, row *dataRow, batch []dataRow) (int64, error) {
	if op == "BatchInsert" {
		err := d.batch(batch)
		return int64(len(batch)), err
	}
	if d.http != nil {
		return d.searchExecute(op, key, row)
	}
	p := d.p(1)
	selectSQL := "SELECT id,group_id,amount_cents,version,payload FROM records"
	text := map[string]string{"PointRead": selectSQL + " WHERE id=" + p, "RangeRead": selectSQL + " WHERE id>=" + p + " ORDER BY id", "Aggregate": "SELECT group_id,COUNT(*),SUM(amount_cents) FROM records WHERE group_id=" + p + " GROUP BY group_id", "Update": "UPDATE records SET version=version+1 WHERE id=" + p, "Delete": "DELETE FROM records WHERE id=" + p, "Insert": d.insertSQL(), "AnalyticalScan": "SELECT COUNT(*),SUM(amount_cents),AVG(amount_cents) FROM records WHERE id>=? AND id<?"}[op]
	if text == "" {
		return 0, fmt.Errorf("unsupported SQL operation")
	}
	if op == "RangeRead" {
		switch d.run.Target {
		case "sqlServer":
			text = strings.Replace(text, "SELECT ", "SELECT TOP (100) ", 1)
		case "oracle":
			text += " FETCH FIRST 100 ROWS ONLY"
		default:
			text += " LIMIT 100"
		}
	}
	args := []any{key}
	if op == "Aggregate" {
		args[0] = (key - 1) % 100
	}
	if op == "Insert" {
		args = d.values(*row)
	}
	if op == "AnalyticalScan" {
		args = append(args, key+max(100, d.run.Rows/100))
	}
	if op == "PointRead" || op == "RangeRead" {
		rows, err := d.db.Query(text, args...)
		if err != nil {
			return 0, err
		}
		defer rows.Close()
		var n, previous int64
		previous = key - 1
		for rows.Next() {
			var id, group, amount, version int64
			var payload string
			if err := rows.Scan(&id, &group, &amount, &version, &payload); err != nil {
				return 0, err
			}
			if id <= previous || op == "PointRead" && id != key || len(payload) != d.run.Config.Payload {
				return 0, fmt.Errorf("invalid ID or payload")
			}
			previous = id
			n++
		}
		if err := rows.Err(); err != nil {
			return 0, err
		}
		if n == 0 || op == "PointRead" && n != 1 {
			return 0, fmt.Errorf("invalid SQL response")
		}
		return n, nil
	}
	if op == "Aggregate" || op == "AnalyticalScan" {
		rows, err := d.db.Query(text, args...)
		if err != nil {
			return 0, err
		}
		defer rows.Close()
		var n int64
		for rows.Next() {
			var a, b, c any
			if err := rows.Scan(&a, &b, &c); err != nil {
				return 0, err
			}
			n++
		}
		if err := rows.Err(); err != nil {
			return 0, err
		}
		if n == 0 {
			return 0, fmt.Errorf("empty aggregate")
		}
		return n, nil
	}
	result, err := d.db.Exec(text, args...)
	if err != nil {
		return 0, err
	}
	n, err := result.RowsAffected()
	if err != nil || n != 1 {
		return 0, fmt.Errorf("write did not affect one record: %v", err)
	}
	return n, nil
}
func (d *extendedDatabase) searchExecute(op string, key int64, r *dataRow) (int64, error) {
	path := "/records/_doc/" + strconv.FormatInt(key, 10)
	if op == "PointRead" {
		v, e := d.request("GET", path, nil, false)
		if e != nil {
			return 0, e
		}
		source, ok := v["_source"].(map[string]any)
		if v["found"] != true || !ok || int64(source["id"].(float64)) != key || len(source["payload"].(string)) != d.run.Config.Payload {
			return 0, fmt.Errorf("invalid document")
		}
		return 1, nil
	}
	if op == "Update" || op == "Insert" || op == "Delete" {
		var v map[string]any
		var err error
		expected := "deleted"
		switch op {
		case "Update":
			expected = "updated"
			v, err = d.request("POST", "/records/_update/"+strconv.FormatInt(key, 10)+"?refresh=false&retry_on_conflict=0", map[string]any{"script": map[string]any{"source": "ctx._source.version += 1", "lang": "painless"}}, false)
		case "Insert":
			expected = "created"
			v, err = d.request("PUT", "/records/_create/"+strconv.FormatInt(r.ID, 10)+"?refresh=false", searchDoc(*r), false)
		default:
			v, err = d.request("DELETE", path+"?refresh=false", nil, false)
		}
		if err != nil {
			return 0, err
		}
		if v["result"] != expected {
			return 0, fmt.Errorf("search write failed")
		}
		return 1, nil
	}
	if op == "Aggregate" {
		v, e := d.request("POST", "/records/_search", map[string]any{"size": 0, "track_total_hits": true, "query": map[string]any{"term": map[string]any{"group_id": (key - 1) % 100}}, "aggs": map[string]any{"amount": map[string]any{"sum": map[string]any{"field": "amount_cents"}}}}, false)
		if e != nil {
			return 0, e
		}
		if v["hits"].(map[string]any)["total"].(map[string]any)["value"].(float64) <= 0 {
			return 0, fmt.Errorf("empty aggregate")
		}
		_ = v["aggregations"].(map[string]any)["amount"].(map[string]any)["value"].(float64)
		return 1, nil
	}
	term := ""
	body := map[string]any{"size": 100, "query": map[string]any{"range": map[string]any{"id": map[string]any{"gte": key}}}, "sort": []any{map[string]any{"id": "asc"}}}
	if op == "FullTextSearch" {
		term = createRow(key, d.run.Config.Seed, d.run.Config.Payload).Payload[:16]
		body = map[string]any{"size": 10, "query": map[string]any{"match": map[string]any{"search_text": term}}}
	}
	v, e := d.request("POST", "/records/_search", body, false)
	if e != nil {
		return 0, e
	}
	hits := v["hits"].(map[string]any)["hits"].([]any)
	if len(hits) == 0 {
		return 0, fmt.Errorf("empty search response")
	}
	previous := key - 1
	for _, h := range hits {
		s := h.(map[string]any)["_source"].(map[string]any)
		id := int64(s["id"].(float64))
		if len(s["payload"].(string)) != d.run.Config.Payload || term != "" && !slices.Contains(strings.Fields(strings.ToLower(s["search_text"].(string))), strings.ToLower(term)) || term == "" && id <= previous {
			return 0, fmt.Errorf("invalid search response")
		}
		previous = id
	}
	return int64(len(hits)), nil
}
func (d *extendedDatabase) close() {
	if d.db != nil {
		d.db.Close()
	}
	if d.http != nil {
		d.http.CloseIdleConnections()
	}
}
