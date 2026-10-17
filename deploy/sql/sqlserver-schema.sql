-- deploy/sql/sqlserver-schema.sql — SQL Server 2019/2022 equivalent (Database:Provider = SqlServer)
CREATE DATABASE eminova; GO
USE eminova; GO
CREATE LOGIN eminova_app WITH PASSWORD = '<SECRET>'; CREATE USER eminova_app FOR LOGIN eminova_app;
CREATE TABLE eminova_state ( TenantId nvarchar(64) NOT NULL, UserId nvarchar(256) NOT NULL, Json nvarchar(max) NOT NULL,
  Version int NOT NULL DEFAULT 0, UpdatedUtc datetime2 NOT NULL, CONSTRAINT PK_eminova_state PRIMARY KEY (TenantId, UserId));
CREATE TABLE eminova_audit ( Id bigint IDENTITY PRIMARY KEY, TenantId nvarchar(64) NOT NULL, UserId nvarchar(256) NOT NULL,
  [Action] nvarchar(64) NOT NULL, Detail nvarchar(1024) NOT NULL, AtUtc datetime2 NOT NULL);
CREATE INDEX IX_eminova_audit_TenantId_AtUtc ON eminova_audit (TenantId, AtUtc);
CREATE TABLE eminova_import ( Id bigint IDENTITY PRIMARY KEY, TenantId nvarchar(64) NOT NULL, UserId nvarchar(256) NOT NULL,
  Entity nvarchar(64) NOT NULL, FileName nvarchar(260) NOT NULL, RowsRead int NOT NULL, Added int NOT NULL, Updated int NOT NULL,
  Skipped int NOT NULL, Warnings nvarchar(4000) NOT NULL, AtUtc datetime2 NOT NULL);
CREATE INDEX IX_eminova_import_TenantId_AtUtc ON eminova_import (TenantId, AtUtc);
ALTER ROLE db_datareader ADD MEMBER eminova_app; ALTER ROLE db_datawriter ADD MEMBER eminova_app;
