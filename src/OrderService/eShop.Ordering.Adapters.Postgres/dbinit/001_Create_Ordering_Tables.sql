CREATE SCHEMA IF NOT EXISTS "$Schema$";

CREATE TABLE "$Schema$"."Buyers"
(
    "Id"                 UUID PRIMARY KEY,
    "Name"               VARCHAR(200) NOT NULL
);

CREATE TABLE "$Schema$"."CardTypes"
(
    "Id"                 INTEGER PRIMARY KEY,
    "Name"               VARCHAR(200) NOT NULL
);

INSERT INTO "$Schema$"."CardTypes" VALUES (1, 'Amex'), (2, 'Visa'), (3, 'MasterCard');

CREATE TABLE "$Schema$"."PaymentMethods"
(
    "Id"                 UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    "BuyerId"            UUID NOT NULL REFERENCES "$Schema$"."Buyers"("Id"),
    "CardTypeId"         INTEGER NOT NULL REFERENCES "$Schema$"."CardTypes"("Id"),
    "CardNumber"         VARCHAR(25) NOT NULL,
    "CardHolderName"     VARCHAR(200) NOT NULL,
    "Expiration"         DATE NOT NULL
);

CREATE UNIQUE INDEX UIX_PaymentMethod ON "$Schema$"."PaymentMethods" ("BuyerId", "CardTypeId", "CardNumber", "CardHolderName", "Expiration");

CREATE TABLE "$Schema$"."Orders"
(
    "Id"                 INTEGER PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    "BuyerId"            UUID NOT NULL REFERENCES "$Schema$"."Buyers"("Id"),
    "PaymentMethodId"    UUID REFERENCES "$Schema$"."PaymentMethods"("Id") ON DELETE RESTRICT,
    "Status"             VARCHAR(50) NOT NULL,
    "StartedAt"          TIMESTAMPTZ NULL,
    "Street"             VARCHAR(100) NULL,
    "City"               VARCHAR(100) NULL,
    "State"              VARCHAR(100) NULL,
    "Country"            VARCHAR(100) NULL,
    "ZipCode"            VARCHAR(100) NULL
);

CREATE TABLE "$Schema$"."OrderItems"
(
    "ProductId"          INTEGER NOT NULL,
    "OrderId"            INTEGER NOT NULL REFERENCES "$Schema$"."Orders"("Id"),
    "ProductName"        TEXT NOT NULL,
    "UnitPrice"          NUMERIC NOT NULL,
    "Units"              INTEGER NOT NULL,
    "Discount"           NUMERIC NOT NULL DEFAULT 0,
    "PictureUrl"         TEXT NULL,
    PRIMARY KEY("ProductId", "OrderId")
);