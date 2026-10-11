import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    globals: true,
    environment: "node",
    include: [
      "tests/cortex/**/*.test.ts",
      "tests/unit/adapters/deepseek-harness-*.test.ts",
      "tests/unit/server/brain-data.test.ts",
      "tests/unit/viewer/brain-*.test.ts",
      "tests/unit/viewer/api-client.test.ts",
      "tests/unit/viewer/share.test.ts",
    ],
    testTimeout: 30000,
    hookTimeout: 30000,
    pool: "forks",
    poolOptions: {
      forks: {
        singleFork: true,
      },
    },
    coverage: {
      provider: "v8",
      reporter: ["text", "html", "json"],
      include: [
        "core/**/*.ts",
        "server/**/*.ts",
        "adapters/deepseek-harness/**/*.ts",
      ],
      exclude: ["**/*.test.ts", "**/*.d.ts", "**/index.ts"],
    },
  },
});
