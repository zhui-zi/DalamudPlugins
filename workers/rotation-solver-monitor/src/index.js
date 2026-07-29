const UPSTREAM_MASTER_URL =
  "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json";
const DOWNSTREAM_MASTER_URL =
  "https://raw.githubusercontent.com/zhui-zi/DalamudPlugins/main/pluginmaster.json";
const DISPATCH_URL =
  "https://api.github.com/repos/zhui-zi/DalamudPlugins/dispatches";
const INTERNAL_NAME = "RotationSolver";
const EVENT_TYPE = "rotation-solver-release";
const USER_AGENT = "zhui-zi/rotation-solver-release-monitor";

function findVersion(entries) {
  if (!Array.isArray(entries)) {
    throw new Error("Plugin master is not an array.");
  }

  const plugin = entries.find((entry) => entry?.InternalName === INTERNAL_NAME);
  const version = plugin?.AssemblyVersion;
  if (typeof version !== "string" || version.length === 0) {
    throw new Error(`${INTERNAL_NAME} version is missing.`);
  }

  return version;
}

async function fetchVersion(url, fetchImpl) {
  const response = await fetchImpl(url, {
    headers: {
      Accept: "application/json",
      "User-Agent": USER_AGENT
    },
    cf: {
      cacheTtl: 0,
      cacheEverything: false
    }
  });

  if (!response.ok) {
    throw new Error(`Version request failed with HTTP ${response.status}: ${url}`);
  }

  return findVersion(await response.json());
}

export async function inspectVersions(fetchImpl = fetch) {
  const [upstreamVersion, downstreamVersion] = await Promise.all([
    fetchVersion(UPSTREAM_MASTER_URL, fetchImpl),
    fetchVersion(DOWNSTREAM_MASTER_URL, fetchImpl)
  ]);

  return {
    upstreamVersion,
    downstreamVersion,
    updateAvailable: upstreamVersion !== downstreamVersion
  };
}

async function dispatchUpdate(version, token, fetchImpl) {
  if (typeof token !== "string" || token.length === 0) {
    throw new Error("GITHUB_TOKEN is not configured.");
  }

  const response = await fetchImpl(DISPATCH_URL, {
    method: "POST",
    headers: {
      Accept: "application/vnd.github+json",
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json",
      "User-Agent": USER_AGENT,
      "X-GitHub-Api-Version": "2026-03-10"
    },
    body: JSON.stringify({
      event_type: EVENT_TYPE,
      client_payload: {
        version
      }
    })
  });

  if (response.status !== 204) {
    const message = await response.text();
    throw new Error(
      `GitHub dispatch failed with HTTP ${response.status}: ${message.slice(0, 500)}`
    );
  }
}

export async function checkAndDispatch(env, fetchImpl = fetch) {
  const status = await inspectVersions(fetchImpl);
  if (!status.updateAvailable) {
    return {
      ...status,
      dispatched: false
    };
  }

  await dispatchUpdate(status.upstreamVersion, env.GITHUB_TOKEN, fetchImpl);
  return {
    ...status,
    dispatched: true
  };
}

async function runScheduled(env) {
  const result = await checkAndDispatch(env);
  console.log(JSON.stringify(result));
}

function jsonResponse(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: {
      "Cache-Control": "no-store",
      "Content-Type": "application/json; charset=utf-8"
    }
  });
}

export default {
  scheduled(_controller, env, ctx) {
    ctx.waitUntil(runScheduled(env));
  },

  async fetch(request) {
    const url = new URL(request.url);
    if (request.method !== "GET" || url.pathname !== "/health") {
      return jsonResponse({ error: "Not found." }, 404);
    }

    try {
      return jsonResponse(await inspectVersions());
    } catch (error) {
      return jsonResponse(
        {
          error: error instanceof Error ? error.message : String(error)
        },
        502
      );
    }
  }
};
