type RuntimeEnv = Env & {
  HASH_KEY: string;
  OWNER_EMAIL: string;
};

type UsageBody = {
  installId: string;
  version: string;
};

type SummaryRow = {
  total: number;
  active1: number;
  active7: number;
  active30: number;
  new1: number;
  new7: number;
  new30: number;
};

type DailyRow = {
  day: string;
  count: number;
};

type VersionRow = {
  version: string;
  count: number;
};

const signalHost = "pluginping.keita.cc";
const dashboardHost = "pluginstats.keita.cc";
const installIdPattern = /^[a-f0-9]{32}$/i;
const versionPattern = /^\d{1,4}\.\d{1,4}\.\d{1,4}$/;

const textEncoder = new TextEncoder();

export default {
  async fetch(request, env): Promise<Response> {
    const url = new URL(request.url);

    try {
      if (url.hostname === signalHost)
        return await handleSignal(request, url, env);
      if (url.hostname === dashboardHost)
        return await handleDashboard(request, url, env);
      return response("Not found", 404);
    } catch {
      return response("Service unavailable", 500);
    }
  },
} satisfies ExportedHandler<RuntimeEnv>;

async function handleSignal(
  request: Request,
  url: URL,
  env: RuntimeEnv,
): Promise<Response> {
  if (request.method !== "POST" || url.pathname !== "/v1/heartbeat")
    return response("Not found", 404);

  const contentLength = Number(request.headers.get("content-length") ?? "0");
  if (!Number.isFinite(contentLength) || contentLength > 512)
    return response("Payload too large", 413);

  const raw = await request.text();
  if (textEncoder.encode(raw).byteLength > 512)
    return response("Payload too large", 413);

  let body: UsageBody;
  try {
    body = JSON.parse(raw) as UsageBody;
  } catch {
    return response("Invalid payload", 400);
  }

  const installId = body.installId?.toLowerCase();
  const version = body.version;
  if (!installIdPattern.test(installId) || !versionPattern.test(version))
    return response("Invalid payload", 400);
  if (!env.HASH_KEY)
    return response("Service unavailable", 503);

  const installHash = await createInstallHash(installId, env.HASH_KEY);
  const now = Math.floor(Date.now() / 1000);
  const day = new Date(now * 1000).toISOString().slice(0, 10);

  await env.DB.batch([
    env.DB.prepare(
      "INSERT INTO installations (install_hash, first_seen, last_seen, last_day, last_version, active_days) VALUES (?, ?, ?, ?, ?, 1) ON CONFLICT(install_hash) DO UPDATE SET last_seen = excluded.last_seen, last_day = excluded.last_day, last_version = excluded.last_version, active_days = installations.active_days + CASE WHEN installations.last_day <> excluded.last_day THEN 1 ELSE 0 END",
    ).bind(installHash, now, now, day, version),
    env.DB.prepare(
      "INSERT INTO daily_heartbeats (day, install_hash, version, received_at) VALUES (?, ?, ?, ?) ON CONFLICT(day, install_hash) DO UPDATE SET version = excluded.version, received_at = excluded.received_at",
    ).bind(day, installHash, version, now),
  ]);

  return new Response(null, {
    status: 204,
    headers: secureHeaders(),
  });
}

