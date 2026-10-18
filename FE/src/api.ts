import { InteractionRequiredAuthError, type IPublicClientApplication } from "@azure/msal-browser";
import type { AppConfig } from "./config";
import type { FeedEvent } from "./scanFeed";

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

/** Which part of the stock list a product is in (#52). */
export type StockSection = "useFirst" | "later" | "waitingForName";

export interface StockProduct {
    productId: string;
    barcode: string | null;
    displayName: string;
    brand: string | null;
    size: string | null;
    count: number;
    latestCheckedInAt: string;
    status: StockStatus;
    section: StockSection;
    /** YYYY-MM-DD: the soonest estimated expiry date of its units; null until it has an estimate. */
    soonestEstimatedExpiryDate: string | null;
    /** Days from today (UK) to that date: negative once passed. */
    daysUntilSoonest: number | null;
    shelfLifeDays: number | null;
    /** Units opened, and units in the freezer (#65). */
    openedCount: number;
    frozenCount: number;
    /** When the soonest unit's date is from being opened, frozen or defrosted: which, and since when (YYYY-MM-DD). */
    soonestState: UnitState | null;
    soonestStateOn: string | null;
}

/** Opened, frozen, defrosted (#65). */
export type UnitState = "opened" | "frozen" | "defrosted";

export type StorageName = "cupboard" | "fridge" | "freezer";

/** How long a product keeps once opened, in the freezer, or once defrosted, and where to keep it then (#65). */
export interface StateShelfLife {
    days: number;
    storage: StorageName;
    /** "jev" or "manual". */
    source: string;
}

/** GET /api/stock: grouped, put in sections and sorted by the API (Use first and Later by soonest estimated expiry,
 * then name; Waiting for a name newest first). */
export interface Stock {
    generatedAt: string;
    totalUnits: number;
    /** Products in Use first. */
    useFirstCount: number;
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
    shelfLifeDays: number | null;
    /** "jev" or "manual". */
    shelfLifeSource: string | null;
    /** In-stock units, opened first and frozen last, then soonest estimated expiry (#53, #65). */
    units: ProductUnit[];
    /** Where it's kept sealed (#52); null until estimated. */
    usualStorage: StorageName | null;
    opened: StateShelfLife | null;
    frozen: StateShelfLife | null;
    defrosted: StateShelfLife | null;
    openedCount: number;
    frozenCount: number;
}

export interface ProductUnit {
    unitId: string;
    checkedInAt: string;
    /** YYYY-MM-DD, or null until estimated. */
    estimatedExpiryDate: string | null;
    expirySource: "shelfLife" | "manual" | UnitState | null;
    daysUntilExpiry: number | null;
    /** YYYY-MM-DD, or null (#65). */
    openedOn: string | null;
    frozenOn: string | null;
    defrostedOn: string | null;
    /** Where to keep it now, once opened, frozen or defrosted. */
    currentStorage: StorageName | null;
    /** Its new estimate is on its way (the product's estimate for that state isn't in yet). */
    estimatePending: boolean;
    canUndoStateChange: boolean;
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

/** GET /api/scanners (#55). */
export interface Scanners {
    generatedAt: string;
    hub: {
        hubId: string;
        lastSeenAt: string;
        heartbeatIntervalSeconds: number;
        offlineThresholdMinutes: number;
        online: boolean;
    } | null;
    devices: Array<{
        deviceId: string;
        role: "in" | "out";
        /** null until a heartbeat reports the receiver. */
        present: boolean | null;
        presentSince: string | null;
        absentSince: string | null;
        lastScanAt: string | null;
    }>;
    noMatchCheckIns: Array<{
        /** null for a store label (#58): it has no product until someone says what it is. */
        productId: string | null;
        barcode: string;
        status: "noMatch" | "lookupFailed" | "storeLabel";
        scannedAt: string;
        unitId: string | null;
        inStockCount: number;
        /** The store-label check-in to assign (#58); null otherwise. */
        scanId: string | null;
    }>;
    unmatchedCheckOuts: Array<{
        scanId: string;
        barcode: string;
        productId: string | null;
        displayName: string | null;
        scannedAt: string;
        /** An in-store label: it can never match a unit, so check it out from the app (#58). */
        storeLabel: boolean;
    }>;
    recentlyCheckedOut: Array<{
        unitId: string;
        productId: string;
        displayName: string;
        checkedOutAt: string;
        source: string | null;
    }>;
}

/** POST /api/photos/recognise (#58): what the photo shows. */
export interface PhotoRecognition {
    /** false when the model couldn't be reached: the app falls back to picking and typing. */
    available: boolean;
    readable: boolean;
    items: Array<{
        name: string;
        brand: string | null;
        size: string | null;
        kind: "packaged" | "loose" | "storeLabelled";
        quantity: number;
        /** An existing product with that name. */
        match: { productId: string; displayName: string } | null;
    }>;
}

export interface LooseProduct {
    productId: string;
    displayName: string;
    shelfLifeDays: number | null;
    lastAddedAt: string | null;
}

export type AddSource = "photo" | "pick" | "typed";

/** One row to add: an existing product, or a name (matched to one, or a new loose product). */
export interface AddItem {
    productId?: string;
    name?: string;
    brand?: string;
    size?: string;
    quantity: number;
    source: AddSource;
}

export interface Added {
    units: number;
    items: Array<{ productId: string; displayName: string; quantity: number; createdProduct: boolean }>;
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

    /** Checks out that unit (item detail; 409 → ApiError: not in stock). */
    checkOutUnit(unitId: string): Promise<CheckedOut> {
        return this.send<CheckedOut>("POST", `/api/units/${encodeURIComponent(unitId)}/check-out`);
    }

