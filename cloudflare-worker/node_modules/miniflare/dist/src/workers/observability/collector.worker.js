// src/workers/observability/collector.worker.ts
import { WorkerEntrypoint } from "cloudflare:workers";

// src/workers/observability/tail-to-store.ts
function toMs(timestamp) {
  return typeof timestamp == "number" ? timestamp : timestamp.getTime();
}
function ids(event) {
  return event.event.type === "onset" || event.event.type === "spanOpen" ? {
    traceId: event.spanContext.traceId,
    spanId: event.event.spanId,
    parentId: event.spanContext.spanId
  } : {
    traceId: event.spanContext.traceId,
    spanId: event.spanContext.spanId
  };
}
function friendlyKind(faasTrigger, name) {
  if (faasTrigger)
    switch (faasTrigger) {
      case "http":
        return "http";
      case "timer":
        return "scheduled";
      case "pubsub":
        return "queue";
      case "email":
        return "email";
      case "jsrpc":
        return "jsrpc";
      case "websocket":
        return "websocket";
      case "trace":
        return "trace";
      default:
        return "worker";
    }
  let n = name.toLowerCase();
  return n.includes("kv") ? "kv" : n.includes("d1") ? "d1" : n.includes("r2") ? "r2" : n.includes("queue") ? "queue" : n.includes("durable") || n.includes("do_") ? "do" : n.includes("cache") ? "cache" : n.includes("fetch") ? "fetch" : "span";
}
function describeTrigger(info) {
  switch (info.type) {
    case "fetch":
      return {
        name: info.method,
        attributes: {
          "faas.trigger": "http",
          "http.request.method": info.method,
          "url.full": info.url
        }
      };
    case "jsrpc":
      return { name: "jsrpc", attributes: { "faas.trigger": "jsrpc" } };
    case "scheduled":
      return {
        name: "scheduled",
        attributes: { "faas.trigger": "timer", "faas.cron": info.cron }
      };
    case "alarm":
      return { name: "alarm", attributes: { "faas.trigger": "timer" } };
    case "queue":
      return {
        name: "queue",
        attributes: {
          "faas.trigger": "pubsub",
          "cloudflare.queue.name": info.queueName
        }
      };
    case "email":
      return {
        name: "email",
        attributes: {
          "faas.trigger": "email",
          "cloudflare.email.to": info.rcptTo
        }
      };
    case "trace":
      return { name: "trace", attributes: { "faas.trigger": "trace" } };
    case "hibernatableWebSocket":
      return {
        name: "hibernatableWebSocket",
        attributes: { "faas.trigger": "websocket" }
      };
    default:
      return { name: info.type, attributes: { "faas.trigger": "other" } };
  }
}
function isErrorOutcome(outcome) {
  return outcome !== "ok";
}
var TailToStoreHandler = class {
  constructor(store, onset, worker) {
    this.store = store;
    this.worker = worker;
    let { traceId, spanId, parentId } = ids(onset);
    if (!spanId)
      return;
    this.#rootSpanId = spanId, this.#traceId = traceId, this.#startMs = toMs(onset.timestamp), this.#latestEventMs = this.#startMs;
    let { name, attributes: triggerAttributes } = describeTrigger(
      onset.event.info
    ), attributes = {};
    for (let attr of onset.event.attributes ?? [])
      attributes[attr.name] = normalizeAttr(attr.value);
    Object.assign(attributes, triggerAttributes), onset.invocationId && (attributes["faas.invocation_id"] = onset.invocationId), this.#invocationBody = onset.event.info.type === "fetch" ? `${onset.event.info.method} ${onset.event.info.url}` : name, this.#spans.set(spanId, {
      traceId,
      startMs: this.#startMs,
      name,
      outcome: null,
      error: null,
      errored: !1,
      closed: !1
    }), this.#open({
      traceId,
      spanId,
      // A sub-invocation (e.g. downstream of a service binding) carries the
      // caller's span as its parent, so it nests into one distributed trace;
      // a true top-level invocation has no inherited parent (→ null root).
      parentId: parentId ?? null,
      service: this.worker ?? null,
      name,
      kind: friendlyKind(attributes["faas.trigger"], name),
      startMs: this.#startMs,
      durationMs: null,
      outcome: null,
      error: null,
      attributes
    }), this.#flush();
  }
  store;
  worker;
  #spans = /* @__PURE__ */ new Map();
  #rootSpanId = null;
  #traceId = null;
  #startMs = null;
  #invocationBody = null;
  #writes = [];
  #rows = /* @__PURE__ */ new Map();
  #dirty = /* @__PURE__ */ new Set();
  #logs = [];
  #latestEventMs = 0;
  #lastFlushMs = 0;
  spanOpen(event) {
    this.#latestEventMs = toMs(event.timestamp);
    let { traceId, spanId, parentId } = ids(event);
    if (!spanId)
      return;
    let startMs = toMs(event.timestamp);
    this.#spans.set(spanId, {
      traceId,
      startMs,
      name: event.event.name,
      outcome: null,
      error: null,
      errored: !1,
      closed: !1
    }), this.#open({
      traceId,
      spanId,
      parentId: parentId ?? null,
      service: this.worker ?? null,
      name: event.event.name,
      kind: friendlyKind(void 0, event.event.name),
      startMs,
      durationMs: null,
      outcome: null,
      error: null,
      attributes: null
    });
  }
  spanClose(event) {
    this.#latestEventMs = toMs(event.timestamp);
    let { traceId, spanId } = ids(event), pending = spanId ? this.#spans.get(spanId) : void 0;
    spanId && pending && (isErrorOutcome(event.event.outcome) && (pending.errored = !0, pending.outcome ??= event.event.outcome), this.#close(traceId, spanId, pending, toMs(event.timestamp), null));
  }
  attributes(event) {
    this.#latestEventMs = toMs(event.timestamp);
    let { traceId, spanId } = ids(event);
    if (!spanId || !this.#spans.has(spanId))
      return;
    let attrs = {};
    for (let attr of event.event.info)
      attrs[attr.name] = normalizeAttr(attr.value);
    Object.keys(attrs).length > 0 && this.#merge(traceId, spanId, attrs);
  }
  return(event) {
    this.#latestEventMs = toMs(event.timestamp);
    let { traceId, spanId } = ids(event), pending = spanId ? this.#spans.get(spanId) : void 0;
    spanId && pending && event.event.info?.type === "fetch" && this.#merge(traceId, spanId, {
      "http.response.status_code": event.event.info.statusCode
    });
  }
  log(event) {
    this.#latestEventMs = toMs(event.timestamp);
    let { traceId, spanId } = ids(event), level = event.event.level === "log" ? "info" : event.event.level;
    this.#append({
      traceId,
      spanId: spanId ?? null,
      tsMs: toMs(event.timestamp),
      level,
      message: serialize(event.event.message),
      operation: null
    });
  }
  exception(event) {
    this.#latestEventMs = toMs(event.timestamp);
    let { traceId, spanId } = ids(event), pending = spanId ? this.#spans.get(spanId) : void 0, type = event.event.name || "Error", message = event.event.message ?? "", head = `${type}: ${message}`, text = event.event.stack ? `${head}
${event.event.stack}` : head;
    pending && (pending.errored = !0, pending.outcome = "error", pending.error = head), this.#append({
      traceId,
      spanId: spanId ?? null,
      tsMs: toMs(event.timestamp),
      level: "error",
      message: serialize(text),
      operation: pending?.name ?? null
    });
  }
  async outcome(event) {
    let endMs = toMs(event.timestamp);
    this.#latestEventMs = endMs;
    let traceId = this.#traceId ?? event.spanContext.traceId, root = this.#rootSpanId ? this.#spans.get(this.#rootSpanId) : void 0;
    root && this.#rootSpanId && (isErrorOutcome(event.event.outcome) && (root.errored = !0), root.outcome = event.event.outcome, this.#close(traceId, this.#rootSpanId, root, endMs, {
      "cloudflare.outcome": event.event.outcome,
      cpu_time_ms: event.event.cpuTime,
      wall_time_ms: event.event.wallTime
    }));
    for (let [spanId, pending] of this.#spans)
      pending.closed || this.#close(pending.traceId, spanId, pending, endMs, null);
    this.#rootSpanId && this.#invocationBody !== null && this.#append({
      traceId,
      spanId: this.#rootSpanId,
      tsMs: this.#startMs ?? endMs,
      level: root?.errored ? "error" : "info",
      message: serialize(this.#invocationBody),
      operation: null
    }), this.#flush(), await Promise.all(this.#writes);
  }
  #open(input) {
    let key = rowKey(input.traceId, input.spanId);
    this.#rows.set(key, input), this.#dirty.add(key), this.#maybeFlush();
  }
  /** Fold attributes into a buffered row, so merging costs no round-trip. */
  #merge(traceId, spanId, attributes) {
    let key = rowKey(traceId, spanId), row = this.#rows.get(key);
    row && (row.attributes = { ...row.attributes ?? {}, ...attributes }, this.#dirty.add(key), this.#maybeFlush());
  }
  #append(log) {
    this.#logs.push(log), this.#flush();
  }
  #maybeFlush() {
    let buffered = this.#dirty.size + this.#logs.length;
    buffered !== 0 && (buffered >= 16 || this.#latestEventMs - this.#lastFlushMs >= 100) && this.#flush();
  }
  /**
   * Write everything buffered since the last flush. Rows stay in the map after
   * flushing so a later close still carries the whole row — `persist` upserts,
   * so re-sending a row is safe.
   */
  #flush() {
    if (this.#dirty.size === 0 && this.#logs.length === 0)
      return;
    let spans = [];
    for (let key of this.#dirty) {
      let row = this.#rows.get(key);
      row && spans.push(row);
    }
    let logs = this.#logs;
    this.#dirty.clear(), this.#logs = [], this.#lastFlushMs = this.#latestEventMs, this.#track(this.store.persist(spans, logs));
  }
  /** Finish a span, setting its duration and outcome and adding any final
   * attributes. Runs at most once per span (guarded by `closed`). */
  #close(traceId, spanId, pending, endMs, attributes) {
    if (pending.closed)
      return;
    pending.closed = !0;
    let key = rowKey(traceId, spanId), row = this.#rows.get(key);
    row && (row.durationMs = Math.max(0, endMs - pending.startMs), row.outcome = pending.outcome ?? (pending.errored ? "error" : "ok"), row.error = pending.error, attributes && (row.attributes = { ...row.attributes ?? {}, ...attributes }), this.#dirty.add(key), this.#maybeFlush());
  }
  /** Track an in-flight store write so `outcome` can await completion. */
  #track(result) {
    this.#writes.push(Promise.resolve(result));
  }
};
function rowKey(traceId, spanId) {
  return `${traceId}\0${spanId}`;
}
function normalizeAttr(value) {
  return Array.isArray(value) ? value.map((v) => typeof v == "bigint" ? bigintToJson(v) : v) : typeof value == "bigint" ? bigintToJson(value) : value;
}
function bigintToJson(value) {
  return value >= Number.MIN_SAFE_INTEGER && value <= Number.MAX_SAFE_INTEGER ? Number(value) : value.toString();
}
function serialize(value) {
  try {
    return JSON.stringify(value ?? "");
  } catch {
    return String(value);
  }
}

// src/workers/observability/trace-store.ts
import { DurableObject } from "cloudflare:workers";
var SCHEMA = [
  `CREATE TABLE IF NOT EXISTS spans (
		trace_id     TEXT NOT NULL,
		span_id      TEXT NOT NULL,
		parent_id    TEXT,
		service      TEXT,
		name         TEXT,
		kind         TEXT,
		start_ms     INTEGER,
		duration_ms  INTEGER,          -- whole ms; NULL while the span is still running
		outcome      TEXT,
		error        TEXT,
		attributes   BLOB,
		created_at   TEXT DEFAULT (datetime('now')),
		PRIMARY KEY (trace_id, span_id)
	)`,
  "CREATE INDEX IF NOT EXISTS spans_roots ON spans (start_ms) WHERE parent_id IS NULL",
  `CREATE TABLE IF NOT EXISTS logs (
		trace_id   TEXT NOT NULL,
		span_id    TEXT,
		seq        INTEGER NOT NULL,
		ts_ms      INTEGER,
		level      TEXT,
		message    TEXT,
		operation  TEXT,
		created_at TEXT DEFAULT (datetime('now')),
		PRIMARY KEY (trace_id, seq)
	)`,
  "CREATE INDEX IF NOT EXISTS logs_by_level ON logs (level)"
], MAX_QUERY_ROWS = 1e4, TraceStore = class extends DurableObject {
  sql = this.ctx.storage.sql;
  constructor(ctx, env) {
    super(ctx, env), this.ctx.blockConcurrencyWhile(async () => {
      for (let stmt of SCHEMA)
        this.sql.exec(stmt);
    });
  }
  /** Persist one invocation's spans + logs. Called by the collector. */
  persist(spans, logs) {
    for (let s of spans)
      this.sql.exec(
        `INSERT INTO spans
					(trace_id, span_id, parent_id, service, name, kind, start_ms, duration_ms, outcome, error, attributes)
					VALUES (?,?,?,?,?,?,?,?,?,?, jsonb(?))
					ON CONFLICT (trace_id, span_id) DO UPDATE SET
						parent_id = excluded.parent_id,
						service = excluded.service,
						name = excluded.name,
						kind = excluded.kind,
						start_ms = excluded.start_ms,
						duration_ms = excluded.duration_ms,
						outcome = excluded.outcome,
						error = excluded.error,
						attributes = excluded.attributes`,
        s.traceId,
        s.spanId,
        s.parentId,
        s.service,
        s.name,
        s.kind,
        s.startMs,
        s.durationMs == null ? null : Math.round(s.durationMs),
        s.outcome,
        s.error,
        s.attributes ? JSON.stringify(s.attributes) : null
      );
    let nextSeq = /* @__PURE__ */ new Map();
    for (let l of logs) {
      let seq = nextSeq.get(l.traceId);
      if (seq === void 0) {
        let row = this.sql.exec(
          "SELECT COALESCE(MAX(seq), -1) + 1 AS next FROM logs WHERE trace_id = ?",
          l.traceId
        ).one();
        seq = Number(row.next);
      }
      this.sql.exec(
        `INSERT INTO logs
					(trace_id, span_id, seq, ts_ms, level, message, operation)
					VALUES (?,?,?,?,?,?,?)`,
        l.traceId,
        l.spanId,
        seq,
        l.tsMs,
        l.level,
        l.message,
        l.operation
      ), nextSeq.set(l.traceId, seq + 1);
    }
  }
  /**
   * Write-through capture (for long-running spans). A span is written across
   * its lifetime instead of all at once on `outcome`, so the UI can show it
   * in-flight: `openSpan` on start, `mergeAttributes` as they stream in, and
   * `closeSpan` when it ends. `duration_ms IS NULL` marks a still-open span.
   */
  /** Insert a span at open time (duration/outcome stay NULL until it closes). */
  openSpan(s) {
    this.sql.exec(
      `INSERT INTO spans
				(trace_id, span_id, parent_id, service, name, kind, start_ms, duration_ms, outcome, error, attributes)
				VALUES (?,?,?,?,?,?,?,?,?,?, jsonb(?))
				ON CONFLICT (trace_id, span_id) DO NOTHING`,
      s.traceId,
      s.spanId,
      s.parentId,
      s.service,
      s.name,
      s.kind,
      s.startMs,
      s.durationMs == null ? null : Math.round(s.durationMs),
      s.outcome,
      s.error,
      s.attributes ? JSON.stringify(s.attributes) : null
    );
  }
  /** Merge attributes onto an open span as the tail stream emits them. */
  mergeAttributes(traceId, spanId, attributes) {
    this.sql.exec(
      `UPDATE spans
				SET attributes = jsonb_patch(COALESCE(attributes, jsonb('{}')), jsonb(?))
				WHERE trace_id = ? AND span_id = ?`,
      JSON.stringify(attributes),
      traceId,
      spanId
    );
  }
  /** Finalise a span: set duration/outcome/error and merge any final attributes. */
  closeSpan(traceId, spanId, close) {
    this.sql.exec(
      `UPDATE spans
				SET duration_ms = ?, outcome = ?, error = ?,
					attributes = jsonb_patch(COALESCE(attributes, jsonb('{}')), jsonb(?))
				WHERE trace_id = ? AND span_id = ?`,
      Math.round(close.durationMs),
      close.outcome,
      close.error,
      close.attributes ? JSON.stringify(close.attributes) : "{}",
      traceId,
      spanId
    );
  }
  /** Append a single log, assigning the next per-trace `seq` (store-owned). */
  appendLog(log) {
    let { next } = this.sql.exec(
      "SELECT COALESCE(MAX(seq), -1) + 1 AS next FROM logs WHERE trace_id = ?",
      log.traceId
    ).one();
    this.sql.exec(
      `INSERT INTO logs
				(trace_id, span_id, seq, ts_ms, level, message, operation)
				VALUES (?,?,?,?,?,?,?)`,
      log.traceId,
      log.spanId,
      Number(next),
      log.tsMs,
      log.level,
      log.message,
      log.operation
    );
  }
  /**
   * The only way to read the store: a single read-only SQL query. The
   * Observability tab and coding agents both go through here (the UI has a set of
   * built-in queries; agents write their own), so the `spans` and `logs` schema
   * acts as the contract and is documented in the `/query` endpoint's OpenAPI
   * description.
   *
   * Because this runs SQL we did not write, and workerd's DO SQLite has no
   * read-only execution mode to rely on (`PRAGMA query_only` is rejected with
   * SQLITE_AUTH), we guard it two ways. First a syntactic check: checking only
   * the first keyword is not enough (`WITH … DELETE` is a single statement that
   * still starts with `WITH`), so we remove comments and anything inside quotes —
   * so a value like `'…delete…'` or a `;` inside a string cannot trip the checks —
   * then require a single statement (no `;`) that starts with `SELECT`/`WITH` and
   * contains no data- or schema-changing keyword. Second, and not relying on that
   * regex, the statement runs inside a transaction that is always rolled back, so
   * any write or DDL that slipped past the checks is discarded rather than
   * persisted. Values are always passed as bound `params`, and at most
   * `MAX_QUERY_ROWS` rows are returned.
   *
   * `attributes` is stored as JSONB; wrap it with `json(attributes)` to read it
   * back as JSON (the built-in queries already do).
   */
  query(sql, params = []) {
    let statement = sql.trim().replace(/;\s*$/, ""), stripped = statement.replace(/--[^\n]*/g, " ").replace(/\/\*[\s\S]*?\*\//g, " ").replace(/'(?:[^']|'')*'/g, "''").replace(/"(?:[^"]|"")*"/g, '""').trim();
    if (!/^(SELECT|WITH)\b/i.test(stripped))
      throw new Error("Only read-only SELECT/WITH queries are allowed");
    if (stripped.includes(";"))
      throw new Error("Only a single statement is allowed");
    if (/\b(INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|TRUNCATE|PRAGMA|ATTACH|DETACH|VACUUM|REINDEX|ANALYZE|BEGIN|COMMIT|ROLLBACK|SAVEPOINT|RELEASE|LOAD_EXTENSION)\b|\bREPLACE\s+INTO\b/i.test(
      stripped
    ))
      throw new Error("Only read-only SELECT/WITH queries are allowed");
    let columns = [], rows = [], rollback = /* @__PURE__ */ Symbol("rollback");
    try {
      this.ctx.storage.transactionSync(() => {
        let cursor = this.sql.exec(statement, ...params);
        columns.push(...cursor.columnNames);
        for (let row of cursor.raw()) {
          if (rows.length >= MAX_QUERY_ROWS)
            break;
          rows.push([...row]);
        }
        throw rollback;
      });
    } catch (e) {
      if (e !== rollback)
        throw e;
    }
    return { columns, rows };
  }
  /** Delete all captured data. An RPC method; nothing calls it yet. */
  clear() {
    this.sql.exec("DELETE FROM logs"), this.sql.exec("DELETE FROM spans");
  }
};

// src/workers/observability/collector.worker.ts
var LocalObservabilityCollector = class extends WorkerEntrypoint {
  tailStream(onset) {
    let store = this.env.TRACE_STORE.get(
      this.env.TRACE_STORE.idFromName("singleton")
    ), worker = this.ctx.props?.worker;
    return new TailToStoreHandler(store, onset, worker);
  }
  /**
   * Reads from the trace store. A single `POST /query` runs read-only SQL against
   * the `spans` and `logs` tables. The Local Explorer's Observability API
   * forwards requests here, so the UI's built-in views and any coding agent all
   * go through this one endpoint. The query validation lives in
   * `TraceStore.query`.
   */
  async fetch(request) {
    let url = new URL(request.url), store = this.env.TRACE_STORE.get(
      this.env.TRACE_STORE.idFromName("singleton")
    );
    if (url.pathname === "/query" && request.method === "POST") {
      let { sql, params } = await request.json();
      if (typeof sql != "string")
        return Response.json({ error: "missing 'sql'" }, { status: 400 });
      try {
        return Response.json(await store.query(sql, params ?? []));
      } catch (err) {
        let message = err instanceof Error ? err.message : String(err);
        return Response.json({ error: message }, { status: 400 });
      }
    }
    return url.pathname === "/clear" && request.method === "POST" ? (await store.clear(), Response.json({ success: !0 })) : new Response("not found", { status: 404 });
  }
};
export {
  TraceStore,
  LocalObservabilityCollector as default
};
//# sourceMappingURL=collector.worker.js.map
