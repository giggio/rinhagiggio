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

CREATE UNLOGGED TABLE "Pessoas" (
    "Id" uuid NOT NULL,
    "Apelido" character varying(32) NOT NULL,
    "Nome" character varying(100) NOT NULL,
    "Nascimento" date NOT NULL,
    "Stack" character varying(32)[],
    CONSTRAINT "PK_Pessoas" PRIMARY KEY ("Id")
);


