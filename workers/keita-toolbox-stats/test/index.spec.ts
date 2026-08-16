import { env, SELF } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";

const signalUrl = "https://pluginping.keita.cc/v1/heartbeat";
const dashboardUrl = "https://pluginstats.keita.cc/";
const installId = "0123456789abcdef0123456789abcdef";

describe("worker", () => {
  beforeEach(async () => {
    await env.DB.batch([
      env.DB.prepare("DELETE FROM daily_heartbeats"),
      env.DB.prepare("DELETE FROM installations"),
    ]);
  });

  it("stores only a keyed installation hash", async () => {
    const result = await sendSignal();
    expect(result.status).toBe(204);

    const row = await env.DB.prepare(
      "SELECT install_hash, last_version, active_days FROM installations",
    ).first<{ install_hash: string; last_version: string; active_days: number }>();
    expect(row?.install_hash).toMatch(/^[a-f0-9]{64}$/);
    expect(row?.install_hash).not.toContain(installId);
    expect(row?.last_version).toBe("1.5.27");
    expect(row?.active_days).toBe(1);
  });

  it("deduplicates repeat signals for a day", async () => {
    await sendSignal();
    await sendSignal();

    const daily = await env.DB.prepare("SELECT COUNT(*) AS count FROM daily_heartbeats")
      .first<{ count: number }>();
    const install = await env.DB.prepare("SELECT active_days FROM installations")
      .first<{ active_days: number }>();
    expect(daily?.count).toBe(1);
    expect(install?.active_days).toBe(1);
  });

  it("rejects malformed and oversized payloads", async () => {
    const malformed = await SELF.fetch(signalUrl, {
      method: "POST",
      body: "{}",
    });
    const oversized = await SELF.fetch(signalUrl, {
      method: "POST",
      body: "x".repeat(513),
    });
    expect(malformed.status).toBe(400);
    expect(oversized.status).toBe(413);
  });

  it("requires the configured Access identity", async () => {
    await sendSignal();
    const denied = await SELF.fetch(dashboardUrl);
    const allowed = await SELF.fetch(dashboardUrl, {
      headers: {
        "cf-access-authenticated-user-email": "owner@example.com",
      },
    });
    expect(denied.status).toBe(403);
    expect(allowed.status).toBe(200);
    const html = await allowed.text();
    expect(html).toContain('lang="zh-CN"');
    expect(html).toContain("已记录安装");
    expect(html).toContain("每日活跃安装数");
    expect(html).not.toContain("Known installs");
  });

  it("isolates the public and private hosts", async () => {
    const publicDashboard = await SELF.fetch("https://pluginping.keita.cc/");
    const privateSignal = await SELF.fetch("https://pluginstats.keita.cc/v1/heartbeat", {
      method: "POST",
    });
    expect(publicDashboard.status).toBe(404);
    expect(privateSignal.status).toBe(404);
  });
});

function sendSignal(): Promise<Response> {
  return SELF.fetch(signalUrl, {
    method: "POST",
    headers: {
      "content-type": "application/json",
    },
    body: JSON.stringify({
      installId,
      version: "1.5.27",
    }),
  });
}
