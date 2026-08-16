import { cloudflareTest, readD1Migrations } from "@cloudflare/vitest-pool-workers";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [cloudflareTest(async () => ({
    main: "./src/index.ts",
    wrangler: {
      configPath: "./wrangler.jsonc",
    },
    miniflare: {
      bindings: {
        HASH_KEY: "test-hash-key",
        OWNER_EMAIL: "owner@example.com",
        TEST_MIGRATIONS: await readD1Migrations("./migrations"),
      },
    },
  }))],
  test: {
    setupFiles: ["./test/apply-migrations.ts"],
  },
});
