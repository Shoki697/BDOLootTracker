// src/workers/email/email-store.worker.ts
import { WorkerEntrypoint } from "cloudflare:workers";

// src/workers/email/email-store.ts
import { DurableObject } from "cloudflare:workers";
import { z as z2 } from "miniflare:zod";

// src/workers/email/capture.ts
var MAX_EMAIL_BODY_BYTES = Math.floor(499744) * 3, encoder = new TextEncoder();
function bytesToBase64(bytes) {
  let binary = "";
  for (let offset = 0; offset < bytes.byteLength; offset += 32768)
    binary += String.fromCharCode(
      ...bytes.subarray(offset, offset + 32768)
    );
  return btoa(binary);
}
function base64ToBytes(encoded) {
  let binary = atob(encoded), bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++)
    bytes[index] = binary.charCodeAt(index);
  return bytes;
}

// src/workers/email/contracts.ts
import { z } from "miniflare:zod";
var zEmailHandlerEvent = z.discriminatedUnion("type", [
  z.object({
    type: z.enum(["received", "reject", "unhandled"]),
    timestamp: z.string().describe("ISO 8601 timestamp of when the event occurred.")
  }),
  z.object({
    type: z.enum(["forward", "reply"]),
    timestamp: z.string().describe("ISO 8601 timestamp of when the event occurred."),
    messageId: z.string().describe("Correlates with the matching `forwards`/`replies` entry.")
  })
]).describe(
  "One entry in the ordered lifecycle of what the handler did to the message. `received` is first for any message actually delivered to an `email()` handler. The exception is `unhandled`: when the Worker exports no `email()` handler the message never reaches one, so the timeline is a single `unhandled` event with no preceding `received`. `forward`/`reply` events carry a `messageId` correlating with the matching `forwards`/`replies` entry."
), zEmailHeaders = z.array(z.tuple([z.string(), z.string()])).describe("Email headers as ordered name/value pairs, including duplicates."), zEmailHandlerForward = z.object({
  messageId: z.string(),
  recipient: z.string().describe("Envelope recipient the message was forwarded to."),
  headers: z.array(z.tuple([z.string(), z.string()])).describe("Headers added to the forwarded message.")
}), zEmailHandlerReplyBase = z.object({
  messageId: z.string(),
  sender: z.string().describe("Address the reply was sent from.")
}), zEmailHandlerReplyApi = zEmailHandlerReplyBase.extend({
  raw: z.string().describe(
    "Raw MIME content of the reply. Omitted from the routing list; present on the detail response."
  ).optional(),
  rawBase64: z.string().describe("Lossless base64 representation of the reply MIME.").optional()
}), zEmailHandlerReply = zEmailHandlerReplyBase.extend({
  raw: z.string().describe("Raw MIME content of the reply."),
  rawBase64: z.string().describe("Lossless base64 representation of the reply MIME.").optional()
}), zEmailHandlerResult = z.object({
  outcome: z.enum(["ok", "exception"]),
  rejectReason: z.string().describe(
    "Reason passed to `setReject()`, if the handler rejected the message."
  ).optional(),
  forwards: z.array(zEmailHandlerForward),
  replies: z.array(zEmailHandlerReply),
  events: z.array(zEmailHandlerEvent).describe(
    "Ordered lifecycle of everything the handler did to the message."
  )
}), zEmailAttachment = z.object({
  filename: z.string(),
  contentType: z.string(),
  disposition: z.enum(["inline", "attachment"]),
  size: z.number()
}).describe(
  "Metadata describing an attachment on a captured email, without its content."
), zEmailBase = z.object({
  worker: z.string().describe("Worker associated with the email, if known.").optional(),
  from: z.string().describe("Envelope MAIL FROM address."),
  subject: z.string(),
  messageId: z.string().describe(
    "RFC Message-ID header value. Identifies the email in the store."
  ),
  attachments: z.array(zEmailAttachment).describe(
    "Metadata for attachments parsed out of the email. The content itself is only available in the raw MIME."
  )
}), zEmailRoutingItem = zEmailBase.extend({
  to: z.string().describe("Envelope RCPT TO address."),
  cc: z.array(z.string()).optional(),
  headers: z.record(z.string(), z.string()).optional(),
  headerEntries: zEmailHeaders.optional(),
  receivedAt: z.string(),
  rawSize: z.number(),
  outcome: z.enum(["ok", "exception"]).describe("Whether the handler ran to completion or threw."),
  rejectReason: z.string().describe(
    "Reason passed to setReject(), if the handler rejected the message."
  ).optional(),
  forwards: z.array(zEmailHandlerForward),
  replies: z.array(zEmailHandlerReplyApi),
  events: z.array(zEmailHandlerEvent)
}), zEmailRoutingDetail = zEmailRoutingItem.extend({
  text: z.string().describe("Parsed plain text body, when present.").optional(),
  html: z.string().describe("Parsed HTML body, when present.").optional(),
  raw: z.string().describe("Raw MIME content of the received email."),
  rawBase64: z.string().describe("Lossless base64 representation of the received MIME.").optional()
}), zEmailSendAttachment = z.object({
  filename: z.string().describe("Name the attachment is presented under."),
  type: z.string().describe("MIME type of the attachment, e.g. 'application/pdf'."),
  content: z.string().describe(
    "Attachment content, base64-encoded. MessageBuilder takes raw bytes here, but this endpoint accepts JSON so the bytes must be base64-encoded."
  ),
  contentId: z.string().describe("Content-ID for an inline attachment.").optional(),
  disposition: z.enum(["inline", "attachment"]).describe("How the attachment is presented. Defaults to 'attachment'.").optional()
}), zEmailSendRequest = z.object({
  from: z.string().describe("Sender address."),
  to: z.array(z.string()).min(1).describe("Recipient addresses."),
  cc: z.array(z.string()).optional(),
  bcc: z.array(z.string()).optional(),
  replyTo: z.string().optional(),
  subject: z.string(),
  text: z.string().describe("Plain text body.").optional(),
  html: z.string().describe("HTML body.").optional(),
  headers: z.record(z.string(), z.string()).describe("Custom headers to include on the message.").optional(),
  attachments: z.array(zEmailSendAttachment).describe(
    "Attachments to include on the message, mirroring the MessageBuilder `attachments` entries accepted by a send_email binding. Adding any attachment composes the message as multipart/mixed."
  ).optional()
}).describe("Fields for composing a test email, mirroring MessageBuilder."), zEmailSendingItem = zEmailBase.extend({
  to: z.array(z.string()),
  cc: z.array(z.string()).optional(),
  bcc: z.array(z.string()).optional(),
  replyTo: z.string().optional(),
  sentAt: z.string(),
  headers: z.record(z.string(), z.string()).optional()
}), zEmailSendingDetail = zEmailSendingItem.extend({
  text: z.string().optional(),
  html: z.string().optional(),
  raw: z.string().describe("Raw MIME content, present when sent via the EmailMessage API.").optional(),
  rawBase64: z.string().describe("Lossless base64 representation of sent MIME.").optional()
});

