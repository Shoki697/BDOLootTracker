# BDO Loot Tracker - Community Parser Worker

This Worker replaces the old GitHub-Issue submission flow.

The desktop client uses:

- `POST /community/submit` - submit a validated Ground Loot calibration.
- `GET /community/latest` - fetch the latest Community parser manifest.
- `GET /community/profile` - fetch the exact parser profile bytes referenced by the manifest.
- `GET /health` - simple deployment check.

Only parser structure and validation metadata are persisted. The Worker code does **not** store account names, character names, loot history, packet captures, or IP addresses.

## Deploy

Prerequisites: Node.js and a Cloudflare account.

```bash
cd cloudflare-worker
npm install
npx wrangler login
npx wrangler kv namespace create COMMUNITY_PARSER
```

Copy the returned KV namespace ID into `wrangler.jsonc`:

```jsonc
"kv_namespaces": [
  {
    "binding": "COMMUNITY_PARSER",
    "id": "YOUR_REAL_NAMESPACE_ID"
  }
]
```

Then deploy:

```bash
npm run deploy
```

Wrangler prints a URL similar to:

```text
https://bdoloottracker-community-parser.<your-subdomain>.workers.dev
```

The production desktop client already contains the BDOLootTracker Worker URL, so end users do not need to configure anything in Settings. If you deploy this Worker under a different hostname for development, update `Services/CommunityParserEndpoint.cs` before building that dev client.

You can verify the Worker in a browser with:

```text
https://...workers.dev/health
```

## Candidate rules in this dev Worker

A submission is accepted only when the report is Ground-Loot-only, has at least 10 samples, has confidence >= 75%, contains 2-8 fingerprint checks, references the current GitHub official parser version, passes parser sanity checks, and is structurally different from the current official parser.

The Worker re-downloads the official GitHub manifest + profile and verifies its SHA-256 before accepting a submission. If the official parser changed after calibration, the submission is rejected and the client must compare again.

One accepted valid submission can publish a Community candidate. This is intentionally the requested fast-repair behavior; it is not the same trust level as an owner-reviewed official parser. Official GitHub parser updates always keep priority in the desktop client.

Workers KV is eventually consistent, so a newly accepted candidate can take a short time to appear from every location.
