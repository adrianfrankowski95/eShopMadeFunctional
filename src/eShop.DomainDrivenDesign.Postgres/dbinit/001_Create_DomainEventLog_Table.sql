CREATE SCHEMA IF NOT EXISTS "$Schema$";

CREATE TABLE "$Schema$".domain_event_log
(
    event_id            UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    aggregate_id        UUID        NULL,
    aggregate_type      VARCHAR(50) NULL,
    event_type          TEXT        NOT NULL,
    event_data          JSONB       NOT NULL,
    occurred_at         TIMESTAMPTZ NOT NULL,
    successful_handlers TEXT[]      NOT NULL DEFAULT '{}',
    processed_at        TIMESTAMPTZ NULL
);

CREATE INDEX ix_aggregate_type_event_type_occured_at_processed_at ON "$Schema$".domain_event_log (aggregate_type, event_type, occurred_at ASC, processed_at NULLS FIRST)