async function handleDashboard(
  request: Request,
  url: URL,
  env: RuntimeEnv,
): Promise<Response> {
  if (request.method !== "GET" || url.pathname !== "/")
    return response("Not found", 404);

  const email = request.headers.get("cf-access-authenticated-user-email");
  if (!env.OWNER_EMAIL)
    return response("Service unavailable", 503);
  if (!email || email.toLowerCase() !== env.OWNER_EMAIL.toLowerCase())
    return response("Forbidden", 403);

  const now = Math.floor(Date.now() / 1000);
  const day1 = now - 86400;
  const day7 = now - 7 * 86400;
  const day30 = now - 30 * 86400;
  const firstChartDay = new Date((now - 29 * 86400) * 1000)
    .toISOString()
    .slice(0, 10);

  const [summaryResult, dailyResult, versionResult] = await env.DB.batch([
    env.DB.prepare(
      "SELECT COUNT(*) AS total, COALESCE(SUM(CASE WHEN last_seen >= ? THEN 1 ELSE 0 END), 0) AS active1, COALESCE(SUM(CASE WHEN last_seen >= ? THEN 1 ELSE 0 END), 0) AS active7, COALESCE(SUM(CASE WHEN last_seen >= ? THEN 1 ELSE 0 END), 0) AS active30, COALESCE(SUM(CASE WHEN first_seen >= ? THEN 1 ELSE 0 END), 0) AS new1, COALESCE(SUM(CASE WHEN first_seen >= ? THEN 1 ELSE 0 END), 0) AS new7, COALESCE(SUM(CASE WHEN first_seen >= ? THEN 1 ELSE 0 END), 0) AS new30 FROM installations",
    ).bind(day1, day7, day30, day1, day7, day30),
    env.DB.prepare(
      "SELECT day, COUNT(*) AS count FROM daily_heartbeats WHERE day >= ? GROUP BY day ORDER BY day",
    ).bind(firstChartDay),
    env.DB.prepare(
      "SELECT last_version AS version, COUNT(*) AS count FROM installations WHERE last_seen >= ? GROUP BY last_version ORDER BY count DESC, last_version DESC LIMIT 12",
    ).bind(day30),
  ]);

  const summary = (summaryResult.results[0] ?? emptySummary()) as SummaryRow;
  const daily = dailyResult.results as unknown as DailyRow[];
  const versions = versionResult.results as unknown as VersionRow[];
  const html = renderDashboard(summary, fillDailyRows(daily, now), versions, now);

  return new Response(html, {
    status: 200,
    headers: {
      ...secureHeaders(),
      "content-type": "text/html; charset=utf-8",
      "content-security-policy": "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
    },
  });
}

