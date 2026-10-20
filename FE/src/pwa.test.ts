import indexHtml from "../index.html?raw";
import manifestText from "../public/manifest.webmanifest?raw";
import staticWebAppConfig from "../public/staticwebapp.config.json?raw";
import swText from "../public/sw.js?raw";
import addCss from "./Add.module.css?raw";
import appCss from "./App.module.css?raw";
import navCss from "./BottomNav.module.css?raw";
import nameCss from "./Name.module.css?raw";
import { registerServiceWorker } from "./pwa";

// The installable app (#76): manifest, icons, iOS tags, the service worker, the Static Web App config and safe areas.
const icons = import.meta.glob("../public/icons/*.png", {
    eager: true,
    query: "?inline",
    import: "default",
}) as Record<string, string>;

/** Width and height of a PNG (bytes 16–23), from its inlined data URL. */
function pngSize(path: string): [number, number] {
    const dataUrl = icons[`../public${path}`];
    expect(dataUrl, path).toBeDefined();
    const bytes = Uint8Array.from(atob(dataUrl.split(",")[1]), (c) => c.charCodeAt(0));
    const view = new DataView(bytes.buffer);
    return [view.getUint32(16), view.getUint32(20)];
}

describe("the web app manifest", () => {
    const manifest = JSON.parse(manifestText) as {
        id: string;
        name: string;
        short_name: string;
        start_url: string;
        scope: string;
        display: string;
        theme_color: string;
        background_color: string;
        icons: Array<{ src: string; sizes: string; type: string; purpose: string }>;
    };

    test("has what makes it installable, with the agreed names and colours", () => {
        expect(manifest).toMatchObject({
            id: "/",
            name: "Cupboard Tracker",
            short_name: "Cupboard",
            start_url: "/",
            scope: "/",
            display: "standalone",
            theme_color: "#2F6B4F",
            background_color: "#F4F7F3",
        });
    });

    test("lists 192 and 512 px PNG icons and a maskable 512, and every icon exists", () => {
        const icons = manifest.icons.map((i) => `${i.sizes} ${i.purpose}`);
        expect(icons).toEqual(expect.arrayContaining(["192x192 any", "512x512 any", "512x512 maskable"]));
        for (const icon of manifest.icons) {
            expect(icon.type).toBe("image/png");
            const [w] = icon.sizes.split("x").map(Number);
            expect(pngSize(icon.src)).toEqual([w, w]);
        }
    });
});

describe("index.html", () => {
    const html = indexHtml;

    test("links the manifest and the 180 px apple-touch-icon", () => {
        expect(html).toContain('<link rel="manifest" href="/manifest.webmanifest" />');
        expect(html).toContain('<link rel="apple-touch-icon" href="/icons/apple-touch-icon.png" />');
        expect(pngSize("/icons/apple-touch-icon.png")).toEqual([180, 180]);
    });

    test("has the iOS web app tags, theme colour and viewport-fit=cover for the safe areas", () => {
        expect(html).toContain('<meta name="apple-mobile-web-app-capable" content="yes" />');
        expect(html).toContain('<meta name="apple-mobile-web-app-title" content="Cupboard" />');
        expect(html).toContain('<meta name="apple-mobile-web-app-status-bar-style" content="default" />');
        expect(html).toMatch(/<meta name="theme-color" content="#[0-9A-Fa-f]{6}" \/>/);
        expect(html).toContain("viewport-fit=cover");
    });
});

