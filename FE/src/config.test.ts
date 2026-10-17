import { readConfig } from "./config";

const env = {
    VITE_TENANT_ID: "tenant",
    VITE_SPA_CLIENT_ID: "spa",
    VITE_API_SCOPE: "api://tenant/cupboard-tracker-api/access_as_user",
    VITE_API_BASE_URL: "https://api.example.net/",
};

test("reads the build settings and trims the API URL", () => {
    expect(readConfig({ ...env, VITE_BUILD_SHA: "abc1234" })).toEqual({
        tenantId: "tenant",
        spaClientId: "spa",
        apiScope: "api://tenant/cupboard-tracker-api/access_as_user",
        apiBaseUrl: "https://api.example.net",
        buildSha: "abc1234",
    });
});

test("names every missing setting", () => {
    expect(() => readConfig({ VITE_TENANT_ID: "t" })).toThrow(
        "VITE_SPA_CLIENT_ID, VITE_API_SCOPE, VITE_API_BASE_URL",
    );
});

test("build SHA defaults to local", () => {
    expect(readConfig(env).buildSha).toBe("local");
});
