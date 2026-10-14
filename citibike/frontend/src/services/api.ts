import axios from "axios";
import { useSessionStore } from "../stores/sessionStore";

// The ONLY axios instance in the app. Same-origin: the API serves this SPA, so no host and no CORS.
export const api = axios.create({
  baseURL: "/api",
  timeout: 30_000,
  headers: { "Content-Type": "application/json" },
});

api.interceptors.request.use((config) => {
  // Starting a run needs the shared admin token (there is no platform identity in front of this API).
  const token = useSessionStore.getState().adminToken;
  if (token) config.headers["X-Admin-Token"] = token;
  return config;
});

api.interceptors.response.use(
  (res) => res,
  (err) => {
    const body = err.response?.data; // backend returns a human-readable text body
    const msg =
      (typeof body === "string" && body.trim()) ||
      `${err.response?.status ?? ""} ${err.response?.statusText ?? err.message}`.trim();
    const error = new Error(msg) as Error & { status?: number };
    error.status = err.response?.status;
    return Promise.reject(error);
  },
);