describe("the service worker", () => {
    const sw = swText;

    test("takes over after a deploy", () => {
        expect(sw).toContain('addEventListener("install"');
        expect(sw).toContain("skipWaiting()");
        expect(sw).toContain("clients.claim()");
    });

    /** Runs sw.js as the build leaves it (#85), with fake caches and fetch. */
    function built(online: boolean) {
        const files = ["/index.html", "/assets/index-abc.js", "/icons/icon-192.png"];
        const text = sw
            .replace('"__SHELL_VERSION__"', '"v2"')
            .replace("/* __SHELL_FILES__ */ []", JSON.stringify(files));
        const stores = new Map<string, Map<string, Response>>([["cupboard-shell-v1", new Map()]]);
        const open = async (name: string) => {
            if (!stores.has(name)) stores.set(name, new Map());
            const store = stores.get(name)!;
            return {
                addAll: async (urls: string[]) =>
                    urls.forEach((u) => store.set(u, new Response(`cached ${u}`))),
                put: async (url: string, response: Response) => void store.set(url, response),
                match: async (url: string) => store.get(url),
            };
        };
        const caches = {
            open,
            keys: async () => [...stores.keys()],
            delete: async (name: string) => stores.delete(name),
            match: async (url: string) => {
                for (const store of stores.values()) if (store.has(url)) return store.get(url);
                return undefined;
            },
        };
        const fetched: string[] = [];
        const fetch = vi.fn(async (request: Request | string) => {
            const url = typeof request === "string" ? request : request.url;
            fetched.push(url);
            if (!online) throw new TypeError("Failed to fetch");
            return new Response(`network ${url}`);
        });
        const listeners: Record<string, (event: unknown) => void> = {};
        const self = {
            addEventListener: (type: string, fn: (event: unknown) => void) => (listeners[type] = fn),
            skipWaiting: () => undefined,
            clients: { claim: async () => undefined },
            location: { origin: "https://kitchen.garymcmonagle.com" },
        };
        new Function("self", "caches", "fetch", text)(self, caches, fetch);
        const lifecycle = async (type: string) => {
            let work: Promise<unknown> = Promise.resolve();
            listeners[type]({ waitUntil: (p: Promise<unknown>) => (work = p) });
            await work;
        };
        const request = async (url: string, init: { method?: string; mode?: string } = {}) => {
            let answer: Promise<Response> | undefined;
            listeners.fetch({
                request: { url, method: init.method ?? "GET", mode: init.mode ?? "cors" },
                respondWith: (p: Promise<Response>) => (answer = p),
            });
            return answer ? (await answer).text() : undefined;
        };
        return { stores, fetched, lifecycle, request };
    }

    test("caches the shell on install and drops the last deploy's cache when it takes over", async () => {
        const worker = built(true);
        await worker.lifecycle("install");
        await worker.lifecycle("activate");
        expect([...worker.stores.keys()]).toEqual(["cupboard-shell-v2"]);
        expect([...worker.stores.get("cupboard-shell-v2")!.keys()]).toContain("/assets/index-abc.js");
    });

    test("the shell comes from the cache; the API, sign-in, version.json and writes are never touched", async () => {
        const worker = built(true);
        await worker.lifecycle("install");
        expect(await worker.request("https://kitchen.garymcmonagle.com/assets/index-abc.js")).toBe(
            "cached /assets/index-abc.js",
        );
        expect(
            await worker.request("https://func-cupboard-api-x.azurewebsites.net/api/stock"),
        ).toBeUndefined();
        expect(
            await worker.request("https://login.microsoftonline.com/tenant/oauth2/v2.0/token"),
        ).toBeUndefined();
        expect(await worker.request("https://kitchen.garymcmonagle.com/version.json")).toBeUndefined();
        expect(
            await worker.request("https://kitchen.garymcmonagle.com/assets/index-abc.js", { method: "POST" }),
        ).toBeUndefined();
    });

    test("pages come from the network when online, and from the cached app page offline", async () => {
        const online = built(true);
        await online.lifecycle("install");
        expect(await online.request("https://kitchen.garymcmonagle.com/list", { mode: "navigate" })).toBe(
            "network https://kitchen.garymcmonagle.com/list",
        );

        const offline = built(false);
        await offline.lifecycle("install");
        expect(await offline.request("https://kitchen.garymcmonagle.com/list", { mode: "navigate" })).toBe(
            "cached /index.html",
        );
    });

    test("unbuilt (the dev server), it caches nothing and answers nothing", async () => {
        const listeners: Record<string, (event: unknown) => void> = {};
        const self = {
            addEventListener: (type: string, fn: (event: unknown) => void) => (listeners[type] = fn),
            skipWaiting: () => undefined,
            location: { origin: "http://localhost:5173" },
        };
        new Function("self", sw)(self);
        const respondWith = vi.fn();
        listeners.fetch({
            request: { url: "http://localhost:5173/list", method: "GET", mode: "navigate" },
            respondWith,
        });
        expect(respondWith).not.toHaveBeenCalled();
    });
});

