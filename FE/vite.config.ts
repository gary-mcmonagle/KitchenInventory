/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { serviceWorkerShell } from "./sw-shell.mjs";

export default defineConfig({
    plugins: [react(), serviceWorkerShell()],
    build: { target: "es2022" }, // main.tsx awaits MSAL start-up at the top level
    server: { port: 5173, strictPort: true }, // the registered SPA redirect URI is http://localhost:5173/
    test: {
        environment: "jsdom",
        globals: true,
        setupFiles: ["./src/test-setup.ts"],
        css: true, // tokens.test.ts reads tokens.css
    },
});
