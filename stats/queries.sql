-- Saved queries for the usage data. Run one file against the live database with:
--   npx wrangler d1 execute proteus-stats --remote --file queries.sql
-- or paste a single query into:  npx wrangler d1 execute proteus-stats --remote --command "..."
--
-- Every number here is over OPTED-IN installs only, which are a self-selected minority. Read them as
-- "of the people who share stats", not "of everyone".

-- Active installs: last 7 and last 30 days.
SELECT
  (SELECT COUNT(DISTINCT install_id) FROM reports WHERE day >= date('now', '-7 days'))  AS weekly_installs,
  (SELECT COUNT(DISTINCT install_id) FROM reports WHERE day >= date('now', '-30 days')) AS monthly_installs;

-- Feature reach over the last 30 days: how many active installs used each feature at least once,
-- as a share of all active installs, plus total uses and uses per installing user.
WITH active AS (
  SELECT COUNT(DISTINCT install_id) AS n FROM reports WHERE day >= date('now', '-30 days')
)
SELECT
  f.key                                                   AS feature,
  COUNT(DISTINCT r.install_id)                            AS installs,
  ROUND(100.0 * COUNT(DISTINCT r.install_id) / active.n, 1) AS pct_of_active,
  SUM(f.value)                                            AS total_uses,
  ROUND(1.0 * SUM(f.value) / COUNT(DISTINCT r.install_id), 1) AS uses_per_install
FROM reports r, json_each(r.counts) f, active
WHERE r.day >= date('now', '-30 days')
GROUP BY f.key
ORDER BY installs DESC;

-- Weekly trend per feature (installs using it), for spotting a feature taking off or dying.
SELECT strftime('%Y-W%W', r.day) AS week, f.key AS feature, COUNT(DISTINCT r.install_id) AS installs
FROM reports r, json_each(r.counts) f
WHERE r.day >= date('now', '-90 days')
GROUP BY week, feature
ORDER BY week DESC, installs DESC;

-- Version adoption: each install counted once, on the newest build it reported in the last 14 days.
SELECT version, COUNT(*) AS installs
FROM (
  SELECT install_id, version, MAX(build) AS build
  FROM reports WHERE day >= date('now', '-14 days')
  GROUP BY install_id
)
GROUP BY version
ORDER BY installs DESC;

-- UI language split over the last 30 days (which translations matter most).
SELECT lang, COUNT(DISTINCT install_id) AS installs
FROM reports WHERE day >= date('now', '-30 days')
GROUP BY lang
ORDER BY installs DESC;

-- Database size check against the 5 GB free tier.
SELECT COUNT(*) AS rows, MIN(received) AS oldest, MAX(received) AS newest FROM reports;