// src/workers/email/message-id.ts
function messageIdToStorageId(messageId) {
  return messageId.replace(/^<|>$/g, "");
}

// src/workers/email/email-store.ts
function decodeCapturedRaw(rawBase64, truncated) {
  let bytes = base64ToBytes(rawBase64);
  return new TextDecoder().decode(
    truncated ? trimIncompleteUtf8Suffix(bytes) : bytes
  );
}
function trimIncompleteUtf8Suffix(bytes) {
  if (bytes.byteLength === 0)
    return bytes;
  let sequenceStart = bytes.byteLength - 1;
  for (; sequenceStart > 0 && (bytes[sequenceStart] & 192) === 128 && bytes.byteLength - sequenceStart < 4; )
    sequenceStart--;
  let leadingByte = bytes[sequenceStart], expectedLength = (leadingByte & 128) === 0 ? 1 : (leadingByte & 224) === 192 ? 2 : (leadingByte & 240) === 224 ? 3 : (leadingByte & 248) === 240 ? 4 : 1;
  return bytes.byteLength - sequenceStart < expectedLength ? bytes.subarray(0, sequenceStart) : bytes;
}
function materialiseReceivedEmail(email, rawBase64, replyRawBase64) {
  return {
    ...email,
    raw: decodeCapturedRaw(rawBase64, email.captureTruncated === !0),
    rawBase64,
    replies: email.replies.map((reply, index) => {
      let encoded = replyRawBase64.get(index);
      if (encoded === void 0)
        throw new Error(
          `Received email ${email.messageId} has no captured reply body at index ${index}`
        );
      return {
        ...reply,
        raw: decodeCapturedRaw(encoded, reply.captureTruncated === !0),
        rawBase64: encoded
      };
    })
  };
}
function materialiseSentEmail(email) {
  return email.raw !== void 0 || email.rawBase64 === void 0 ? email : {
    ...email,
    raw: decodeCapturedRaw(email.rawBase64, email.captureTruncated === !0)
  };
}
var SCHEMA = [
  `CREATE TABLE IF NOT EXISTS email_store_metadata (
		key TEXT PRIMARY KEY,
		value TEXT NOT NULL
	)`,
  `CREATE TABLE IF NOT EXISTS emails (
		seq  INTEGER PRIMARY KEY AUTOINCREMENT,
		kind TEXT NOT NULL CHECK (kind IN ('received', 'sent')),
		id   TEXT NOT NULL,
		created_at TEXT NOT NULL,
		data TEXT NOT NULL
	)`,
  "CREATE INDEX IF NOT EXISTS emails_by_kind_seq ON emails (kind, seq DESC)",
  `CREATE INDEX IF NOT EXISTS emails_by_kind_created_seq ON emails (
			kind, created_at DESC, seq DESC
		)`,
  "CREATE INDEX IF NOT EXISTS emails_by_kind_id ON emails (kind, id)",
  `CREATE INDEX IF NOT EXISTS emails_by_kind_worker_seq ON emails (
			kind, json_extract(data, '$.worker'), seq DESC
		)`,
  `CREATE INDEX IF NOT EXISTS emails_by_kind_worker_created_seq ON emails (
			kind, json_extract(data, '$.worker'), created_at DESC, seq DESC
		)`,
  `CREATE TABLE IF NOT EXISTS received_email_bodies (
			capture_id TEXT NOT NULL,
			part INTEGER NOT NULL,
			raw_base64 TEXT NOT NULL,
			PRIMARY KEY (capture_id, part)
		)`
], zStoredEmailReply = zEmailHandlerReplyApi.omit({
  raw: !0,
  rawBase64: !0
}), zStoredEmailReplyMetadata = zStoredEmailReply.extend({
  captureTruncated: z2.boolean().optional()
}), zStoredRoutingEmailSummary = zEmailBase.extend({
  to: z2.string(),
  cc: z2.array(z2.string()).optional(),
  headers: z2.record(z2.string(), z2.string()).optional(),
  headerEntries: zEmailHeaders.optional(),
  receivedAt: z2.string(),
  rawSize: z2.number(),
  outcome: z2.enum(["ok", "exception"]),
  rejectReason: z2.string().optional(),
  forwards: z2.array(zEmailHandlerForward),
  replies: z2.array(zStoredEmailReply),
  events: z2.array(zEmailHandlerEvent)
}), zStoredRoutingEmailMetadata = zStoredRoutingEmailSummary.extend({
  captureTruncated: z2.boolean().optional(),
  replies: z2.array(zStoredEmailReplyMetadata)
}), zStoredRoutingEmail = zStoredRoutingEmailMetadata.extend({
  raw: z2.string(),
  rawBase64: z2.string(),
  replies: z2.array(
    zEmailHandlerReplyApi.extend({
      raw: z2.string(),
      rawBase64: z2.string(),
      captureTruncated: z2.boolean().optional()
    })
  )
}), encoder2 = new TextEncoder();
function assertEmailRowValueFits(value, description) {
  if (encoder2.encode(value).byteLength > 1998976)
    throw new RangeError(
      `${description} exceeds the ${1998976}-byte email storage row value limit`
    );
}
function createStatements(kind) {
  return {
    insert: `INSERT INTO emails (kind, id, created_at, data)
			VALUES ('${kind}', ?, ?, ?) RETURNING seq`,
    list: `SELECT seq, created_at, data FROM emails
			WHERE kind = '${kind}'
			ORDER BY created_at DESC, seq DESC LIMIT ?`,
    listForWorker: `SELECT seq, created_at, data FROM emails
			WHERE kind = '${kind}' AND json_extract(data, '$.worker') = ?
			ORDER BY created_at DESC, seq DESC LIMIT ?`,
    listAfter: `SELECT seq, created_at, data FROM emails
			WHERE kind = '${kind}'
			AND (created_at < ? OR (created_at = ? AND seq < ?))
			ORDER BY created_at DESC, seq DESC LIMIT ?`,
    listAfterForWorker: `SELECT seq, created_at, data FROM emails
			WHERE kind = '${kind}'
			AND (created_at < ? OR (created_at = ? AND seq < ?))
			AND json_extract(data, '$.worker') = ?
			ORDER BY created_at DESC, seq DESC LIMIT ?`,
    find: `SELECT seq, data FROM emails WHERE kind = '${kind}' AND id = ?
			ORDER BY seq DESC LIMIT 1`,
    findForWorker: `SELECT seq, data FROM emails
			WHERE kind = '${kind}' AND id = ?
			AND json_extract(data, '$.worker') = ?
			ORDER BY seq DESC LIMIT 1`
  };
}
var STATEMENTS = {
  received: createStatements("received"),
  sent: createStatements("sent"),
  insertReceivedBody: `INSERT INTO received_email_bodies
		(capture_id, part, raw_base64) VALUES (?, ?, ?)`,
  countReceivedBodies: `SELECT COUNT(*) AS count, MIN(part) AS first_part,
		MAX(part) AS last_part FROM received_email_bodies WHERE capture_id = ?`,
  findReceivedBodies: `SELECT part, raw_base64 FROM received_email_bodies
		WHERE capture_id = ? ORDER BY part`,
  discardReceivedBodies: "DELETE FROM received_email_bodies WHERE capture_id = ?",
  discardReceivedMetadata: "DELETE FROM emails WHERE kind = 'received' AND json_extract(data, '$.bodyId') = ?",
  insertMetadata: `INSERT OR IGNORE INTO email_store_metadata (key, value)
		VALUES (?, ?)`,
  findMetadata: "SELECT value FROM email_store_metadata WHERE key = ?",
  clearReceivedBodies: "DELETE FROM received_email_bodies",
  clear: "DELETE FROM emails"
}, DEFAULT_LIST_LIMIT = 25, MAX_LIST_LIMIT = 100;
function encodeCursor(cursor) {
  return bytesToBase64(new TextEncoder().encode(JSON.stringify(cursor)));
}
function decodeCursor(value) {
  try {
    let cursor = JSON.parse(
      new TextDecoder().decode(base64ToBytes(value))
    );
    if (typeof cursor.createdAt != "string" || typeof cursor.seq != "number" || !Number.isSafeInteger(cursor.seq))
      throw new Error("Invalid cursor");
    return cursor;
  } catch {
    throw new TypeError("Invalid email pagination cursor");
  }
}
function normaliseLimit(limit) {
  if (limit === void 0)
    return DEFAULT_LIST_LIMIT;
  if (!Number.isSafeInteger(limit) || limit < 1 || limit > MAX_LIST_LIMIT)
    throw new RangeError("Invalid email pagination limit");
  return limit;
}
function getSentSummary(email) {
  let {
    text: _text,
    html: _html,
    raw: _raw,
    rawBase64: _rawBase64,
    captureTruncated: _captureTruncated,
    ...summary
  } = email;
  return summary;
}
var EmailStore = class extends DurableObject {
  sql = this.ctx.storage.sql;
  constructor(ctx, env) {
    super(ctx, env), this.ctx.blockConcurrencyWhile(async () => {
      for (let stmt of SCHEMA)
        this.sql.exec(stmt);
      this.sql.exec(
        STATEMENTS.insertMetadata,
        "source_id",
        crypto.randomUUID()
      );
    });
  }
  getSourceId() {
    let row = this.sql.exec(STATEMENTS.findMetadata, "source_id").toArray()[0];
    if (row === void 0)
      throw new Error("Email store source ID is unavailable");
    return row.value;
  }
  #insert(table, id, createdAt, data) {
    let encoded = JSON.stringify(data);
    assertEmailRowValueFits(encoded, `${table} email metadata`);
    let row = this.sql.exec(STATEMENTS[table].insert, id, createdAt, encoded).toArray()[0];
    if (row === void 0)
      throw new Error(`Failed to store ${table} email`);
    return row.seq;
  }
  /** Newest-first cursor page of records from a table. */
  #list(table, parse, cursor, limit, worker) {
    let pageSize = normaliseLimit(limit), rows = cursor === void 0 ? this.sql.exec(
      worker === void 0 ? STATEMENTS[table].list : STATEMENTS[table].listForWorker,
      ...worker === void 0 ? [pageSize + 1] : [worker, pageSize + 1]
    ).toArray() : (() => {
      let decoded = decodeCursor(cursor);
      return this.sql.exec(
        worker === void 0 ? STATEMENTS[table].listAfter : STATEMENTS[table].listAfterForWorker,
        ...worker === void 0 ? [
          decoded.createdAt,
          decoded.createdAt,
          decoded.seq,
          pageSize + 1
        ] : [
          decoded.createdAt,
          decoded.createdAt,
          decoded.seq,
          worker,
          pageSize + 1
        ]
      ).toArray();
    })(), hasMore = rows.length > pageSize, pageRows = rows.slice(0, pageSize), last = pageRows.at(-1);
    return {
      items: pageRows.map(({ data }) => parse(data)),
      hasMore,
      ...hasMore && last !== void 0 ? {
        cursor: encodeCursor({
          createdAt: last.created_at,
          seq: last.seq
        })
      } : {}
    };
  }
  /** Most recently stored full record with the given message ID. */
  #find(table, id, worker) {
    let row = this.sql.exec(
      worker === void 0 ? STATEMENTS[table].find : STATEMENTS[table].findForWorker,
      ...worker === void 0 ? [id] : [id, worker]
    ).toArray()[0];
    return row === void 0 ? void 0 : JSON.parse(row.data);
  }
  storeReceivedBody(captureId, part, rawBase64) {
    if (!Number.isSafeInteger(part) || part < 0)
      throw new RangeError("Invalid received email body part");
    assertEmailRowValueFits(rawBase64, "Received email body"), this.sql.exec(STATEMENTS.insertReceivedBody, captureId, part, rawBase64);
  }
  storeReceivedMetadata(captureId, expectedBodyParts, email) {
    if (!Number.isSafeInteger(expectedBodyParts) || expectedBodyParts < 1)
      throw new RangeError("Invalid received email body count");
    this.ctx.storage.transactionSync(() => {
      let bodies = this.sql.exec(STATEMENTS.countReceivedBodies, captureId).toArray()[0];
      if (bodies === void 0 || bodies.count !== expectedBodyParts || bodies.first_part !== 0 || bodies.last_part !== expectedBodyParts - 1)
        throw new Error(
          `Received email ${email.messageId} has incomplete captured bodies`
        );
      this.#insert(
        "received",
        messageIdToStorageId(email.messageId),
        email.receivedAt,
        { ...email, bodyId: captureId }
      );
    });
  }
  discardReceived(captureId) {
    this.ctx.storage.transactionSync(() => {
      this.sql.exec(STATEMENTS.discardReceivedBodies, captureId), this.sql.exec(STATEMENTS.discardReceivedMetadata, captureId);
    });
  }
  findReceived(id, worker) {
    let row = this.sql.exec(
      worker === void 0 ? STATEMENTS.received.find : STATEMENTS.received.findForWorker,
      ...worker === void 0 ? [id] : [id, worker]
    ).toArray()[0];
    if (row === void 0)
      return;
    let stored = JSON.parse(row.data), bodyId = typeof stored == "object" && stored !== null && "bodyId" in stored && typeof stored.bodyId == "string" ? stored.bodyId : void 0;
    if (bodyId === void 0)
      throw new Error(`Received email ${id} has no body identifier`);
    let bodies = this.sql.exec(
      STATEMENTS.findReceivedBodies,
      bodyId
    ).toArray(), rawBase64 = bodies.find(({ part }) => part === 0)?.raw_base64;
    if (rawBase64 === void 0)
      throw new Error(`Received email ${id} has no captured body`);
    return materialiseReceivedEmail(
      zStoredRoutingEmailMetadata.parse(stored),
      rawBase64,
      new Map(
        bodies.filter(({ part }) => part > 0).map(({ part, raw_base64 }) => [part - 1, raw_base64])
      )
    );
  }
  listReceived(cursor, limit, worker) {
    return this.#list(
      "received",
      (data) => zStoredRoutingEmailSummary.parse(JSON.parse(data)),
      cursor,
      limit,
      worker
    );
  }
  storeSent(email) {
    this.#insert(
      "sent",
      messageIdToStorageId(email.messageId),
      email.sentAt,
      email
    );
  }
  findSent(id, worker) {
    let email = this.#find("sent", id, worker);
    return email === void 0 ? void 0 : materialiseSentEmail(email);
  }
  listSent(cursor, limit, worker) {
    return this.#list(
      "sent",
      (data) => getSentSummary(zEmailSendingDetail.parse(JSON.parse(data))),
      cursor,
      limit,
      worker
    );
  }
  clear() {
    this.ctx.storage.transactionSync(() => {
      this.sql.exec(STATEMENTS.clearReceivedBodies), this.sql.exec(STATEMENTS.clear);
    });
  }
};

