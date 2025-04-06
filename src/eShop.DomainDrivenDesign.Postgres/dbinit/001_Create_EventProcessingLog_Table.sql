CREATE SCHEMA IF NOT EXISTS "$Schema$";

CREATE TABLE "$Schema$"."EventProcessingLog"
(
    "EventId"            UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    "CorrelationId"      UUID        NOT NULL,
    "AggregateId"        INTEGER     NOT NULL,
    "AggregateType"      VARCHAR(50) NOT NULL,
    "EventType"          VARCHAR(50) NOT NULL,
    "EventData"          JSONB       NOT NULL,
    "OccurredAt"         TIMESTAMPTZ NOT NULL,
    "SuccessfulHandlers" TEXT[]      NOT NULL DEFAULT '{}',
    "ProcessedAt"        TIMESTAMPTZ NULL
);

CREATE INDEX IX_AggregateType_EventType_OccuredAt_ProcessedAt ON "$Schema$"."EventProcessingLog" ("AggregateType", "EventType", "OccurredAt" ASC, "ProcessedAt" NULLS FIRST)