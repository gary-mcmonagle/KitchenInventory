/** Build-time config (VITE_*), from deploy/entra.sh's outputs. Not secrets. */
export interface AppConfig {
    tenantId: string;
    spaClientId: string;
    apiScope: string;
    apiBaseUrl: string;
    buildSha: string;
}

type Env = Record<string, string | undefined>;

export function readConfig(env: Env = import.meta.env as unknown as Env): AppConfig {
    const required = ["VITE_TENANT_ID", "VITE_SPA_CLIENT_ID", "VITE_API_SCOPE", "VITE_API_BASE_URL"] as const;
    const missing = required.filter((name) => !env[name]?.trim());
    if (missing.length > 0) {
        throw new Error(`Missing build settings: ${missing.join(", ")} (see FE/.env.example)`);
    }
    return {
        tenantId: env.VITE_TENANT_ID!.trim(),
        spaClientId: env.VITE_SPA_CLIENT_ID!.trim(),
        apiScope: env.VITE_API_SCOPE!.trim(),
        apiBaseUrl: env.VITE_API_BASE_URL!.trim().replace(/\/+$/, ""),
        buildSha: env.VITE_BUILD_SHA?.trim() || "local",
    };
}
