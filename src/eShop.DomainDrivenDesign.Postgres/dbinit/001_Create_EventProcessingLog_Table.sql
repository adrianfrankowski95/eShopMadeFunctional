CREATE SCHEMA IF NOT EXISTS "$Schema$";

CREATE TABLE "$Schema$"."EventProcessingLog"
(
    "EventId"            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    "AggregateId"        INTEGER NOT NULL,
    "AggregateType"      VARCHAR(50) NOT NULL,
    "EventData"          JSONB       NOT NULL,
    "OccurredAt"         TIMESTAMPTZ NOT NULL,
    "SuccessfulHandlers" TEXT[] NOT NULL DEFAULT '{}',
    "ProcessedAt"        TIMESTAMPTZ NULL
);

CREATE INDEX IX_AggregateType_ProcessedAt ON "$Schema$"."EventProcessingLog" ("AggregateType", "ProcessedAt") NULLS FIRST ASC