// src/workers/email/email-store.worker.ts
var EmailStoreHost = class extends WorkerEntrypoint {
  #store() {
    return this.env.EMAIL_STORE_DO.get(
      this.env.EMAIL_STORE_DO.idFromName("singleton")
    );
  }
  async getSourceId() {
    return await this.#store().getSourceId();
  }
  async storeReceivedBody(captureId, part, rawBase64) {
    await this.#store().storeReceivedBody(captureId, part, rawBase64);
  }
  async storeReceivedMetadata(captureId, expectedBodyParts, email) {
    await this.#store().storeReceivedMetadata(
      captureId,
      expectedBodyParts,
      email
    );
  }
  async discardReceived(captureId) {
    await this.#store().discardReceived(captureId);
  }
  async findReceived(id, worker) {
    let email = await this.#store().findReceived(id, worker);
    return email === void 0 ? void 0 : zStoredRoutingEmail.parse(email);
  }
  async listReceived(cursor, limit, worker) {
    let page = await this.#store().listReceived(cursor, limit, worker);
    return {
      ...page,
      items: zStoredRoutingEmailSummary.array().parse(page.items)
    };
  }
  async storeSent(email) {
    await this.#store().storeSent(email);
  }
  async findSent(id, worker) {
    return await this.#store().findSent(id, worker);
  }
  async listSent(cursor, limit, worker) {
    return await this.#store().listSent(cursor, limit, worker);
  }
  async clear() {
    await this.#store().clear();
  }
};
export {
  EmailStore,
  EmailStoreHost as default
};
//# sourceMappingURL=email-store.worker.js.map
