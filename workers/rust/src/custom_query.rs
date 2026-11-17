use anyhow::{bail, Context, Result};
use mysql::{prelude::Queryable, Params};
use postgres::{
    types::{FromSql, ToSql, Type},
    Client,
};
use std::{error::Error, time::Duration};

pub struct Answer {
    pub scalar: Option<String>,
    pub count: i64,
}
pub fn answer(scalar: Option<String>, count: i64) -> Answer {
    Answer { scalar, count }
}

pub struct Raw<'a>(&'a [u8]);
impl<'a> FromSql<'a> for Raw<'a> {
    fn from_sql(
        _: &Type,
        raw: &'a [u8],
    ) -> std::result::Result<Self, Box<dyn Error + Sync + Send>> {
        Ok(Self(raw))
    }
    fn accepts(_: &Type) -> bool {
        true
    }
}

fn pg_scalar(kind: &Type, raw: &[u8]) -> Result<String> {
    let text = match *kind {
        Type::INT2 => i16::from_be_bytes(raw.try_into()?).to_string(),
        Type::INT4 => i32::from_be_bytes(raw.try_into()?).to_string(),
        Type::INT8 => i64::from_be_bytes(raw.try_into()?).to_string(),
        Type::FLOAT4 => f32::from_be_bytes(raw.try_into()?).to_string(),
        Type::FLOAT8 => f64::from_be_bytes(raw.try_into()?).to_string(),
        Type::BOOL => if raw == [0] { "0" } else { "1" }.into(),
        Type::UUID => format_uuid(raw.try_into()?, false),
        Type::TIMESTAMP | Type::TIMESTAMPTZ => {
            let micros = i64::from_be_bytes(raw.try_into()?) as i128 + 946684800000000i128;
            date(time::OffsetDateTime::from_unix_timestamp_nanos(
                micros * 1000,
            )?)
        }
        Type::NUMERIC => {
            if raw.len() < 8 {
                bail!("Invalid PostgreSQL numeric")
            }
            let weight = i16::from_be_bytes([raw[2], raw[3]]) as i32;
            let sign = u16::from_be_bytes([raw[4], raw[5]]);
            if !matches!(sign, 0 | 0x4000) {
                bail!("Non-finite PostgreSQL numeric")
            }
            let scale = u16::from_be_bytes([raw[6], raw[7]]) as usize;
            let digits: Vec<u16> = raw[8..]
                .chunks_exact(2)
                .map(|d| u16::from_be_bytes([d[0], d[1]]))
                .collect();
            let digit = |position: i32| -> u16 {
                (weight - position)
                    .try_into()
                    .ok()
                    .and_then(|i: usize| digits.get(i))
                    .copied()
                    .unwrap_or(0)
            };
            let mut output = if sign == 0x4000 {
                "-".to_owned()
            } else {
                String::new()
            };
            if weight < 0 {
                output.push('0');
            } else {
                for place in (0..=weight).rev() {
                    if place == weight {
                        output.push_str(&digit(place).to_string())
                    } else {
                        output.push_str(&format!("{:04}", digit(place)));
                    }
                }
            }
            if scale > 0 {
                output.push('.');
                let pos = output.len();
                for offset in 1..=scale.div_ceil(4) {
                    output.push_str(&format!("{:04}", digit(-(offset as i32))));
                }
                output.truncate(pos + scale);
            }
            output
        }
        _ => std::str::from_utf8(raw)
            .context("Unsupported PostgreSQL scalar type")?
            .to_owned(),
    };
    Ok(text)
}
pub fn postgres(
    client: &mut Client,
    prepared: &mut std::collections::HashMap<String, postgres::Statement>,
    text: &str,
    values: &[Option<String>],
    mode: &str,
    cache: bool,
) -> Result<Answer> {
    let statement = if cache {
        match prepared.get(text) {
            Some(statement) => statement.clone(),
            None => {
                let stmt = client.prepare(text)?;
                prepared.insert(text.to_owned(), stmt.clone());
                stmt
            }
        }
    } else {
        client.prepare(text)?
    };
    let parameters: Vec<&(dyn ToSql + Sync)> = values.iter().map(|v| v as _).collect();
    if mode == "nonQuery" {
        return Ok(answer(
            None,
            client.execute(&statement, &parameters)? as i64,
        ));
    }
    let rows = client.query(&statement, &parameters)?;
    if mode == "scalar" {
        let value = rows
            .first()
            .map(|row| -> Result<Option<String>> {
                let raw: Option<Raw<'_>> = row.try_get(0)?;
                raw.map(|v| pg_scalar(row.columns()[0].type_(), v.0))
                    .transpose()
            })
            .transpose()?
            .flatten();
        return Ok(answer(value.clone(), i64::from(value.is_some())));
    }
    for row in &rows {
        for index in 0..row.len() {
            let _: Option<Raw<'_>> = row.try_get(index)?;
        }
    }
    Ok(answer(None, rows.len() as i64))
}
pub fn sqlite(
    conn: &rusqlite::Connection,
    sql: &str,
    values: &[Option<String>],
    mode: &str,
    _cache: bool,
) -> Result<Answer> {
    if mode == "nonQuery"
        && values.is_empty()
        && !sql.trim_start().to_ascii_uppercase().starts_with("DELETE")
        && !sql.trim_start().to_ascii_uppercase().starts_with("UPDATE")
        && !sql.trim_start().to_ascii_uppercase().starts_with("INSERT")
    {
        conn.execute_batch(sql)?;
        return Ok(answer(None, 0));
    }
    let mut statement = if _cache {
        conn.prepare_cached(sql)?
    } else {
        return sqlite_statement(&mut conn.prepare(sql)?, values, mode);
    };
    sqlite_statement(&mut statement, values, mode)
}
fn sqlite_statement(
    stmt: &mut rusqlite::Statement<'_>,
    values: &[Option<String>],
    mode: &str,
) -> Result<Answer> {
    use rusqlite::types::ValueRef;
    let args = rusqlite::params_from_iter(values.iter());
    if mode == "nonQuery" {
        return Ok(answer(None, stmt.execute(args)? as i64));
    }
    let columns = stmt.column_count();
    let mut rows = stmt.query(args)?;
    let mut count = 0;
    while let Some(row) = rows.next()? {
        if mode == "scalar" {
            let scalar = match row.get_ref(0)? {
                ValueRef::Null => None,
                ValueRef::Integer(n) => Some(n.to_string()),
                ValueRef::Real(n) => Some(n.to_string()),
                ValueRef::Text(s) => Some(std::str::from_utf8(s)?.to_owned()),
                ValueRef::Blob(bytes) => Some(hex(bytes)),
            };
            return Ok(answer(scalar.clone(), i64::from(scalar.is_some())));
        }
        for i in 0..columns {
            let _ = row.get_ref(i)?;
        }
        count += 1;
    }
    Ok(answer(None, count))
}
pub fn mysql(
    conn: &mut mysql::Conn,
    sql: &str,
    values: &[Option<String>],
    mode: &str,
    cache: bool,
) -> Result<Answer> {
    let params = Params::Positional(
        values
            .iter()
            .map(|value| {
                value
                    .as_ref()
                    .map(|s| mysql::Value::Bytes(s.as_bytes().to_vec()))
                    .unwrap_or(mysql::Value::NULL)
            })
            .collect(),
    );
    if mode == "nonQuery" {
        if values.is_empty() {
            conn.query_drop(sql)?;
        } else if cache {
            conn.exec_drop(sql, params)?;
        } else {
            let stmt = conn.prep(sql)?;
            let result = conn.exec_drop(&stmt, params);
            conn.close(stmt)?;
            result?;
        }
        return Ok(answer(None, conn.affected_rows() as i64));
    }
    let rows: Vec<mysql::Row> = if cache {
        conn.exec(sql, params)?
    } else {
        let stmt = conn.prep(sql)?;
        let result = conn.exec(&stmt, params);
        conn.close(stmt)?;
        result?
    };
    if mode == "scalar" {
        let scalar = rows
            .first()
            .and_then(|row| row.as_ref(0))
            .map(mysql_value)
            .transpose()?
            .flatten();
        return Ok(answer(scalar.clone(), i64::from(scalar.is_some())));
    }
    Ok(answer(None, rows.len() as i64))
}
fn mysql_value(value: &mysql::Value) -> Result<Option<String>> {
    use mysql::Value::*;
    Ok(Some(match value {
        NULL => return Ok(None),
        Bytes(bytes) => String::from_utf8(bytes.clone())?,
        Int(n) => n.to_string(),
        UInt(n) => n.to_string(),
        Float(n) => n.to_string(),
        Double(n) => n.to_string(),
        Date(y, m, d, h, min, s, us) => {
            format!("{y:04}-{m:02}-{d:02} {h:02}:{min:02}:{s:02}.{us:06}")
        }
        _ => bail!("Unsupported MySQL time scalar"),
    }))
}
pub fn oracle(
    conn: &oracle::Connection,
    sql: &str,
    values: &[Option<String>],
    mode: &str,
) -> Result<Answer> {
    use oracle::sql_type::ToSql;
    use std::io::Write;
    let mut lobs = vec![];
    for value in values {
        let lob = if let Some(text) = value.as_ref().filter(|text| text.len() > 2000) {
            let mut lob = oracle::sql_type::Clob::new(conn)?;
            lob.write_all(text.as_bytes())?;
            Some(lob)
        } else {
            None
        };
        lobs.push(lob);
    }
    let args: Vec<&dyn ToSql> = values
        .iter()
        .zip(&lobs)
        .map(|(v, lob)| lob.as_ref().map(|lob| lob as &dyn ToSql).unwrap_or(v))
        .collect();
    if mode == "nonQuery" {
        return Ok(answer(None, conn.execute(sql, &args)?.row_count()? as i64));
    }
    let rows = conn.query(sql, &args)?;
    let mut count = 0;
    for row in rows {
        let row = row?;
        if mode == "scalar" {
            let value: Option<String> = row.get(0)?;
            return Ok(answer(value.clone(), i64::from(value.is_some())));
        }
        count += 1;
    }
    Ok(answer(None, count))
}
pub async fn tds(
    client: &mut crate::extended::Tds,
    text: &str,
    values: &[Option<String>],
    mode: &str,
    timeout: Duration,
) -> Result<Answer> {
    tokio::time::timeout(timeout, async {
        if mode == "nonQuery" && values.is_empty() {
            client.simple_query(text).await?.into_results().await?;
            return Ok(answer(None, 0));
        }
        let mut query = tiberius::Query::new(text);
        for value in values {
            query.bind(value.as_deref());
        }
        if mode == "nonQuery" {
            return Ok(answer(None, query.execute(client).await?.total() as i64));
        }
        let rows = query.query(client).await?.into_first_result().await?;
        if mode == "scalar" {
            let value = rows
                .first()
                .map(|row| -> Result<Option<String>> {
                    if let Ok(value) = row.try_get::<&str, _>(0) {
                        return Ok(value.map(str::to_owned));
                    }
                    if let Ok(value) = row.try_get::<i64, _>(0) {
                        return Ok(value.map(|v| v.to_string()));
                    }
                    if let Ok(value) = row.try_get::<i32, _>(0) {
                        return Ok(value.map(|v| v.to_string()));
                    }
                    if let Ok(value) = row.try_get::<f64, _>(0) {
                        return Ok(value.map(|v| v.to_string()));
                    }
                    if let Ok(value) = row.try_get::<bool, _>(0) {
                        return Ok(value.map(|v| i32::from(v).to_string()));
                    }
                    if let Ok(value) = row.try_get::<tiberius::numeric::Numeric, _>(0) {
                        return Ok(value.map(|v| v.to_string()));
                    }
                    bail!("Unsupported SQL Server scalar type")
                })
                .transpose()?
                .flatten();
            return Ok(answer(value.clone(), i64::from(value.is_some())));
        }
        Ok(answer(None, rows.len() as i64))
    })
    .await?
}
pub fn hex(raw: &[u8]) -> String {
    raw.iter().map(|b| format!("{b:02x}")).collect()
}
pub fn format_uuid(mut raw: [u8; 16], little: bool) -> String {
    if little {
        raw[..4].reverse();
        raw[4..6].reverse();
        raw[6..8].reverse();
    }
    format!(
        "{}-{}-{}-{}-{}",
        hex(&raw[..4]),
        hex(&raw[4..6]),
        hex(&raw[6..8]),
        hex(&raw[8..10]),
        hex(&raw[10..])
    )
}
pub fn date(value: time::OffsetDateTime) -> String {
    let value = value.to_offset(time::UtcOffset::UTC);
    format!(
        "{:04}-{:02}-{:02} {:02}:{:02}:{:02}.{:06}",
        value.year(),
        u8::from(value.month()),
        value.day(),
        value.hour(),
        value.minute(),
        value.second(),
        value.microsecond()
    )
}