    /** Sets one unit's estimated expiry date by hand (YYYY-MM-DD). */
    setUnitExpiry(unitId: string, date: string): Promise<{ unitId: string; estimatedExpiryDate: string }> {
        return this.send("PUT", `/api/units/${encodeURIComponent(unitId)}/estimated-expiry`, { date });
    }

    /** Sets a product's shelf life by hand, for units bought from now on. */
    setShelfLife(productId: string, days: number): Promise<{ productId: string; shelfLifeDays: number }> {
        return this.send("PUT", `/api/products/${encodeURIComponent(productId)}/shelf-life`, { days });
    }

    /** Marks a unit opened, frozen or defrosted (#65), today or on <c>date</c> (YYYY-MM-DD). 409: not in stock, or
     * defrosting a unit that isn't frozen. */
    markUnit(
        unitId: string,
        state: UnitState,
        date?: string,
    ): Promise<{ productId: string; unit: ProductUnit }> {
        return this.send("POST", `/api/units/${encodeURIComponent(unitId)}/${state}`, date ? { date } : {});
    }

    /** The stock list's Opened button (#65): opens the unit used next (not one in the freezer). 409: one's already
     * open, or they're all frozen. */
    openNext(productId: string): Promise<{ productId: string; unit: ProductUnit }> {
        return this.send("POST", `/api/products/${encodeURIComponent(productId)}/opened`);
    }

    /** Puts a unit's estimate back as it was before its last state change (#65). */
    undoStateChange(unitId: string): Promise<{ productId: string; unit: ProductUnit }> {
        return this.send("POST", `/api/units/${encodeURIComponent(unitId)}/undo-state-change`);
    }

    /** Sets how long a product keeps once opened, by hand (#65): for units opened from now on. */
    setOpenedShelfLife(
        productId: string,
        days: number,
    ): Promise<{ productId: string; shelfLifeDays: number }> {
        return this.send("PUT", `/api/products/${encodeURIComponent(productId)}/opened-shelf-life`, { days });
    }

    /** Corrects a known product's details (400 → ApiError with FieldErrors). */
    editDetails(
        productId: string,
        details: NameDetails,
    ): Promise<{ productId: string; displayName: string }> {
        return this.send("PUT", `/api/products/${encodeURIComponent(productId)}/details`, details);
    }

    /** What the scan feed missed since <c>since</c>, plus toasts still waiting for a name (#54). */
    scanFeed(since?: string): Promise<{ generatedAt: string; events: FeedEvent[] }> {
        const query = since ? `?since=${encodeURIComponent(since)}` : "";
        return this.get(`/api/scan-feed${query}`);
    }

    /** Hides a product's amber toast for the household (#54). */
    dismissFeed(productId: string): Promise<{ productId: string; feedDismissedAt: string }> {
        return this.send("POST", `/api/scan-feed/${encodeURIComponent(productId)}/dismiss`);
    }

    /** The scan feed hub's URL; @microsoft/signalr adds /negotiate. */
    get feedUrl(): string {
        return `${this.config.apiBaseUrl}/api`;
    }

    /** A token for the API (the scan feed's negotiate). Sends the user through sign-in again if it can't renew. */
    accessToken(): Promise<string> {
        return this.token();
    }

    /** The scanners screen (#55). */
    scanners(): Promise<Scanners> {
        return this.get<Scanners>("/api/scanners");
    }

    /** Dismisses an unmatched check-out (idempotent). */
    dismissUnmatched(scanId: string): Promise<{ scanId: string; dismissed: boolean }> {
        return this.send("POST", `/api/unmatched-check-outs/${encodeURIComponent(scanId)}/dismiss`);
    }

    /** Puts a checked-out unit back in stock (idempotent). */
    undoCheckOut(unitId: string): Promise<UndoneCheckOut> {
        return this.send<UndoneCheckOut>("POST", `/api/units/${encodeURIComponent(unitId)}/undo-check-out`);
    }

    /** What a photo shows (#58). The JPEG is sent as is and never stored. `available: false`: try typing instead. */
    recognisePhoto(jpeg: Blob): Promise<PhotoRecognition> {
        return this.send<PhotoRecognition>("POST", "/api/photos/recognise", jpeg);
    }

    /** Loose products for the quick pick, most recently added first (#58). */
    looseProducts(): Promise<{ products: LooseProduct[] }> {
        return this.get("/api/products/loose");
    }

    /** Adds items to stock (#58). Send the same request ID again to retry safely. */
    addToStock(requestId: string, items: AddItem[]): Promise<Added> {
        return this.send<Added>("POST", "/api/stock/add", { requestId, items });
    }

    /** Says what a store-labelled check-in was (#58): adds its unit at the scan time. 409 → already assigned. */
    assignStoreLabel(
        scanId: string,
        item: Omit<AddItem, "quantity">,
    ): Promise<{ scanId: string; unitId: string; productId: string; displayName: string }> {
        return this.send("POST", `/api/store-label-check-ins/${encodeURIComponent(scanId)}/assign`, item);
    }

    /** Hides a store-label toast for the household; it stays on the scanners screen (#58). */
    dismissStoreLabel(scanId: string): Promise<{ scanId: string; dismissed: boolean }> {
        return this.send("POST", `/api/scan-feed/store-labels/${encodeURIComponent(scanId)}/dismiss`);
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
        if (body instanceof Blob) {
            headers["Content-Type"] = body.type || "application/octet-stream";
            init.body = body;
        } else if (body !== undefined) {
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
