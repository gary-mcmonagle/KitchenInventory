import { InteractionRequiredAuthError, type IPublicClientApplication } from "@azure/msal-browser";
import { ApiClient, NoAccessError, SignInRequiredError } from "./api";
import type { AppConfig } from "./config";

const config: AppConfig = {
    tenantId: "t",
    spaClientId: "spa",
    apiScope: "api://t/cupboard-tracker-api/access_as_user",
    apiBaseUrl: "https://api.example.net",
    buildSha: "local",
};

function fakeMsal(overrides: Partial<Record<string, unknown>> = {}) {
    const account = { homeAccountId: "1", username: "gary" };
    return {
        getActiveAccount: vi.fn(() => account),
        getAllAccounts: vi.fn(() => [account]),
        acquireTokenSilent: vi.fn(async () => ({ accessToken: "token-123" })),
        acquireTokenRedirect: vi.fn(async () => undefined),
        ...overrides,
    } as unknown as IPublicClientApplication & Record<string, ReturnType<typeof vi.fn>>;
}

const json = (status: number, body: unknown) =>
    new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

test("attaches the bearer token and returns the caller", async () => {
    const fetcher = vi.fn(async () => json(200, { name: "Gary", objectId: "o" }));
    const api = new ApiClient(fakeMsal(), config, fetcher);

    await expect(api.me()).resolves.toEqual({ name: "Gary", objectId: "o" });
    expect(fetcher).toHaveBeenCalledWith("https://api.example.net/api/me", {
        headers: { Authorization: "Bearer token-123" },
    });
});

test("a 401 sends the user through sign-in again, back to where they were", async () => {
    const msal = fakeMsal();
    const api = new ApiClient(
        msal,
        config,
        vi.fn(async () => json(401, { error: "Invalid token" })),
    );

    await expect(api.me()).rejects.toBeInstanceOf(SignInRequiredError);
    expect(msal.acquireTokenRedirect).toHaveBeenCalledWith({
        scopes: [config.apiScope],
        redirectStartPage: window.location.href,
    });
});

test("a 403 is a no-access error, not a sign-in loop", async () => {
    const msal = fakeMsal();
    const api = new ApiClient(
        msal,
        config,
        vi.fn(async () => json(403, { error: "nope" })),
    );

    await expect(api.me()).rejects.toBeInstanceOf(NoAccessError);
    expect(msal.acquireTokenRedirect).not.toHaveBeenCalled();
});

test("when silent renewal can't work (about daily), the user signs in again", async () => {
    const msal = fakeMsal({
        acquireTokenSilent: vi.fn(async () => {
            throw new InteractionRequiredAuthError("interaction_required", "Sign in again");
        }),
    });
    const fetcher = vi.fn();
    const api = new ApiClient(msal, config, fetcher);

    await expect(api.me()).rejects.toBeInstanceOf(SignInRequiredError);
    expect(msal.acquireTokenRedirect).toHaveBeenCalled();
    expect(fetcher).not.toHaveBeenCalled();
});

test("ping is public: no token", async () => {
    const msal = fakeMsal();
    const fetcher = vi.fn(async () => json(200, { status: "ok", commit: "abc1234" }));

    await expect(new ApiClient(msal, config, fetcher).ping()).resolves.toEqual({
        status: "ok",
        commit: "abc1234",
    });
    expect(fetcher).toHaveBeenCalledWith("https://api.example.net/api/ping");
    expect(msal.acquireTokenSilent).not.toHaveBeenCalled();
});
