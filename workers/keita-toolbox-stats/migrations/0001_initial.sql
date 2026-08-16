CREATE TABLE installations (
    install_hash TEXT PRIMARY KEY,
    first_seen INTEGER NOT NULL,
    last_seen INTEGER NOT NULL,
    last_day TEXT NOT NULL,
    last_version TEXT NOT NULL,
    active_days INTEGER NOT NULL DEFAULT 1
) WITHOUT ROWID;

CREATE TABLE daily_heartbeats (
    day TEXT NOT NULL,
    install_hash TEXT NOT NULL,
    version TEXT NOT NULL,
    received_at INTEGER NOT NULL,
    PRIMARY KEY (day, install_hash)
) WITHOUT ROWID;

CREATE INDEX idx_installations_last_seen ON installations(last_seen);
CREATE INDEX idx_installations_first_seen ON installations(first_seen);
CREATE INDEX idx_daily_heartbeats_day ON daily_heartbeats(day);
