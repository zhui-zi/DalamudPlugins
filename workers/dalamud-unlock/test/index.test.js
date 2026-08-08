import assert from "node:assert/strict";
import test from "node:test";
import worker, { verifyPassword } from "../src/index.js";

async function sha256Hex(value) {
  const digest = await crypto.subtle.digest(
    "SHA-256",
    new TextEncoder().encode(value)
  );
  return Array.from(new Uint8Array(digest), (byte) =>
    byte.toString(16).padStart(2, "0")
  ).join("");
}

function unlockRequest(password) {
  return new Request("https://dalamudunlock.ff14.cafe/toolbox/unlock", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ password })
  });
}

test("unlocks with a matching server-side digest", async () => {
  const password = "test-password";
  const response = await verifyPassword(unlockRequest(password), {
    TOOLBOX_PASSWORD_SHA256: await sha256Hex(password)
  });

  assert.equal(response.status, 204);
  assert.equal(response.headers.get("Cache-Control"), "no-store");
});

test("rejects an incorrect password", async () => {
  const response = await verifyPassword(unlockRequest("wrong"), {
    TOOLBOX_PASSWORD_SHA256: "0".repeat(64)
  });

  assert.equal(response.status, 401);
});

test("stays unavailable without a valid secret", async () => {
  const response = await verifyPassword(unlockRequest("anything"), {
    TOOLBOX_PASSWORD_SHA256: "invalid"
  });

  assert.equal(response.status, 503);
});

test("rejects malformed JSON", async () => {
  const response = await verifyPassword(
    new Request("https://dalamudunlock.ff14.cafe/toolbox/unlock", {
      method: "POST",
      body: "{"
    }),
    { TOOLBOX_PASSWORD_SHA256: "0".repeat(64) }
  );

  assert.equal(response.status, 400);
});

test("rejects oversized bodies without relying on Content-Length", async () => {
  const response = await verifyPassword(
    new Request("https://dalamudunlock.ff14.cafe/toolbox/unlock", {
      method: "POST",
      body: "x".repeat(1025)
    }),
    { TOOLBOX_PASSWORD_SHA256: "0".repeat(64) }
  );

  assert.equal(response.status, 413);
});

test("reports a healthy independent service", async () => {
  const response = await worker.fetch(
    new Request("https://dalamudunlock.ff14.cafe/health"),
    {}
  );

  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), {
    status: "ok",
    service: "dalamud-unlock"
  });
});

test("does not expose the unlock handler on other paths", async () => {
  const response = await worker.fetch(
    unlockRequest("anything").clone(),
    { TOOLBOX_PASSWORD_SHA256: "0".repeat(64) }
  );
  assert.equal(response.status, 401);

  const missing = await worker.fetch(
    new Request("https://dalamudunlock.ff14.cafe/unlock", {
      method: "POST",
      body: JSON.stringify({ password: "anything" })
    }),
    { TOOLBOX_PASSWORD_SHA256: "0".repeat(64) }
  );
  assert.equal(missing.status, 404);
});