async function createInstallHash(installId: string, key: string): Promise<string> {
  const cryptoKey = await crypto.subtle.importKey(
    "raw",
    textEncoder.encode(key),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
  const signature = await crypto.subtle.sign(
    "HMAC",
    cryptoKey,
    textEncoder.encode(installId),
  );
  return Array.from(new Uint8Array(signature), byte => byte.toString(16).padStart(2, "0")).join("");
}

function fillDailyRows(rows: DailyRow[], now: number): DailyRow[] {
  const byDay = new Map(rows.map(row => [row.day, Number(row.count)]));
  const result: DailyRow[] = [];
  for (let offset = 29; offset >= 0; offset -= 1) {
    const day = new Date((now - offset * 86400) * 1000).toISOString().slice(0, 10);
    result.push({ day, count: byDay.get(day) ?? 0 });
  }
  return result;
}

function renderDashboard(
  summary: SummaryRow,
  daily: DailyRow[],
  versions: VersionRow[],
  now: number,
): string {
  const maximum = Math.max(1, ...daily.map(row => Number(row.count)));
  const bars = daily.map(row => {
    const height = Math.max(2, Math.round((Number(row.count) / maximum) * 100));
    return `<div class="bar-wrap" title="${row.day}: ${Number(row.count)}"><div class="bar" style="height:${height}%"></div><span>${row.day.slice(5)}</span></div>`;
  }).join("");
  const versionRows = versions.length > 0
    ? versions.map(row => `<tr><td>${escapeHtml(row.version)}</td><td>${Number(row.count).toLocaleString("zh-CN")}</td></tr>`).join("")
    : "<tr><td>暂无数据</td><td>0</td></tr>";
  const updated = new Date(now * 1000).toISOString().replace("T", " ").slice(0, 19) + " UTC";

  return `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="refresh" content="300"><title>Keita 工具箱使用监测</title><style>:root{color-scheme:dark;font-family:Inter,ui-sans-serif,system-ui,sans-serif;background:#090b10;color:#eef2ff}*{box-sizing:border-box}body{margin:0;min-height:100vh;background:radial-gradient(circle at top,#182035 0,#090b10 48%)}main{width:min(1120px,calc(100% - 32px));margin:0 auto;padding:48px 0 64px}header{display:flex;align-items:end;justify-content:space-between;gap:16px;margin-bottom:28px}h1{font-size:clamp(26px,4vw,40px);margin:0;letter-spacing:-.04em}p{color:#8d98b5;margin:8px 0 0}.updated{font-size:13px;white-space:nowrap}.cards{display:grid;grid-template-columns:repeat(4,1fr);gap:14px}.card,.panel{background:rgba(18,23,35,.82);border:1px solid #252d43;border-radius:18px;box-shadow:0 16px 50px rgba(0,0,0,.18)}.card{padding:20px}.label{font-size:13px;color:#8d98b5}.value{display:block;font-size:32px;font-weight:750;margin-top:9px}.delta{font-size:12px;color:#70dfbd;margin-top:5px}.panel{margin-top:16px;padding:22px}h2{font-size:16px;margin:0 0 18px}.chart{display:flex;height:250px;align-items:stretch;gap:4px;border-bottom:1px solid #303951;padding-top:10px}.bar-wrap{height:100%;flex:1;display:flex;align-items:center;justify-content:end;flex-direction:column;min-width:0}.bar{width:100%;max-width:22px;background:linear-gradient(180deg,#8a7dff,#4bd6b8);border-radius:5px 5px 1px 1px;min-height:2px}.bar-wrap span{color:#69748d;font-size:9px;writing-mode:vertical-rl;margin-top:5px;height:36px}.grid{display:grid;grid-template-columns:2fr 1fr;gap:16px}table{width:100%;border-collapse:collapse}td{padding:11px 0;border-top:1px solid #252d43}td:last-child{text-align:right;font-variant-numeric:tabular-nums;color:#a7f3d0}@media(max-width:780px){main{padding-top:28px}.cards{grid-template-columns:repeat(2,1fr)}.grid{grid-template-columns:1fr}header{align-items:start;flex-direction:column}.chart{height:210px}}@media(max-width:440px){.cards{grid-template-columns:1fr}.panel{padding:16px}.bar-wrap span{display:none}}</style></head><body><main><header><div><h1>Keita 工具箱</h1><p>匿名每日活跃情况</p></div><p class="updated">更新时间：${updated}</p></header><section class="cards"><article class="card"><span class="label">已记录安装</span><strong class="value">${Number(summary.total).toLocaleString("zh-CN")}</strong><div class="delta">近 30 天新增 ${Number(summary.new30).toLocaleString("zh-CN")}</div></article><article class="card"><span class="label">24 小时活跃</span><strong class="value">${Number(summary.active1).toLocaleString("zh-CN")}</strong><div class="delta">新增 ${Number(summary.new1).toLocaleString("zh-CN")}</div></article><article class="card"><span class="label">7 天活跃</span><strong class="value">${Number(summary.active7).toLocaleString("zh-CN")}</strong><div class="delta">新增 ${Number(summary.new7).toLocaleString("zh-CN")}</div></article><article class="card"><span class="label">30 天活跃</span><strong class="value">${Number(summary.active30).toLocaleString("zh-CN")}</strong><div class="delta">新增 ${Number(summary.new30).toLocaleString("zh-CN")}</div></article></section><section class="grid"><article class="panel"><h2>每日活跃安装数</h2><div class="chart">${bars}</div></article><article class="panel"><h2>近 30 天活跃版本</h2><table><tbody>${versionRows}</tbody></table></article></section></main></body></html>`;
}

function emptySummary(): SummaryRow {
  return {
    total: 0,
    active1: 0,
    active7: 0,
    active30: 0,
    new1: 0,
    new7: 0,
    new30: 0,
  };
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, character => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#39;",
  })[character] ?? character);
}

function secureHeaders(): Record<string, string> {
  return {
    "cache-control": "no-store",
    "referrer-policy": "no-referrer",
    "x-content-type-options": "nosniff",
    "x-frame-options": "DENY",
  };
}

function response(body: string, status: number): Response {
  return new Response(body, {
    status,
    headers: {
      ...secureHeaders(),
      "content-type": "text/plain; charset=utf-8",
    },
  });
}
