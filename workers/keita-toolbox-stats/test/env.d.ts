import type { D1Migration } from "@cloudflare/vitest-pool-workers";

declare global {
  namespace Cloudflare {
    interface Env {
      HASH_KEY: string;
      OWNER_EMAIL: string;
      TEST_MIGRATIONS: D1Migration[];
    }
  }
}

export {};
