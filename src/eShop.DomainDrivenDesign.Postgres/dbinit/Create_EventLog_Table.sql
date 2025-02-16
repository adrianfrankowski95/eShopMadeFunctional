CREATE SCHEMA IF NOT EXISTS "eShop";

CREATE TABLE "eShop"."EventLog"
(
    "Id"                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    "AggregateType"           VARCHAR(50) NOT NULL,
    "Data"                    JSONB       NOT NULL,
    "OccurredAt"              TIMESTAMPTZ NOT NULL,
    "SuccessfulEventHandlers" TEXT[] NULL,
    "ProcessedAt"             TIMESTAMPTZ NULL
);