describe("registerServiceWorker", () => {
    function fakes(readyState: DocumentReadyState) {
        const register = vi.fn(() => Promise.resolve({} as ServiceWorkerRegistration));
        const listeners: Record<string, () => void> = {};
        const nav = { serviceWorker: { register } } as unknown as Navigator;
        const win = {
            document: { readyState },
            addEventListener: (type: string, fn: () => void) => (listeners[type] = fn),
        } as unknown as Window;
        return { register, listeners, nav, win };
    }

    test("registers /sw.js for the whole site only after the page has loaded", () => {
        const { register, listeners, nav, win } = fakes("loading");
        registerServiceWorker(nav, win);
        expect(register).not.toHaveBeenCalled();

        listeners.load();
        expect(register).toHaveBeenCalledWith("/sw.js", { scope: "/" });
    });

    test("registers at once if the page has already loaded", () => {
        const { register, nav, win } = fakes("complete");
        registerServiceWorker(nav, win);
        expect(register).toHaveBeenCalledTimes(1);
    });

    test("does nothing in a browser without service workers", () => {
        const { win } = fakes("complete");
        expect(() => registerServiceWorker({} as Navigator, win)).not.toThrow();
    });

    test("a failed registration is a warning, not an error", async () => {
        const { nav, win } = fakes("complete");
        (nav.serviceWorker.register as ReturnType<typeof vi.fn>).mockRejectedValueOnce(new Error("nope"));
        const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
        registerServiceWorker(nav, win);
        await vi.waitFor(() =>
            expect(warn).toHaveBeenCalledWith("Couldn't register the service worker", expect.any(Error)),
        );
        warn.mockRestore();
    });
});

describe("the Static Web App config", () => {
    const config = JSON.parse(staticWebAppConfig) as {
        navigationFallback: { rewrite: string; exclude: string[] };
        routes: Array<{ route: string; headers?: Record<string, string> }>;
        mimeTypes: Record<string, string>;
    };

    test("never rewrites the worker, the manifest or the icons to index.html", () => {
        expect(config.navigationFallback.exclude).toEqual(
            expect.arrayContaining(["/sw.js", "/manifest.webmanifest", "/icons/*", "/assets/*"]),
        );
    });

    test("serves the worker and the manifest with no-cache, and the manifest's content type", () => {
        const header = (route: string) =>
            config.routes.find((r) => r.route === route)?.headers?.["Cache-Control"];
        expect(header("/sw.js")).toBe("no-cache");
        expect(header("/manifest.webmanifest")).toBe("no-cache");
        expect(config.mimeTypes[".webmanifest"]).toBe("application/manifest+json");
    });
});

describe("safe areas in the installed app", () => {
    test("the page clears the notch, and the nav and bottom bars clear the home bar, only when installed", () => {
        const standalone = (name: string, css: string) => {
            const start = css.indexOf("@media (display-mode: standalone)");
            expect(start, name).toBeGreaterThan(-1);
            return css.slice(start);
        };
        expect(standalone("App", appCss)).toContain("env(safe-area-inset-top");
        for (const [name, css] of Object.entries({ BottomNav: navCss, Add: addCss, Name: nameCss })) {
            expect(standalone(name, css)).toContain("env(safe-area-inset-bottom");
        }
    });
});
