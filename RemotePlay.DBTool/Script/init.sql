CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;
CREATE TABLE t_enum (
    id character varying(50) NOT NULL,
    enum_type character varying(100) NOT NULL,
    enum_key character varying(100) NOT NULL,
    enum_value character varying(200) NOT NULL,
    enum_code character varying(50),
    sort_order integer NOT NULL,
    is_active boolean NOT NULL,
    description character varying(500),
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone,
    CONSTRAINT "PK_t_enum" PRIMARY KEY (id)
);

CREATE TABLE t_log (
    id character varying(50) NOT NULL,
    level character varying(20) NOT NULL,
    message text NOT NULL,
    exception text,
    source character varying(200),
    category character varying(100),
    user_id character varying(50),
    device_id character varying(50),
    ip_address character varying(50),
    request_path character varying(500),
    properties jsonb,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_t_log" PRIMARY KEY (id)
);

CREATE TABLE t_playstation_device (
    id text NOT NULL,
    uuid uuid NOT NULL,
    host_id text NOT NULL,
    host_name text NOT NULL,
    host_type text,
    mac_address text,
    ip_address text,
    system_version text,
    discover_protocol_version text,
    ap_bssid text,
    is_registered boolean,
    rp_key text,
    rp_key_type text,
    regist_key text,
    regist_data jsonb,
    notes text,
    last_play_date timestamp with time zone,
    status text,
    CONSTRAINT "PK_t_playstation_device" PRIMARY KEY (id)
);

CREATE TABLE t_settings (
    id character varying(50) NOT NULL,
    key character varying(100) NOT NULL,
    value text,
    value_json jsonb,
    category character varying(50),
    description character varying(500),
    is_encrypted boolean NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone,
    CONSTRAINT "PK_t_settings" PRIMARY KEY (id)
);

CREATE TABLE t_user (
    id character varying(50) NOT NULL,
    username character varying(50) NOT NULL,
    email character varying(100) NOT NULL,
    avatar_url character varying(500),
    password_hash character varying(256) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone,
    last_login_at timestamp with time zone,
    is_active boolean NOT NULL,
    CONSTRAINT "PK_t_user" PRIMARY KEY (id)
);

CREATE TABLE t_device_config (
    id character varying(50) NOT NULL,
    device_id character varying(50) NOT NULL,
    user_id text NOT NULL,
    config_key character varying(100) NOT NULL,
    config_value text,
    config_json jsonb,
    config_type character varying(50),
    description character varying(500),
    is_active boolean NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone,
    CONSTRAINT "PK_t_device_config" PRIMARY KEY (id),
    CONSTRAINT "FK_t_device_config_t_playstation_device_device_id" FOREIGN KEY (device_id) REFERENCES t_playstation_device (id) ON DELETE CASCADE
);

CREATE TABLE t_user_device (
    id character varying(50) NOT NULL,
    user_id character varying(50) NOT NULL,
    device_id character varying(50) NOT NULL,
    device_name character varying(200),
    device_type character varying(50),
    is_default boolean NOT NULL,
    is_active boolean NOT NULL,
    last_used_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone,
    CONSTRAINT "PK_t_user_device" PRIMARY KEY (id),
    CONSTRAINT "FK_t_user_device_t_playstation_device_device_id" FOREIGN KEY (device_id) REFERENCES t_playstation_device (id) ON DELETE CASCADE,
    CONSTRAINT "FK_t_user_device_t_user_user_id" FOREIGN KEY (user_id) REFERENCES t_user (id) ON DELETE CASCADE
);

