import { InteractionRequiredAuthError, type IPublicClientApplication } from "@azure/msal-browser";
import type { AppConfig } from "./config";

/** The API refused this user (403): signed in, but not allowed. */
export class NoAccessError extends Error {}

/** Sign-in is needed again (the user is being redirected). */
export class SignInRequiredError extends Error {}

export interface Me {
    name: string;
    objectId: string;
}

export type StockStatus = "known" | "noMatch" | "lookupFailed";

export interface StockProduct {
    productId: string;
    barcode: string | null;
    displayName: string;
    brand: string | null;
    size: string | null;
    count: number;
    latestCheckedInAt: string;
    status: StockStatus;
}

/** GET /api/stock: grouped and sorted by the API (named A–Z, then unnamed, newest first). */
export interface Stock {
    generatedAt: string;
    totalUnits: number;
    products: StockProduct[];
}

export interface Ping {
    status: string;
    commit: string;
}

/**
 * The one way the app talks to the API Function app (called directly: SWA Free has no linked backends).
 * Attaches the bearer token; a 401, or a token that can't be renewed silently, sends the user through sign-in
 * again and back to where they were; a 403 is a NoAccessError.
 */
export class ApiClient {
    constructor(
        private readonly msal: IPublicClientApplication,
        private readonly config: AppConfig,
        private readonly fetcher: typeof fetch = (...args) => fetch(...args),
    ) {}

    me(): Promise<Me> {
        return this.get<Me>("/api/me");
    }

    stock(): Promise<Stock> {
        return this.get<Stock>("/api/stock");
    }

    /** Public: no token needed. */
    async ping(): Promise<Ping> {
        const response = await this.fetcher(`${this.config.apiBaseUrl}/api/ping`);
        if (!response.ok) throw new Error(`Ping failed: HTTP ${response.status}`);
        return (await response.json()) as Ping;
    }

    async get<T>(path: string): Promise<T> {
        const token = await this.token();
        const response = await this.fetcher(`${this.config.apiBaseUrl}${path}`, {
            headers: { Authorization: `Bearer ${token}` },
        });
        if (response.status === 401) {
            await this.reauthenticate();
        }
        if (response.status === 403) {
            throw new NoAccessError("You're signed in, but this account can't use Cupboard Tracker.");
        }
        if (!response.ok) throw new Error(`${path} failed: HTTP ${response.status}`);
        return (await response.json()) as T;
    }

    private async token(): Promise<string> {
        const account = this.msal.getActiveAccount() ?? this.msal.getAllAccounts()[0];
        if (!account) return this.reauthenticate();
        try {
            const result = await this.msal.acquireTokenSilent({ scopes: [this.config.apiScope], account });
            return result.accessToken;
        } catch (error) {
            if (error instanceof InteractionRequiredAuthError) return this.reauthenticate();
            throw error;
        }
    }

    private async reauthenticate(): Promise<never> {
        await this.msal.acquireTokenRedirect({
            scopes: [this.config.apiScope],
            redirectStartPage: window.location.href,
        });
        throw new SignInRequiredError("Signing in again.");
    }
}
