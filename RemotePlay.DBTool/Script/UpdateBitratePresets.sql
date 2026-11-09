START TRANSACTION;
UPDATE t_base_enum SET description = '自动（按分辨率匹配码率）'
WHERE id = 'quality-default';

UPDATE t_base_enum SET description = '10 Mbps · 高清'
WHERE id = 'quality-high';

UPDATE t_base_enum SET description = '4 Mbps · 流畅'
WHERE id = 'quality-low';

UPDATE t_base_enum SET description = '6 Mbps · 标准'
WHERE id = 'quality-medium';

UPDATE t_base_enum SET description = '20 Mbps · 高画质'
WHERE id = 'quality-ultra-20m';

UPDATE t_base_enum SET description = '30 Mbps · 超高画质'
WHERE id = 'quality-ultra-30m';

UPDATE t_base_enum SET description = '40 Mbps · 极高画质，需稳定的局域网'
WHERE id = 'quality-ultra-40m';

UPDATE t_base_enum SET description = '50 Mbps · 极致画质，建议有线网络'
WHERE id = 'quality-ultra-50m';

UPDATE t_base_enum SET description = '15 Mbps · 1080p 推荐'
WHERE id = 'quality-very-high';

UPDATE t_base_enum SET description = '2 Mbps · 省流量，适合移动网络'
WHERE id = 'quality-very-low';

INSERT INTO t_base_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('quality-ultra-100m', TIMESTAMPTZ '2026-11-09T00:00:00Z', '100 Mbps · 最高画质，需千兆有线网络', 'ULTRA_100M', 'ULTRA_100M', 'Quality', '100000', TRUE, 10, NULL);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261109131024_UpdateBitratePresets', '10.0.14');

COMMIT;