INSERT INTO t_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('fps-high', TIMESTAMPTZ '2025-11-01T00:00:00Z', '60 帧每秒', '60FPS', 'HIGH', 'FPS', '60', TRUE, 2, NULL);
INSERT INTO t_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('fps-low', TIMESTAMPTZ '2025-11-01T00:00:00Z', '30 帧每秒', '30FPS', 'LOW', 'FPS', '30', TRUE, 1, NULL);
INSERT INTO t_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('quality-default', TIMESTAMPTZ '2025-11-01T00:00:00Z', '自动匹配比特率', 'DEFAULT', 'DEFAULT', 'Quality', '0', TRUE, 0, NULL);
INSERT INTO t_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('quality-high', TIMESTAMPTZ '2025-11-01T00:00:00Z', '高画质（约 10Mbps）', 'HIGH', 'HIGH', 'Quality', '10000', TRUE, 4, NULL);
INSERT INTO t_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('quality-low', TIMESTAMPTZ '2025-11-01T00:00:00Z', '低画质（约 4Mbps）', 'LOW', 'LOW', 'Quality', '4000', TRUE, 2, NULL);
INSERT INTO t_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('quality-medium', TIMESTAMPTZ '2025-11-01T00:00:00Z', '中等画质（约 6Mbps）', 'MEDIUM', 'MEDIUM', 'Quality', '6000', TRUE, 3, NULL);
INSERT INTO t_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('quality-very-high', TIMESTAMPTZ '2025-11-01T00:00:00Z', '非常高画质（约 15Mbps）', 'VERY_HIGH', 'VERY_HIGH', 'Quality', '15000', TRUE, 5, NULL);
INSERT INTO t_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('quality-very-low', TIMESTAMPTZ '2025-11-01T00:00:00Z', '非常低画质（约 2Mbps）', 'VERY_LOW', 'VERY_LOW', 'Quality', '2000', TRUE, 1, NULL);
INSERT INTO t_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('resolution-1080p', TIMESTAMPTZ '2025-11-01T00:00:00Z', '1080p 分辨率预设', 'RES_1080P', '1080p', 'ResolutionPreset', '{"width":1920,"height":1080,"bitrate":15000}', TRUE, 4, NULL);
INSERT INTO t_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('resolution-360p', TIMESTAMPTZ '2025-11-01T00:00:00Z', '360p 分辨率预设', 'RES_360P', '360p', 'ResolutionPreset', '{"width":640,"height":360,"bitrate":2000}', TRUE, 1, NULL);
INSERT INTO t_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('resolution-540p', TIMESTAMPTZ '2025-11-01T00:00:00Z', '540p 分辨率预设', 'RES_540P', '540p', 'ResolutionPreset', '{"width":960,"height":540,"bitrate":6000}', TRUE, 2, NULL);
INSERT INTO t_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('resolution-720p', TIMESTAMPTZ '2025-11-01T00:00:00Z', '720p 分辨率预设', 'RES_720P', '720p', 'ResolutionPreset', '{"width":1280,"height":720,"bitrate":10000}', TRUE, 3, NULL);
INSERT INTO t_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('streamtype-h264', TIMESTAMPTZ '2025-11-01T00:00:00Z', 'H.264 视频流', 'H264', 'H264', 'StreamType', '1', TRUE, 1, NULL);
INSERT INTO t_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('streamtype-hevc', TIMESTAMPTZ '2025-11-01T00:00:00Z', 'HEVC/H.265 视频流', 'HEVC', 'HEVC', 'StreamType', '2', TRUE, 2, NULL);
INSERT INTO t_enum (id, created_at, description, enum_code, enum_key, enum_type, enum_value, is_active, sort_order, updated_at)
VALUES ('streamtype-hevc-hdr', TIMESTAMPTZ '2025-11-01T00:00:00Z', 'HEVC/H.265 HDR 视频流', 'HEVC_HDR', 'HEVC_HDR', 'StreamType', '3', TRUE, 3, NULL);

CREATE UNIQUE INDEX "IX_t_device_config_device_id_config_key" ON t_device_config (device_id, config_key);

CREATE INDEX "IX_t_enum_enum_type" ON t_enum (enum_type);

CREATE UNIQUE INDEX "IX_t_enum_enum_type_enum_key" ON t_enum (enum_type, enum_key);

CREATE INDEX "IX_t_enum_sort_order" ON t_enum (sort_order);

CREATE INDEX "IX_t_log_created_at" ON t_log (created_at);

CREATE INDEX "IX_t_log_device_id" ON t_log (device_id);

CREATE INDEX "IX_t_log_level_created_at" ON t_log (level, created_at);

CREATE INDEX "IX_t_log_user_id" ON t_log (user_id);

CREATE UNIQUE INDEX "IX_t_settings_key" ON t_settings (key);

CREATE UNIQUE INDEX "IX_t_user_email" ON t_user (email);

CREATE UNIQUE INDEX "IX_t_user_username" ON t_user (username);

CREATE INDEX "IX_t_user_device_device_id" ON t_user_device (device_id);

CREATE UNIQUE INDEX "IX_t_user_device_user_id_device_id" ON t_user_device (user_id, device_id);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20251108080418_init', '9.0.10');

COMMIT;

