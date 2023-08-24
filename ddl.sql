SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

SET default_tablespace = '';

SET default_table_access_method = heap;

CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

CREATE TABLE "Pessoas" (
    "Id" uuid NOT NULL,
    "Apelido" character varying(32) NOT NULL,
    "Nome" character varying(100) NOT NULL,
    "Nascimento" date NOT NULL,
    "Stack" text[],
    CONSTRAINT "PK_Pessoas" PRIMARY KEY ("Id")
);

CREATE UNIQUE INDEX "IX_Pessoas_Apelido" ON "Pessoas" ("Apelido");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20230823002823_First', '8.0.0-preview.7.23375.4');

COMMIT;

