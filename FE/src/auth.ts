import { PublicClientApplication, type Configuration } from "@azure/msal-browser";
import type { AppConfig } from "./config";

/** MSAL for the SPA registration (spike #35): auth code + PKCE, redirect flow, single tenant, localStorage cache. */
export function createMsal(config: AppConfig): PublicClientApplication {
    const msal: Configuration = {
        auth: {
            clientId: config.spaClientId,
            authority: `https://login.microsoftonline.com/${config.tenantId}`,
            redirectUri: `${window.location.origin}/`,
            postLogoutRedirectUri: `${window.location.origin}/`,
        },
        cache: { cacheLocation: "localStorage" },
    };
    return new PublicClientApplication(msal);
}

/** Starts sign-in, returning to the current page afterwards. */
export function signIn(msal: PublicClientApplication, config: AppConfig): Promise<void> {
    return msal.loginRedirect({ scopes: [config.apiScope], redirectStartPage: window.location.href });
}
