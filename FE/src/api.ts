import { InteractionRequiredAuthError, type IPublicClientApplication } from "@azure/msal-browser";
import type { AppConfig } from "./config";

/** The API refused this user (403): signed in, but not allowed. */
export class NoAccessError extends Error {}

/** The API answered with an error status (other than 401/403). */
export class ApiError extends Error {
    constructor(
        readonly status: number,
        message: string,
        readonly body?: unknown,
    ) {
        super(message);
    }
}

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

export interface CheckedOut {
    unitId: string;
    productId: string;
    displayName: string;
    remainingCount: number;
}

export interface UndoneCheckOut {
    unitId: string;
    productId: string;
    count: number;
    restored: boolean;
}

export interface Product {
    productId: string;
    barcode: string | null;
    status: StockStatus;
    displayName: string;
    brand: string | null;
    size: string | null;
    inStockCount: number;
    latestCheckedInAt: string | null;
    latestDeviceRole: "in" | "out" | null;
}

export interface NameDetails {
    name: string;
    brand?: string;
    size?: string;
}

/** A 400 from naming: the message per field (name, brand, size). */
export interface FieldErrors {
    errors?: Record<string, string>;
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

    product(productId: string): Promise<Product> {
        return this.get<Product>(`/api/products/${encodeURIComponent(productId)}`);
    }

    /** Names an unknown item (400 → ApiError with FieldErrors; 409 → already named). */
    nameProduct(
        productId: string,
        details: NameDetails,
    ): Promise<{ productId: string; displayName: string }> {
        return this.send("PUT", `/api/products/${encodeURIComponent(productId)}/name`, details);
    }

    /** Checks out one unit (409 → ApiError: nothing in stock). */
    checkOut(productId: string): Promise<CheckedOut> {
        return this.send<CheckedOut>("POST", `/api/products/${encodeURIComponent(productId)}/check-out`);
    }

    /** Puts a checked-out unit back in stock (idempotent). */
    undoCheckOut(unitId: string): Promise<UndoneCheckOut> {
        return this.send<UndoneCheckOut>("POST", `/api/units/${encodeURIComponent(unitId)}/undo-check-out`);
    }

    /** Public: no token needed. */
    async ping(): Promise<Ping> {
        const response = await this.fetcher(`${this.config.apiBaseUrl}/api/ping`);
        if (!response.ok) throw new Error(`Ping failed: HTTP ${response.status}`);
        return (await response.json()) as Ping;
    }

    get<T>(path: string): Promise<T> {
        return this.send<T>("GET", path);
    }

    async send<T>(method: "GET" | "POST" | "PUT", path: string, body?: unknown): Promise<T> {
        const token = await this.token();
        const headers: Record<string, string> = { Authorization: `Bearer ${token}` };
        const init: RequestInit = { headers };
        if (method !== "GET") init.method = method;
        if (body !== undefined) {
            headers["Content-Type"] = "application/json";
            init.body = JSON.stringify(body);
        }
        const response = await this.fetcher(`${this.config.apiBaseUrl}${path}`, init);
        if (response.status === 401) {
            await this.reauthenticate();
        }
        if (response.status === 403) {
            throw new NoAccessError("You're signed in, but this account can't use Cupboard Tracker.");
        }
        if (!response.ok) {
            const errorBody: unknown = await response.json().catch(() => undefined);
            throw new ApiError(response.status, `${path} failed: HTTP ${response.status}`, errorBody);
        }
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
