CREATE SCHEMA IF NOT EXISTS "eShop";

CREATE TABLE "eShop"."EventProcessingLog"
(
    "EventId"            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    "AggregateId"        BIGINT NOT NULL,
    "AggregateType"      VARCHAR(50) NOT NULL,
    "EventData"          JSONB       NOT NULL,
    "OccurredAt"         TIMESTAMPTZ NOT NULL,
    "SuccessfulHandlers" TEXT[] NOT NULL,
    "ProcessedAt"        TIMESTAMPTZ NULL
);
