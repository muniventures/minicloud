import react from "@vitejs/plugin-react";
import path from "node:path";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./app")
    }
  },
  server: {
    port: 3200
  },
  test: {
    environment: "jsdom",
    setupFiles: "./app/test/setup.ts"
  }
});
