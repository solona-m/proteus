-- One row per report: one install's feature counts for one day.
-- No column for an IP address, user agent or anything else about the request; see src/index.js.
CREATE TABLE reports (
  id         INTEGER PRIMARY KEY,
  install_id TEXT    NOT NULL,   -- random GUID made at opt-in; not derived from anything
  day        TEXT    NOT NULL,   -- YYYY-MM-DD (UTC) the counts were collected on
  version    TEXT    NOT NULL,   -- plugin version
  build      INTEGER NOT NULL,   -- Plugin.BuildNumber
  lang       TEXT    NOT NULL,   -- Dalamud UI language code
  counts     TEXT    NOT NULL,   -- JSON object {feature: count}, keys from src/features.js
  received   TEXT    NOT NULL    -- server time, ISO 8601; drives retention
);

CREATE INDEX reports_install  ON reports (install_id);
CREATE INDEX reports_day      ON reports (day);
CREATE INDEX reports_received ON reports (received);
