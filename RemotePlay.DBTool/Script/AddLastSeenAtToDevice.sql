START TRANSACTION;
ALTER TABLE t_playstation_device ADD last_seen_at timestamp with time zone;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20251205123653_AddLastSeenAtToDevice', '9.0.10');

COMMIT;

