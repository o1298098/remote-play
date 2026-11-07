START TRANSACTION;
INSERT INTO t_base_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('quality-ultra-20m', TIMESTAMPTZ '2026-11-07T00:00:00Z', '超高画质（约 20Mbps）', 'ULTRA_20M', 'ULTRA_20M', 'Quality', '20000', TRUE, 6, NULL);
INSERT INTO t_base_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('quality-ultra-30m', TIMESTAMPTZ '2026-11-07T00:00:00Z', '超高画质（约 30Mbps）', 'ULTRA_30M', 'ULTRA_30M', 'Quality', '30000', TRUE, 7, NULL);
INSERT INTO t_base_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('quality-ultra-40m', TIMESTAMPTZ '2026-11-07T00:00:00Z', '极高画质（约 40Mbps，实验性）', 'ULTRA_40M', 'ULTRA_40M', 'Quality', '40000', TRUE, 8, NULL);
INSERT INTO t_base_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('quality-ultra-50m', TIMESTAMPTZ '2026-11-07T00:00:00Z', '极高画质（约 50Mbps，实验性）', 'ULTRA_50M', 'ULTRA_50M', 'Quality', '50000', TRUE, 9, NULL);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261107025544_AddHighBitratePresets', '9.0.10');

COMMIT;

