//! Direct official DuckDB C API, with a shared database handle and one connection/client.
use anyhow::{bail, Context, Result};
use libloading::Library;
use serde_json::Value;
use std::{
    collections::HashMap,
    ffi::{c_char, c_void, CStr, CString},
    ptr,
    sync::{Arc, Mutex, OnceLock, Weak},
};
type Handle = *mut c_void;
#[repr(C)]
#[derive(Default)]
struct DuckResult {
    columns: u64,
    rows: u64,
    changed: u64,
    column_data: Handle,
    message: *mut c_char,
    internal: Handle,
}
struct Api {
    _library: Library,
    open: unsafe extern "C" fn(*const c_char, *mut Handle) -> u32,
    close: unsafe extern "C" fn(*mut Handle),
    connect: unsafe extern "C" fn(Handle, *mut Handle) -> u32,
    disconnect: unsafe extern "C" fn(*mut Handle),
    prepare: unsafe extern "C" fn(Handle, *const c_char, *mut Handle) -> u32,
    prepare_error: unsafe extern "C" fn(Handle) -> *const c_char,
    destroy_prepare: unsafe extern "C" fn(*mut Handle),
    bind_int: unsafe extern "C" fn(Handle, u64, i64) -> u32,
    bind_null: unsafe extern "C" fn(Handle, u64) -> u32,
    bind_text: unsafe extern "C" fn(Handle, u64, *const c_char) -> u32,
    execute: unsafe extern "C" fn(Handle, *mut DuckResult) -> u32,
    destroy: unsafe extern "C" fn(*mut DuckResult),
    error: unsafe extern "C" fn(*mut DuckResult) -> *const c_char,
    rows: unsafe extern "C" fn(*mut DuckResult) -> u64,
    columns: unsafe extern "C" fn(*mut DuckResult) -> u64,
    changed: unsafe extern "C" fn(*mut DuckResult) -> u64,
    text: unsafe extern "C" fn(*mut DuckResult, u64, u64) -> *mut c_char,
    is_null: unsafe extern "C" fn(*mut DuckResult, u64, u64) -> bool,
    free: unsafe extern "C" fn(*mut c_void),
}
static API: OnceLock<Api> = OnceLock::new();
static DATABASES: OnceLock<Mutex<HashMap<String, Weak<Shared>>>> = OnceLock::new();
struct Shared {
    value: Handle,
}
// DuckDB database handles support concurrent independent connections. The registry
// serializes open/close; each DuckConnection is exclusively owned by its client.
unsafe impl Send for Shared {}
unsafe impl Sync for Shared {}
impl Drop for Shared {
    fn drop(&mut self) {
        unsafe { (API.get().unwrap().close)(&mut self.value) }
    }
}
pub struct DuckConnection {
    value: Handle,
    _database: Arc<Shared>,
}
unsafe impl Send for DuckConnection {}
impl Drop for DuckConnection {
    fn drop(&mut self) {
        unsafe { (API.get().unwrap().disconnect)(&mut self.value) }
    }
}
unsafe fn text(p: *const c_char) -> String {
    if p.is_null() {
        String::new()
    } else {
        CStr::from_ptr(p).to_string_lossy().into_owned()
    }
}
fn load() -> Result<()> {
    if API.get().is_some() {
        return Ok(());
    }
    let name = if cfg!(target_os = "windows") {
        "duckdb.dll"
    } else if cfg!(target_os = "macos") {
        "libduckdb.dylib"
    } else {
        "libduckdb.so"
    };
    let path = std::env::current_exe()?
        .parent()
        .context("Missing executable parent")?
        .join("native")
        .join(name);
    unsafe {
        let l = Library::new(path)?;
        macro_rules! symbol {
            ($s:literal) => {
                *l.get(concat!($s, "\0").as_bytes())?
            };
        }
        let a = Api {
            open: symbol!("duckdb_open"),
            close: symbol!("duckdb_close"),
            connect: symbol!("duckdb_connect"),
            disconnect: symbol!("duckdb_disconnect"),
            prepare: symbol!("duckdb_prepare"),
            prepare_error: symbol!("duckdb_prepare_error"),
            destroy_prepare: symbol!("duckdb_destroy_prepare"),
            bind_int: symbol!("duckdb_bind_int64"),
            bind_null: symbol!("duckdb_bind_null"),
            bind_text: symbol!("duckdb_bind_varchar"),
            execute: symbol!("duckdb_execute_prepared"),
            destroy: symbol!("duckdb_destroy_result"),
            error: symbol!("duckdb_result_error"),
            rows: symbol!("duckdb_row_count"),
            columns: symbol!("duckdb_column_count"),
            changed: symbol!("duckdb_rows_changed"),
            text: symbol!("duckdb_value_varchar"),
            is_null: symbol!("duckdb_value_is_null"),
            free: symbol!("duckdb_free"),
            _library: l,
        };
        let _ = API.set(a);
    }
    Ok(())
}
impl DuckConnection {
    pub fn open(path: &str) -> Result<Self> {
        let mut registry = DATABASES
            .get_or_init(|| Mutex::new(HashMap::new()))
            .lock()
            .map_err(|_| anyhow::anyhow!("DuckDB registry poisoned"))?;
        load()?;
        let a = API.get().unwrap();
        let shared = if let Some(h) = registry.get(path).and_then(Weak::upgrade) {
            h
        } else {
            let mut value = ptr::null_mut();
            let file = CString::new(path)?;
            if unsafe { (a.open)(file.as_ptr(), &mut value) } != 0 {
                if !value.is_null() {
                    unsafe { (a.close)(&mut value) }
                }
                bail!("DuckDB could not open owned file")
            }
            let shared = Arc::new(Shared { value });
            registry.insert(path.to_owned(), Arc::downgrade(&shared));
            shared
        };
        let mut value = ptr::null_mut();
        if unsafe { (a.connect)(shared.value, &mut value) } != 0 {
            bail!("DuckDB connect failed")
        }
        Ok(Self {
            value,
            _database: shared,
        })
    }
    pub fn query(&mut self, sql: &str, args: &[Value]) -> Result<(Vec<Vec<Value>>, u64)> {
        let a = API.get().unwrap();
        let text_sql = CString::new(sql)?;
        let mut statement = ptr::null_mut();
        let status = unsafe { (a.prepare)(self.value, text_sql.as_ptr(), &mut statement) };
        struct Statement(Handle);
        impl Drop for Statement {
            fn drop(&mut self) {
                if !self.0.is_null() {
                    unsafe { (API.get().unwrap().destroy_prepare)(&mut self.0) }
                }
            }
        }
        let _statement = Statement(statement);
        if status != 0 {
            bail!("DuckDB prepare: {}", unsafe {
                text((a.prepare_error)(statement))
            })
        }
        for (i, v) in args.iter().enumerate() {
            let status = if v.is_null() {
                unsafe { (a.bind_null)(statement, (i + 1) as u64) }
            } else if let Some(n) = v.as_i64() {
                unsafe { (a.bind_int)(statement, (i + 1) as u64, n) }
            } else {
                let s = CString::new(v.as_str().context("Unsupported DuckDB bind")?)?;
                unsafe { (a.bind_text)(statement, (i + 1) as u64, s.as_ptr()) }
            };
            if status != 0 {
                bail!("DuckDB parameter binding failed")
            }
        }
        struct ResultGuard(DuckResult);
        impl Drop for ResultGuard {
            fn drop(&mut self) {
                unsafe { (API.get().unwrap().destroy)(&mut self.0) }
            }
        }
        let mut result = ResultGuard(DuckResult::default());
        let r = &mut result.0;
        if unsafe { (a.execute)(statement, r) } != 0 {
            bail!("DuckDB execute: {}", unsafe { text((a.error)(r)) })
        }
        let mut rows = Vec::new();
        for i in 0..unsafe { (a.rows)(r) } {
            let mut row = Vec::new();
            for j in 0..unsafe { (a.columns)(r) } {
                if unsafe { (a.is_null)(r, j, i) } {
                    row.push(Value::Null)
                } else {
                    let p = unsafe { (a.text)(r, j, i) };
                    row.push(Value::String(unsafe { text(p) }));
                    unsafe { (a.free)(p.cast()) }
                }
            }
            rows.push(row)
        }
        Ok((rows, unsafe { (a.changed)(r) }))
    }
}
