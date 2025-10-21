CREATE SCHEMA IF NOT EXISTS "$Schema$";

CREATE TABLE "$Schema$".inbox_event_log
(
    event_id            UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    event_type          TEXT        NOT NULL,
    event_data          JSONB       NOT NULL,
    occurred_at         TIMESTAMPTZ NOT NULL,
    processed_at        TIMESTAMPTZ NULL
);

CREATE INDEX ix_occured_at_processed_at ON "$Schema$".domain_event_log (occurred_at ASC, processed_at NULLS FIRST)