# Rotation Solver Release Monitor

Cloudflare Worker that checks the upstream and localized plugin indexes every minute.
It sends a `rotation-solver-release` repository dispatch event when the versions differ.

## Configuration

Set `GITHUB_TOKEN` as an encrypted Worker secret. The token must be limited to this
repository and allow repository dispatch events.

Set `TOOLBOX_PASSWORD_SHA256` as an encrypted Worker secret. Its value is the
lowercase SHA-256 digest of the Keita Toolbox unlock password. The password and
digest must never be committed.

## Commands

```text
npm install
npm run check
npm run deploy
```
