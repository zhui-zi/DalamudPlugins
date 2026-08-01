# Toolbox Unlock Worker

Cloudflare Worker that validates Keita Toolbox unlock requests.

The deployed Worker name remains unchanged to preserve the existing endpoint.

## Configuration

Set `TOOLBOX_PASSWORD_SHA256` as an encrypted Worker secret. Its value is the
lowercase SHA-256 digest of the Keita Toolbox unlock password. The password and
digest must never be committed.

## Commands

```text
npm install
npm run check
npm run deploy
```
