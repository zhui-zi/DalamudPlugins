# Dalamud Unlock Worker

Independent Cloudflare Worker for Keita Toolbox password validation.

Production endpoint:

```text
POST https://dalamudunlock.ff14.cafe/toolbox/unlock
```

## Configuration

Set `TOOLBOX_PASSWORD_SHA256` as an encrypted Worker secret. Its value is the
lowercase SHA-256 digest of the unlock password. Never commit the password or
digest.

The custom domain is declared in `wrangler.jsonc`. This Worker is independent
from the legacy toolbox unlock Worker and does not proxy to it.

## Commands

```text
npm install
npm run check
npm run deploy:dry
npm run deploy
```
