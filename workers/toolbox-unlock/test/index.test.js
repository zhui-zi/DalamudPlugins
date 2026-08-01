import assert from "node:assert/strict";
import test from "node:test";
import worker, { verifyToolboxPassword } from "../src/index.js";

test("unlocks toolbox with a matching server-side digest", async () => {
  const password = "test-password";
  const digest = Array.from(
    new Uint8Array(
      await crypto.subtle.digest("SHA-256", new TextEncoder().encode(password))
    ),
    (byte) => byte.toString(16).padStart(2, "0")
  ).join("");
  const request = new Request("https://example.com/toolbox/unlock", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ password })
  });

  const response = await verifyToolboxPassword(request, {
    TOOLBOX_PASSWORD_SHA256: digest
  });

  assert.equal(response.status, 204);
});

test("rejects an incorrect toolbox password", async () => {
  const request = new Request("https://example.com/toolbox/unlock", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ password: "wrong" })
  });

  const response = await verifyToolboxPassword(request, {
    TOOLBOX_PASSWORD_SHA256: "0".repeat(64)
  });

  assert.equal(response.status, 401);
});

test("keeps toolbox unlock unavailable without its secret", async () => {
  const request = new Request("https://example.com/toolbox/unlock", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ password: "anything" })
  });

  const response = await verifyToolboxPassword(request, {});

  assert.equal(response.status, 503);
});

test("reports a healthy unlock service", async () => {
  const response = await worker.fetch(
    new Request("https://example.com/health"),
    {}
  );

  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), {
    status: "ok",
    service: "toolbox-unlock"
  });
});
