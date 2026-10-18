import "@testing-library/jest-dom/vitest";

// No real SignalR in tests: screens get a connection that never connects (the scan feed's own tests inject a fake).
vi.mock("@microsoft/signalr", async (importOriginal) => {
    const actual = await importOriginal<typeof import("@microsoft/signalr")>();
    class FakeBuilder {
        withUrl() {
            return this;
        }
        withAutomaticReconnect() {
            return this;
        }
        configureLogging() {
            return this;
        }
        build() {
            return {
                state: actual.HubConnectionState.Disconnected,
                on() {},
                onreconnected() {},
                start: () => Promise.reject(new Error("no SignalR in tests")),
                stop: () => Promise.resolve(),
            };
        }
    }
    return { ...actual, HubConnectionBuilder: FakeBuilder };
});

// Screens' fake APIs mostly have no scan feed: its "couldn't connect / catch up" warnings are expected noise there.
const warn = console.warn.bind(console);
console.warn = (...args: unknown[]) => {
    if (typeof args[0] === "string" && args[0].startsWith("Scan feed")) return;
    warn(...args);
};
