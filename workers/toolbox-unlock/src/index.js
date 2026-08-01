const TOOLBOX_UNLOCK_PATH = "/toolbox/unlock";

function jsonResponse(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: {
      "Cache-Control": "no-store",
      "Content-Type": "application/json; charset=utf-8"
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

export async function verifyToolboxPassword(request, env) {
  const expected = env.TOOLBOX_PASSWORD_SHA256?.trim().toLowerCase();
  if (!expected) {
    return jsonResponse({ error: "Unlock service is unavailable." }, 503);
  }

  const contentLength = Number(request.headers.get("Content-Length") ?? "0");
  if (contentLength > 1024) {
    return jsonResponse({ error: "Request is too large." }, 413);
  }

  let body;
  try {
    body = await request.json();
  } catch {
    return jsonResponse({ error: "Invalid request." }, 400);
  }

  if (
    typeof body?.password !== "string" ||
    body.password.length === 0 ||
    body.password.length > 128
  ) {
    return jsonResponse({ error: "Invalid request." }, 400);
  }

  const actual = await sha256Hex(body.password);
  return constantTimeEqual(actual, expected)
    ? new Response(null, {
        status: 204,
        headers: { "Cache-Control": "no-store" }
      })
    : jsonResponse({ error: "Invalid credentials." }, 401);
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === "POST" && url.pathname === TOOLBOX_UNLOCK_PATH) {
      return verifyToolboxPassword(request, env);
    }

    if (request.method === "GET" && url.pathname === "/health") {
      return jsonResponse({ status: "ok", service: "toolbox-unlock" });
    }

    return jsonResponse({ error: "Not found." }, 404);
  }
};
