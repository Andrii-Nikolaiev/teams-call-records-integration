-- TeamsCallRecords: SQLite schema, schema version 1 (migration 20261021000000_InitialCreate)
-- Time: TEXT, ISO 8601 UTC, exactly 7 fractional digits and Z suffix: 2026-10-20T09:00:00.5000000Z

CREATE TABLE cdr (
   cdr_seq        INTEGER PRIMARY KEY AUTOINCREMENT,
   schema_version      INTEGER NOT NULL,
   event_type       TEXT NOT NULL,
   tenant_id       TEXT NOT NULL,
   call_id      TEXT NOT NULL,
   session_id      TEXT NOT NULL,
   source_version     INTEGER NOT NULL,
   call_type      TEXT NOT NULL CHECK (call_type IN ('oneToOne','group','unknown')),
   caller_type     TEXT NULL,
   caller_id     TEXT NULL,
   callee_type      TEXT NULL,
   callee_id      TEXT NULL,
   start_utc      TEXT NOT NULL,
   end_utc        TEXT NOT NULL,
   duration_s      INTEGER NOT NULL CHECK (duration_s >= 0),
   modalities      TEXT NOT NULL,
   status        TEXT NOT NULL CHECK (status IN ('completed','failed','unknown')),
   termination_reason TEXT NULL,
   segment_count       INTEGER NOT NULL CHECK (segment_count >= 0),
   aggregation_status TEXT NOT NULL CHECK (aggregation_status IN ('complete','incomplete')),
   source_hash       TEXT NOT NULL,
   extensions       TEXT NULL,
   created_utc      TEXT NOT NULL,
   updated_utc       TEXT NOT NULL,
   CONSTRAINT uq_cdr_key UNIQUE (tenant_id, call_id, session_id)
);
CREATE INDEX ix_cdr_start ON cdr (start_utc, cdr_seq);
CREATE INDEX ix_cdr_call ON cdr (tenant_id, call_id);

CREATE TABLE source_state (
  tenant_id      TEXT NOT NULL,
  call_id      TEXT NOT NULL,
  applied_version INTEGER NULL,
  rejected_version INTEGER NULL,
  session_set_hash TEXT NULL,
  state        TEXT NOT NULL CHECK (state IN ('pending','done','suspended','expired')),
  attempts       INTEGER NOT NULL DEFAULT 0,
  next_attempt_utc TEXT NULL,
  first_seen_utc TEXT NOT NULL,
  last_error    TEXT NULL,
  updated_utc    TEXT NOT NULL,
  PRIMARY KEY (tenant_id, call_id)
);
CREATE INDEX ix_source_state_due ON source_state (state, next_attempt_utc);

CREATE TABLE inbox (
   id         INTEGER PRIMARY KEY AUTOINCREMENT,
   element_hash     TEXT NOT NULL,
   kind         TEXT NOT NULL CHECK (kind IN ('callRecord','channelMessage','reconcile')),
   tenant_id      TEXT NOT NULL,
   call_id      TEXT NULL,
   raw          TEXT NOT NULL,
   state        TEXT NOT NULL CHECK (state IN ('new','processing','done','failed')),
   attempts       INTEGER NOT NULL DEFAULT 0,
   next_attempt_utc TEXT NOT NULL,
   locked_until_utc TEXT NULL,
   received_utc    TEXT NOT NULL,
   done_utc       TEXT NULL,
   last_error    TEXT NULL
);
CREATE UNIQUE INDEX uq_inbox_new_hash ON inbox (element_hash) WHERE state = 'new';
CREATE INDEX ix_inbox_hash ON inbox (element_hash);
CREATE INDEX ix_inbox_call ON inbox (tenant_id, call_id, state);
CREATE INDEX ix_inbox_due ON inbox (state, next_attempt_utc);

CREATE TABLE rejected (
   id      INTEGER PRIMARY KEY AUTOINCREMENT,
   tenant_id TEXT NOT NULL,
   call_id  TEXT NULL,
   session_id TEXT NULL,
   code      TEXT NOT NULL,
   message     TEXT NOT NULL,
   source_hash TEXT NULL,
   created_utc TEXT NOT NULL
);
CREATE INDEX ix_rejected_created ON rejected (created_utc);

CREATE TABLE audit_log (
  id    INTEGER PRIMARY KEY AUTOINCREMENT,
  ts    TEXT NOT NULL,
  tenant_id TEXT NOT NULL,
  call_id TEXT NOT NULL,
  kind    TEXT NOT NULL CHECK (kind IN ('version_discrepancy')),
  detail TEXT NOT NULL
);

CREATE TABLE outbox (
   id         INTEGER PRIMARY KEY AUTOINCREMENT,
   kind         TEXT NOT NULL CHECK (kind IN ('activity')),
   tenant_id      TEXT NOT NULL,
   call_id      TEXT NOT NULL,
   session_id     TEXT NOT NULL,
   recipient     TEXT NOT NULL,
   payload       TEXT NOT NULL,
   state        TEXT NOT NULL CHECK (state IN ('new','sending','sent','failed')),
   attempts       INTEGER NOT NULL DEFAULT 0,
   next_attempt_utc TEXT NOT NULL,
   locked_until_utc TEXT NULL,
   created_utc     TEXT NOT NULL,
   sent_utc      TEXT NULL,
   last_error    TEXT NULL,
   CONSTRAINT uq_outbox_key UNIQUE (kind, tenant_id, call_id, session_id, recipient)
);
CREATE INDEX ix_outbox_due ON outbox (state, next_attempt_utc);

CREATE TABLE subscription (
   id           TEXT PRIMARY KEY,
   kind          TEXT NOT NULL CHECK (kind IN ('callRecords','channelMessages')),
   resource        TEXT NOT NULL,
   notification_url TEXT NOT NULL,
   expires_utc      TEXT NOT NULL,
   client_state_hash TEXT NOT NULL,
   status         TEXT NOT NULL CHECK (status IN ('active','renewing','failed')),
   created_utc      TEXT NOT NULL,
   renewed_utc       TEXT NULL
);

CREATE TABLE error_log (
  id    INTEGER PRIMARY KEY AUTOINCREMENT,
  ts    TEXT NOT NULL,
  component TEXT NOT NULL,
  message TEXT NOT NULL,
  details TEXT NULL
);
CREATE INDEX ix_error_log_ts ON error_log (ts);
