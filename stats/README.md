# Proteus usage stats

A Cloudflare Worker plus a D1 database that receive the **opt-in** feature-usage counts Proteus
sends. Users are asked once; nothing is counted or sent unless they say yes. What is collected, and
why, is in [`../PRIVACY.md`](../PRIVACY.md). That file is a promise to users, so change it whenever
anything here changes what is stored.

Free tier: 100k requests/day and 100k rows written/day. Each opted-in install sends at most one
report a day, and each report is one row.

| Route | Does |
|---|---|
| `POST /v1/report` | Stores one install's counts for one day. The feature keys must be listed in [`src/features.js`](src/features.js). |
| `DELETE /v1/install/<id>` | Erases everything that install ever sent. The plugin's **Delete my data** button calls this. |
| cron, daily 03:17 UTC | Deletes reports older than 13 months. |

## Setup (once)

```sh
cd stats
npm install
npm test                                  # no network or account needed
npx wrangler login
npx wrangler d1 create proteus-stats --location weur   # prints a database_id
```

`--location weur` asks Cloudflare to keep the database in Western Europe, where most of the users
this law protects live. It is a placement hint, not a guarantee, so PRIVACY.md still discloses that
processing may happen outside the EEA.

1. Paste the printed `database_id` into [`wrangler.toml`](wrangler.toml).
2. Create the table, then deploy:

   ```sh
   npm run migrate                           # applies migrations/ to the remote database
   npm run deploy
   ```

The custom domain `stats.solona.info` is created by the deploy. Its zone must be on the same
Cloudflare account as `dl.solona.info`.

### Verify

```sh
curl -i -X POST https://stats.solona.info/v1/report -H "content-type: application/json" \
  -d '{"v":1,"id":"00000000-0000-4000-8000-000000000000","day":"'$(date -u +%F)'","version":"0.0.0.0","build":0,"lang":"en","counts":{}}'
# -> 204
curl -i -X DELETE https://stats.solona.info/v1/install/00000000-0000-4000-8000-000000000000
# -> 204
```

## Reading the data

[`queries.sql`](queries.sql) holds the saved queries:

- active installs;
- feature reach;
- weekly trend;
- version adoption;
- language split.

Run them all with:

```sh
npx wrangler d1 execute proteus-stats --remote --file queries.sql
```

## Adding a feature

1. Add the key to [`src/features.js`](src/features.js) and deploy the worker **first**.
2. Add the `UsageFeature` value and its wire key in `Proteus/Services/UsageStats.cs`.

`UsageStatsTests` fails if the two lists differ.

Deploy order matters. A plugin that sends a key this worker does not know gets a 400, and it keeps
retrying that day's counts until they age out.

## Privacy constraints this code keeps

- **Nothing about the request is stored.** The worker reads no headers, which means no IP address
  and no user agent.
- **Workers Logs are off** (`[observability] enabled = false`). `wrangler tail` streams live and
  stores nothing.
- **Unexpected fields are refused, not ignored.** A client can't start sending something that
  PRIVACY.md doesn't list.
