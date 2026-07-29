import assert from "node:assert/strict";
import test from "node:test";
import {
  checkAndDispatch,
  inspectVersions
} from "../src/index.js";

function pluginMaster(version) {
  return [
    {
      InternalName: "RotationSolver",
      AssemblyVersion: version
    }
  ];
}

function jsonResponse(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: {
      "Content-Type": "application/json"
    }
  });
}

test("reports matching versions without dispatching", async () => {
  let calls = 0;
  const fetchImpl = async () => {
    calls += 1;
    return jsonResponse(pluginMaster("7.5.5.4"));
  };

  const result = await checkAndDispatch(
    { GITHUB_TOKEN: "test-token" },
    fetchImpl
  );

  assert.deepEqual(result, {
    upstreamVersion: "7.5.5.4",
    downstreamVersion: "7.5.5.4",
    updateAvailable: false,
    dispatched: false
  });
  assert.equal(calls, 2);
});

test("dispatches a repository event when upstream is newer", async () => {
  const requests = [];
  const fetchImpl = async (url, options = {}) => {
    requests.push({ url, options });
    if (requests.length === 1) {
      return jsonResponse(pluginMaster("7.5.5.5"));
    }
    if (requests.length === 2) {
      return jsonResponse(pluginMaster("7.5.5.4"));
    }
    return new Response(null, { status: 204 });
  };

  const result = await checkAndDispatch(
    { GITHUB_TOKEN: "test-token" },
    fetchImpl
  );

  assert.equal(result.dispatched, true);
  assert.equal(requests.length, 3);
  assert.equal(
    requests[2].url,
    "https://api.github.com/repos/zhui-zi/DalamudPlugins/dispatches"
  );
  assert.equal(requests[2].options.method, "POST");
  assert.equal(requests[2].options.headers.Authorization, "Bearer test-token");
  assert.deepEqual(JSON.parse(requests[2].options.body), {
    event_type: "rotation-solver-release",
    client_payload: {
      version: "7.5.5.5"
    }
  });
});

test("does not dispatch without a secret", async () => {
  let calls = 0;
  const fetchImpl = async () => {
    calls += 1;
    return jsonResponse(pluginMaster(calls === 1 ? "7.5.5.5" : "7.5.5.4"));
  };

  await assert.rejects(
    checkAndDispatch({}, fetchImpl),
    /GITHUB_TOKEN is not configured/
  );
  assert.equal(calls, 2);
});

test("rejects an invalid plugin master", async () => {
  const fetchImpl = async () => jsonResponse([]);

  await assert.rejects(
    inspectVersions(fetchImpl),
    /RotationSolver version is missing/
  );
});

test("surfaces GitHub dispatch failures", async () => {
  let calls = 0;
  const fetchImpl = async () => {
    calls += 1;
    if (calls === 1) {
      return jsonResponse(pluginMaster("7.5.5.5"));
    }
    if (calls === 2) {
      return jsonResponse(pluginMaster("7.5.5.4"));
    }
    return new Response("Forbidden", { status: 403 });
  };

  await assert.rejects(
    checkAndDispatch({ GITHUB_TOKEN: "test-token" }, fetchImpl),
    /GitHub dispatch failed with HTTP 403/
  );
});
