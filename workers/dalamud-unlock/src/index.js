const UNLOCK_PATH = "/toolbox/unlock";
const MAX_REQUEST_BYTES = 1024;
const SHA256_PATTERN = /^[0-9a-f]{64}$/;

function jsonResponse(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: {
      "Cache-Control": "no-store",
      "Content-Type": "application/json; charset=utf-8",
      "X-Content-Type-Options": "nosniff"
    }
  });
}

async function sha256Hex(value) {
  const bytes = new TextEncoder().encode(value);
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return Array.from(new Uint8Array(digest), (byte) =>
    byte.toString(16).padStart(2, "0")
  ).join("");
}

function constantTimeEqual(left, right) {
  if (left.length !== right.length) {
    return false;
  }

  let difference = 0;
  for (let index = 0; index < left.length; index += 1) {
    difference |= left.charCodeAt(index) ^ right.charCodeAt(index);
  }
  return difference === 0;
}

async function readJsonBody(request) {
  const declaredLength = Number(request.headers.get("Content-Length") ?? "0");
  if (Number.isFinite(declaredLength) && declaredLength > MAX_REQUEST_BYTES) {
    return { error: "too-large" };
  }

  if (!request.body) {
    return { error: "invalid" };
  }

  const reader = request.body.getReader();
  const chunks = [];
  let totalLength = 0;

  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }

      totalLength += value.byteLength;
      if (totalLength > MAX_REQUEST_BYTES) {
        await reader.cancel();
        return { error: "too-large" };
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }

  const bodyBytes = new Uint8Array(totalLength);
  let offset = 0;
  for (const chunk of chunks) {
    bodyBytes.set(chunk, offset);
    offset += chunk.byteLength;
  }

  try {
    const text = new TextDecoder("utf-8", { fatal: true }).decode(bodyBytes);
    return { value: JSON.parse(text) };
  } catch {
    return { error: "invalid" };
  }
}

export async function verifyPassword(request, env) {
  const expected = env.TOOLBOX_PASSWORD_SHA256?.trim().toLowerCase();
  if (!expected || !SHA256_PATTERN.test(expected)) {
    return jsonResponse({ error: "Unlock service is unavailable." }, 503);
  }

  const bodyResult = await readJsonBody(request);
  if (bodyResult.error === "too-large") {
    return jsonResponse({ error: "Request is too large." }, 413);
  }
  if (bodyResult.error) {
    return jsonResponse({ error: "Invalid request." }, 400);
  }

  const password = bodyResult.value?.password;
  if (
    typeof password !== "string" ||
    password.length === 0 ||
    password.length > 128
  ) {
    return jsonResponse({ error: "Invalid request." }, 400);
  }

  const actual = await sha256Hex(password);
  return constantTimeEqual(actual, expected)
    ? new Response(null, {
        status: 204,
        headers: {
          "Cache-Control": "no-store",
          "X-Content-Type-Options": "nosniff"
        }
      })
    : jsonResponse({ error: "Invalid credentials." }, 401);
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method === "POST" && url.pathname === UNLOCK_PATH) {
      return verifyPassword(request, env);
    }

    if (request.method === "GET" && url.pathname === "/health") {
      return jsonResponse({ status: "ok", service: "dalamud-unlock" });
    }

    return jsonResponse({ error: "Not found." }, 404);
  }
};
