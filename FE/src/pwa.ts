/** Registers the service worker (#76) once the page has loaded, so it never slows the first paint. */
export function registerServiceWorker(nav: Navigator = navigator, win: Window = window): void {
    if (!("serviceWorker" in nav)) return;
    const register = () => {
        nav.serviceWorker.register("/sw.js", { scope: "/" }).catch((error: unknown) => {
            console.warn("Couldn't register the service worker", error);
        });
    };
    if (win.document.readyState === "complete") register();
    else win.addEventListener("load", register, { once: true });
}
