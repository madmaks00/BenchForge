package main

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	mysql "github.com/go-sql-driver/mysql"
	"github.com/jackc/pgx/v5"
	_ "github.com/jackc/pgx/v5/stdlib"
	"github.com/redis/go-redis/v9"
	"go.mongodb.org/mongo-driver/bson"
	"go.mongodb.org/mongo-driver/mongo"
	"go.mongodb.org/mongo-driver/mongo/options"
	"go.mongodb.org/mongo-driver/mongo/writeconcern"
	_ "modernc.org/sqlite"
)

var background = context.Background()

type database struct {
	custom     *customSession
	extra      *extendedDatabase
	run        *runCase
	db         *sql.DB
	pg         *pgx.Conn
	mongo      *mongo.Client
	collection *mongo.Collection
	redis      *redis.Client
	http       *http.Client
	prefix     string
	statements map[string]*sql.Stmt
}

func openDatabase(run *runCase) (*database, error) {
	if extendedTarget(run.Target) {
		extra, err := openExtended(run)
		if err != nil {
			return nil, err
		}
		return &database{run: run, extra: extra}, nil
	}
	d := &database{run: run, prefix: "dblab:" + run.RunID + ":", statements: map[string]*sql.Stmt{}}
	e := run.Endpoint
	var err error
	switch run.Target {
	case "sqlite":
		d.db, err = sql.Open("sqlite", e.FilePath)
		if err == nil {
			d.db.SetMaxOpenConns(1)
			_, err = d.db.Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=10000;")
		}
	case "postgreSql":
		u := &url.URL{Scheme: "postgres", User: url.UserPassword(e.Username, e.Password), Host: fmt.Sprintf("%s:%d", e.Host, e.Port), Path: e.Database}
		q := u.Query()
		q.Set("sslmode", "disable")
		q.Set("connect_timeout", "5")
		q.Set("synchronous_commit", "on")
		q.Set("statement_timeout", "10000")
		u.RawQuery = q.Encode()
		d.db, err = sql.Open("pgx", u.String())
		if err == nil {
			d.db.SetMaxOpenConns(1)
			err = d.db.Ping()
		}
	case "mySql", "mariaDb":
		cfg := mysql.NewConfig()
		cfg.User = e.Username
		cfg.Passwd = e.Password
		cfg.Net = "tcp"
		cfg.Addr = fmt.Sprintf("%s:%d", e.Host, e.Port)
		cfg.DBName = e.Database
		cfg.Timeout = 5 * time.Second
		cfg.ReadTimeout = 15 * time.Second
		cfg.WriteTimeout = 15 * time.Second
		cfg.AllowNativePasswords = true
		d.db, err = sql.Open("mysql", cfg.FormatDSN())
		if err == nil {
			d.db.SetMaxOpenConns(1)
			err = d.db.Ping()
		}
	case "mongoDb":
		wc := writeconcern.New(writeconcern.W(1), writeconcern.J(true), writeconcern.WTimeout(10*time.Second))
		opts := options.Client().SetHosts([]string{fmt.Sprintf("%s:%d", e.Host, e.Port)}).SetAuth(options.Credential{AuthSource: "admin", Username: e.Username, Password: e.Password}).SetRetryWrites(false).SetConnectTimeout(5 * time.Second).SetServerSelectionTimeout(5 * time.Second).SetSocketTimeout(600 * time.Second).SetWriteConcern(wc).SetMaxPoolSize(uint64(max(4, run.Clients+2)))
		d.mongo, err = mongo.Connect(background, opts)
		if err == nil {
			err = d.mongo.Ping(background, nil)
			d.collection = d.mongo.Database(e.Database).Collection("records")
		}
	case "redis", "valkey":
		d.redis = redis.NewClient(&redis.Options{Addr: fmt.Sprintf("%s:%d", e.Host, e.Port), Password: e.Password, DialTimeout: 5 * time.Second, ReadTimeout: 10 * time.Second, WriteTimeout: 10 * time.Second, MaxRetries: -1, PoolSize: 1})
		err = d.redis.Ping(background).Err()
	case "clickHouse":
		d.http = &http.Client{Timeout: 600 * time.Second, Transport: &http.Transport{MaxIdleConnsPerHost: 2, DisableCompression: true}}
		_, err = d.query("SELECT 1", "")
	default:
		err = fmt.Errorf("unsupported target %s", run.Target)
	}
	if err != nil {
		d.close()
		return nil, err
	}
	return d, nil
}
func (d *database) query(query, body string) (string, error) {
	e := d.run.Endpoint
	u := fmt.Sprintf("http://%s:%d/?", e.Host, e.Port) + url.Values{"database": {e.Database}, "query": {query}}.Encode()
	request, err := http.NewRequest("POST", u, strings.NewReader(body))
	if err != nil {
		return "", err
	}
	request.SetBasicAuth(e.Username, e.Password)
	request.Header.Set("Content-Type", "text/plain")
	response, err := d.http.Do(request)
	if err != nil {
		return "", err
	}
	defer response.Body.Close()
	data, err := io.ReadAll(response.Body)
	if err != nil {
		return "", err
	}
	if response.StatusCode != 200 {
		return "", fmt.Errorf("ClickHouse HTTP %d: %.350s", response.StatusCode, data)
	}
	return string(data), nil
}
func (d *database) reset() error {
	if d.extra != nil {
		return d.extra.reset()
	}
	switch d.run.Target {
	case "mongoDb":
		err := d.collection.Drop(background)
		if err != nil {
			return err
		}
		return d.mongo.Database(d.run.Endpoint.Database).CreateCollection(background, "records")
	case "redis", "valkey":
		return nil
	case "clickHouse":
		if _, err := d.query("DROP TABLE IF EXISTS records", ""); err != nil {
			return err
		}
		_, err := d.query("CREATE TABLE records (id Int64,group_id Int32,amount_cents Int64,version Int64,payload String) ENGINE=MergeTree ORDER BY id", "")
		return err
	default:
		if _, err := d.db.Exec("DROP TABLE IF EXISTS records"); err != nil {
			return err
		}
		text := "TEXT"
		if d.run.Target == "mySql" || d.run.Target == "mariaDb" {
			text = "LONGTEXT"
		}
		_, err := d.db.Exec("CREATE TABLE records (id BIGINT PRIMARY KEY,group_id INTEGER NOT NULL,amount_cents BIGINT NOT NULL,version BIGINT NOT NULL,payload " + text + " NOT NULL)")
		return err
	}
}
func doc(r dataRow) bson.M {
	return bson.M{"_id": r.ID, "group_id": r.Group, "amount_cents": r.Amount, "version": r.Version, "payload": r.Payload}
}
func (d *database) batch(rows []dataRow) error {
	if d.extra != nil {
		return d.extra.batch(rows)
	}
	switch d.run.Target {
	case "mongoDb":
		documents := make([]any, len(rows))
		for i, r := range rows {
			documents[i] = doc(r)
		}
		_, err := d.collection.InsertMany(background, documents)
		return err
	case "redis", "valkey":
		pipe := d.redis.Pipeline()
		for _, r := range rows {
			pipe.HSet(background, d.prefix+strconv.FormatInt(r.ID, 10), map[string]any{"group_id": r.Group, "amount_cents": r.Amount, "version": r.Version, "payload": r.Payload})
		}
		_, err := pipe.Exec(background)
		return err
	case "clickHouse":
		var body strings.Builder
		for _, r := range rows {
			data, _ := json.Marshal(map[string]any{"id": r.ID, "group_id": r.Group, "amount_cents": r.Amount, "version": r.Version, "payload": r.Payload})
			body.Write(data)
			body.WriteByte('\n')
		}
		_, err := d.query("INSERT INTO records FORMAT JSONEachRow", body.String())
		return err
	case "postgreSql":
		conn, err := d.db.Conn(background)
		if err != nil {
			return err
		}
		defer conn.Close()
		return conn.Raw(func(driverConn any) error {
			pg := driverConn.(interface{ Conn() *pgx.Conn }).Conn()
			values := make([][]any, len(rows))
			for i, r := range rows {
				values[i] = []any{r.ID, r.Group, r.Amount, r.Version, r.Payload}
			}
			_, err := pg.CopyFrom(background, pgx.Identifier{"records"}, []string{"id", "group_id", "amount_cents", "version", "payload"}, pgx.CopyFromRows(values))
			return err
		})
	case "sqlite":
		tx, err := d.db.Begin()
		if err != nil {
			return err
		}
		defer tx.Rollback()
		stmt, err := tx.Prepare("INSERT INTO records VALUES(?,?,?,?,?)")
		if err != nil {
			return err
		}
		defer stmt.Close()
		for _, r := range rows {
			if _, err = stmt.Exec(r.ID, r.Group, r.Amount, r.Version, r.Payload); err != nil {
				return err
			}
		}
		return tx.Commit()
	default:
		placeholders := make([]string, len(rows))
		args := make([]any, 0, len(rows)*5)
		for i, r := range rows {
			placeholders[i] = "(?,?,?,?,?)"
			args = append(args, r.ID, r.Group, r.Amount, r.Version, r.Payload)
		}
		_, err := d.db.Exec("INSERT INTO records VALUES "+strings.Join(placeholders, ","), args...)
		return err
	}
}
func (d *database) prepare() error {
	if d.extra != nil {
		return d.extra.prepare()
	}
	if d.collection != nil {
		_, err := d.collection.Indexes().CreateOne(background, mongo.IndexModel{Keys: bson.D{{Key: "group_id", Value: 1}}})
		return err
	}
	if d.db == nil {
		return nil
	}
	if _, err := d.db.Exec("CREATE INDEX records_group_id ON records(group_id)"); err != nil {
		return err
	}
	if d.run.Target == "mySql" || d.run.Target == "mariaDb" {
		rows, err := d.db.Query("ANALYZE TABLE records")
		if err != nil {
			return err
		}
		defer rows.Close()
		for rows.Next() {
		}
		return rows.Err()
	}
	_, err := d.db.Exec("ANALYZE records")
	return err
}
func (d *database) digest() ([3]int64, error) {
	if d.extra != nil {
		return d.extra.digest()
	}
	var sum [3]int64
	if d.collection != nil {
		pipeline := mongo.Pipeline{bson.D{{Key: "$group", Value: bson.M{"_id": nil, "n": bson.M{"$sum": 1}, "ids": bson.M{"$sum": "$_id"}, "amount": bson.M{"$sum": "$amount_cents"}}}}}
		cursor, err := d.collection.Aggregate(background, pipeline)
		if err != nil {
			return sum, err
		}
		defer cursor.Close(background)
		if cursor.Next(background) {
			var r struct {
				N      int64 `bson:"n"`
				IDs    int64 `bson:"ids"`
				Amount int64 `bson:"amount"`
			}
			err = cursor.Decode(&r)
			sum = [3]int64{r.N, r.IDs, r.Amount}
		}
		return sum, err
	}
	if d.redis != nil {
		script := "local r=redis.call('SCAN',ARGV[1],'MATCH',ARGV[2]..'*','COUNT',1000); local n=0; local ids=0; local a=0; for _,k in ipairs(r[2]) do n=n+1; ids=ids+tonumber(string.sub(k,string.len(ARGV[2])+1)); a=a+tonumber(redis.call('HGET',k,'amount_cents')); end; return {r[1],n,ids,a}"
		var cursor string = "0"
		for {
			r, err := d.redis.Eval(background, script, nil, cursor, d.prefix).Slice()
			if err != nil {
				return sum, err
			}
			cursor = fmt.Sprint(r[0])
			for i := range sum {
				n, err := strconv.ParseInt(fmt.Sprint(r[i+1]), 10, 64)
				if err != nil {
					return sum, err
				}
				sum[i] += n
			}
			if cursor == "0" {
				return sum, nil
			}
		}
	}
	if d.http != nil {
		text, err := d.query("SELECT count(),sum(id),sum(amount_cents) FROM records FORMAT TabSeparated", "")
		if err != nil {
			return sum, err
		}
		parts := strings.Split(strings.TrimSpace(text), "\t")
		if len(parts) != 3 {
			return sum, fmt.Errorf("bad digest")
		}
		for i := range sum {
			sum[i], err = strconv.ParseInt(parts[i], 10, 64)
			if err != nil {
				return sum, err
			}
		}
		return sum, nil
	}
	err := d.db.QueryRow("SELECT COUNT(*),COALESCE(SUM(id),0),COALESCE(SUM(amount_cents),0) FROM records").Scan(&sum[0], &sum[1], &sum[2])
	return sum, err
}
func (d *database) count() (int64, error) {
	if d.extra != nil {
		return d.extra.count()
	}
	if d.collection != nil {
		return d.collection.CountDocuments(background, bson.D{})
	}
	if d.redis != nil {
		return d.redis.DBSize(background).Result()
	}
	if d.http != nil {
		text, err := d.query("SELECT count() FROM records FORMAT TabSeparated", "")
		if err != nil {
			return 0, err
		}
		return strconv.ParseInt(strings.TrimSpace(text), 10, 64)
	}
	var count int64
	err := d.db.QueryRow("SELECT COUNT(*) FROM records").Scan(&count)
	return count, err
}
func (d *database) metadata() (map[string]any, error) {
	if d.extra != nil {
		return d.extra.metadata()
	}
	settings := map[string]string{"build": "Release", "generator": generator, "connections": "dedicated connection per logical client; goroutines", "batch_transport": "native driver batching"}
	var version, driver string
	var err error
	switch d.run.Target {
	case "sqlite", "postgreSql", "mySql", "mariaDb":
		query := "SELECT VERSION()"
		if d.run.Target == "sqlite" {
			query = "SELECT sqlite_version()"
		}
		err = d.db.QueryRow(query).Scan(&version)
		if err != nil {
			return nil, err
		}
		driver = map[string]string{"sqlite": "modernc.org/sqlite v1.39.1 (pure Go)", "postgreSql": "pgx/v5 v5.7.6", "mySql": "go-sql-driver/mysql v1.9.3", "mariaDb": "go-sql-driver/mysql v1.9.3"}[d.run.Target]
		keys := []string{"innodb_flush_log_at_trx_commit", "innodb_buffer_pool_size", "max_connections"}
		if d.run.Target == "sqlite" {
			keys = []string{"journal_mode", "synchronous", "cache_size"}
			settings["batch_transport"] = "prepared INSERT in transaction"
		} else if d.run.Target == "postgreSql" {
			keys = []string{"synchronous_commit", "fsync", "shared_buffers", "max_connections"}
			settings["batch_transport"] = "binary COPY"
		} else {
			settings["batch_transport"] = "multi-row parameterized INSERT"
		}
		for _, key := range keys {
			q := "SELECT @@GLOBAL." + key
			if d.run.Target == "sqlite" {
				q = "PRAGMA " + key
			} else if d.run.Target == "postgreSql" {
				q = "SHOW " + key
			}
			var value any
			if err = d.db.QueryRow(q).Scan(&value); err != nil {
				return nil, err
			}
			switch v := value.(type) {
			case []byte:
				settings[key] = string(v)
			default:
				settings[key] = fmt.Sprint(v)
			}
		}
	case "mongoDb":
		var build struct {
			Version string `bson:"version"`
		}
		err = d.mongo.Database(d.run.Endpoint.Database).RunCommand(background, bson.D{{Key: "buildInfo", Value: 1}}).Decode(&build)
		version = build.Version
		driver = "mongo-driver v1.17.4"
		settings["write_concern"] = "w=1,j=true"
		settings["retry_writes"] = "false"
		settings["topology"] = "standalone"
	case "redis", "valkey":
		info, e := d.redis.Info(background, "server").Result()
		err = e
		version = "unknown"
		for _, line := range strings.Split(info, "\n") {
			for _, name := range []string{"redis_version:", "valkey_version:"} {
				if strings.HasPrefix(line, name) {
					version = strings.TrimSpace(strings.TrimPrefix(line, name))
				}
			}
		}
		driver = "go-redis/v9 v9.12.1"
		settings["durability_profile"] = "memory cache: AOF and RDB disabled"
		settings["batch_transport"] = "pipelined HSET; non-atomic"
	default:
		version, err = d.query("SELECT version() FORMAT TabSeparated", "")
		version = strings.TrimSpace(version)
		driver = "stdlib net/http"
		settings["transport"] = "HTTP / JSONEachRow / uncompressed"
		settings["insert"] = "synchronous HTTP; async_insert=0"
		settings["table"] = "MergeTree ORDER BY id; no unique constraint"
	}
	return map[string]any{"engine": "go", "runtime": runtimeVersion(), "driver": driver, "serverVersion": version, "settings": settings}, err
}
func (d *database) execute(operation string, key int64, row *dataRow, batch []dataRow) (int64, error) {
	if d.extra != nil {
		return d.extra.execute(operation, key, row, batch)
	}
	if operation == "BatchInsert" {
		err := d.batch(batch)
		return int64(len(batch)), err
	}
	if d.collection != nil {
		ctx, cancel := context.WithTimeout(background, 15*time.Second)
		defer cancel()
		filter := bson.D{{Key: "_id", Value: key}}
		switch operation {
		case "PointRead":
			var r struct {
				ID      int64  `bson:"_id"`
				Payload string `bson:"payload"`
			}
			err := d.collection.FindOne(ctx, filter).Decode(&r)
			if err == nil && r.ID != key {
				err = fmt.Errorf("invalid ID")
			}
			return 1, err
		case "RangeRead":
			cursor, err := d.collection.Find(ctx, bson.D{{Key: "_id", Value: bson.D{{Key: "$gte", Value: key}}}}, options.Find().SetSort(bson.D{{Key: "_id", Value: 1}}).SetLimit(100).SetMaxTime(10*time.Second))
			if err != nil {
				return 0, err
			}
			defer cursor.Close(ctx)
			var count int64
			for cursor.Next(ctx) {
				var r bson.M
				if err = cursor.Decode(&r); err != nil {
					return 0, err
				}
				count++
			}
			if count == 0 {
				return 0, fmt.Errorf("empty range")
			}
			return count, cursor.Err()
		case "Aggregate":
			pipeline := mongo.Pipeline{
				bson.D{{Key: "$match", Value: bson.M{"group_id": (key - 1) % 100}}},
				bson.D{{Key: "$group", Value: bson.M{"_id": "$group_id", "n": bson.M{"$sum": 1}, "amount": bson.M{"$sum": "$amount_cents"}}}},
			}
			cursor, err := d.collection.Aggregate(ctx, pipeline)
			if err != nil {
				return 0, err
			}
			defer cursor.Close(ctx)
			var count int64
			for cursor.Next(ctx) {
				var r bson.M
				if err = cursor.Decode(&r); err != nil {
					return 0, err
				}
				count++
			}
			if count == 0 {
				return 0, fmt.Errorf("empty aggregate")
			}
			return count, cursor.Err()
		case "Insert":
			_, err := d.collection.InsertOne(ctx, doc(*row))
			return 1, err
		case "Update":
			r, err := d.collection.UpdateOne(ctx, filter, bson.D{{Key: "$inc", Value: bson.D{{Key: "version", Value: int64(1)}}}})
			if err != nil {
				return 0, err
			}
			if r.MatchedCount != 1 {
				return 0, fmt.Errorf("missing update")
			}
			return 1, nil
		case "Delete":
			r, err := d.collection.DeleteOne(ctx, filter)
			if err != nil {
				return 0, err
			}
			if r.DeletedCount != 1 {
				return 0, fmt.Errorf("missing delete")
			}
			return 1, nil
		}
	}
	if d.redis != nil {
		name := d.prefix + strconv.FormatInt(key, 10)
		switch operation {
		case "PointRead":
			r, err := d.redis.HGetAll(background, name).Result()
			if err == nil && len(r) != 4 {
				err = fmt.Errorf("incomplete Redis hash")
			}
			return 1, err
		case "Update":
			err := d.redis.HIncrBy(background, name, "version", 1).Err()
			return 1, err
		case "Insert":
			return 1, d.batch([]dataRow{*row})
		case "Delete":
			n, err := d.redis.Del(background, name).Result()
			if err == nil && n != 1 {
				err = fmt.Errorf("missing delete")
			}
			return n, err
		}
	}
	if d.http != nil {
		var query string
		switch operation {
		case "RangeRead":
			query = fmt.Sprintf("SELECT id,group_id,amount_cents,version,payload FROM records WHERE id>=%d ORDER BY id LIMIT 100", key)
		case "Aggregate":
			query = fmt.Sprintf("SELECT group_id,count(),sum(amount_cents) FROM records WHERE group_id=%d GROUP BY group_id", (key-1)%100)
		default:
			query = fmt.Sprintf("SELECT count(),sum(amount_cents),avg(amount_cents) FROM records WHERE id>=%d AND id<%d", key, key+max(int64(100), d.run.Rows/100))
		}
		text, err := d.query(query+" SETTINGS max_execution_time=10 FORMAT JSONEachRow", "")
		if err != nil {
			return 0, err
		}
		var count int64
		for _, line := range strings.Split(text, "\n") {
			if line == "" {
				continue
			}
			var value map[string]any
			if err = json.Unmarshal([]byte(line), &value); err != nil {
				return 0, err
			}
			count++
		}
		if count == 0 {
			return 0, fmt.Errorf("empty ClickHouse response")
		}
		return count, nil
	}
	placeholder := "?"
	if d.run.Target == "postgreSql" {
		placeholder = "$1"
	}
	queries := map[string]string{"PointRead": "SELECT id,group_id,amount_cents,version,payload FROM records WHERE id=" + placeholder, "RangeRead": "SELECT id,group_id,amount_cents,version,payload FROM records WHERE id>=" + placeholder + " ORDER BY id LIMIT 100", "Aggregate": "SELECT group_id,COUNT(*),SUM(amount_cents) FROM records WHERE group_id=" + placeholder + " GROUP BY group_id", "Update": "UPDATE records SET version=version+1 WHERE id=" + placeholder, "Delete": "DELETE FROM records WHERE id=" + placeholder, "Insert": "INSERT INTO records VALUES(?,?,?,?,?)"}
	if d.run.Target == "postgreSql" {
		queries["Insert"] = "INSERT INTO records VALUES($1,$2,$3,$4,$5)"
	}
	stmt := d.statements[operation]
	if stmt == nil {
		var err error
		stmt, err = d.db.Prepare(queries[operation])
		if err != nil {
			return 0, err
		}
		d.statements[operation] = stmt
	}
	args := []any{key}
	if operation == "Aggregate" {
		args[0] = int32((key - 1) % 100)
	}
	if operation == "Insert" {
		args = []any{row.ID, row.Group, row.Amount, row.Version, row.Payload}
	}
	if operation == "PointRead" || operation == "RangeRead" || operation == "Aggregate" {
		rows, err := stmt.Query(args...)
		if err != nil {
			return 0, err
		}
		defer rows.Close()
		var count int64
		for rows.Next() {
			if operation == "Aggregate" {
				var group int32
				var n, amount int64
				err = rows.Scan(&group, &n, &amount)
			} else {
				var id, amount, version int64
				var group int32
				var payload string
				err = rows.Scan(&id, &group, &amount, &version, &payload)
				if err == nil && operation == "PointRead" && id != key {
					err = fmt.Errorf("invalid ID")
				}
			}
			if err != nil {
				return 0, err
			}
			count++
		}
		if count == 0 || operation == "PointRead" && count != 1 {
			return 0, fmt.Errorf("invalid SQL response")
		}
		return count, rows.Err()
	}
	result, err := stmt.Exec(args...)
	if err != nil {
		return 0, err
	}
	n, err := result.RowsAffected()
	if err == nil && n != 1 {
		err = fmt.Errorf("write did not affect one record")
	}
	return n, err
}
func (d *database) close() {
	if d.custom != nil {
		for _, stmt := range d.custom.prepared {
			stmt.Close()
		}
	}
	if d.extra != nil {
		d.extra.close()
		return
	}
	for _, s := range d.statements {
		s.Close()
	}
	if d.db != nil {
		d.db.Close()
	}
	if d.pg != nil {
		d.pg.Close(background)
	}
	if d.mongo != nil {
		d.mongo.Disconnect(background)
	}
	if d.redis != nil {
		d.redis.Close()
	}
	if d.http != nil {
		d.http.CloseIdleConnections()
	}
}
