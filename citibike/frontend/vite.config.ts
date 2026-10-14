import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Dev: the API runs on 8080 (dotnet run); the SPA is served by the API in production, so no host config.
// 127.0.0.1, never localhost: Node resolves localhost to ::1 while Kestrel binds IPv4 here.
export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy: { "/api": "http://127.0.0.1:8080" } },
});
