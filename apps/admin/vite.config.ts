import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import { readFileSync } from "node:fs";
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      "/api": {
        target: process.env.ADMIN_API_ORIGIN || "http://localhost:5050",
        changeOrigin: true,
      },
    },
  },
  preview: { headers: JSON.parse(readFileSync(new URL("./security-headers.json", import.meta.url), "utf8")) },
  test: {
    environment: "jsdom",
    setupFiles: "./src/test-setup.ts",
    restoreMocks: true,
  },
});
