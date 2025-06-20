﻿CREATE SCHEMA IF NOT EXISTS "$Schema$";

CREATE TABLE "$Schema$".buyers
(
    id                 UUID PRIMARY KEY,
    name               VARCHAR(200) NULL
);

CREATE TABLE "$Schema$".card_types
(
    id                 INTEGER PRIMARY KEY,
    name               VARCHAR(200) NOT NULL
);

INSERT INTO "$Schema$".card_types VALUES (1, 'Amex'), (2, 'Visa'), (3, 'MasterCard');

CREATE TABLE "$Schema$".payment_methods
(
    id                 UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    buyer_id           UUID NOT NULL REFERENCES "$Schema$".buyers(id),
    card_type_id       INTEGER NOT NULL REFERENCES "$Schema$".card_types(id),
    card_number        VARCHAR(25) NOT NULL,
    card_holder_name   VARCHAR(200) NOT NULL,
    card_expiration    DATE NOT NULL
);

CREATE UNIQUE INDEX uix_payment_method ON "$Schema$"."payment_methods" (buyer_id, card_type_id, card_number, card_holder_name, card_expiration);

CREATE TABLE "$Schema$"."orders"
(
    "id"                 UUID PRIMARY KEY,
    "buyer_id"           UUID NOT NULL REFERENCES "$Schema$"."buyers"("id"),
    "payment_method_id"  UUID REFERENCES "$Schema$"."payment_methods"("id") ON DELETE RESTRICT,
    "status"             VARCHAR(50) NOT NULL,
    "description"        TEXT NULL,
    "started_at"         TIMESTAMPTZ NULL,
    "street"             VARCHAR(100) NULL,
    "city"               VARCHAR(100) NULL,
    "state"              VARCHAR(100) NULL,
    "country"            VARCHAR(100) NULL,
    "zip_code"           VARCHAR(100) NULL
);

CREATE TABLE "$Schema$"."order_items"
(
    "product_id"         INTEGER NOT NULL,
    "order_id"           INTEGER NOT NULL REFERENCES "$Schema$"."orders"("id"),
    "product_name"       TEXT NOT NULL,
    "unit_price"         NUMERIC NOT NULL,
    "units"              INTEGER NOT NULL,
    "discount"           NUMERIC NOT NULL DEFAULT 0,
    "picture_url"        TEXT NULL,
    PRIMARY KEY("product_id", "order_id")
);