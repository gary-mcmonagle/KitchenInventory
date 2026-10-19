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

    test("takes over after a deploy, and never intercepts requests (no fetch handler, so API calls go straight out)", () => {
        expect(sw).toContain('addEventListener("install"');
        expect(sw).toContain("skipWaiting()");
        expect(sw).toContain("clients.claim()");
        expect(sw).not.toMatch(/addEventListener\(\s*["']fetch["']/);
        expect(sw).not.toMatch(/onfetch/);
        expect(sw).not.toMatch(/caches\./);
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
