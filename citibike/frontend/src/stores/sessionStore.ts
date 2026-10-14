import { create } from "zustand";

interface SessionState {
  adminToken: string;
  setAdminToken: (token: string) => void;
}

// Kept in memory only: a reload asks for the token again, nothing is written to localStorage.
export const useSessionStore = create<SessionState>()((set) => ({
  adminToken: "",
  setAdminToken: (adminToken) => set({ adminToken }),
}));
