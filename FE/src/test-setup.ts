import "@testing-library/jest-dom/vitest";
import { configure } from "@testing-library/react";

// The suite runs many screens in parallel; under load the default 1 s for findBy… was occasionally too short (flaky
// first tests in NameScreen and ItemScreen). 3 s still fails fast on a real miss.
configure({ asyncUtilTimeout: 3000 });

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
                off() {},
                onreconnected() {},
                start: () => Promise.reject(new Error("no SignalR in tests")),
                stop: () => Promise.resolve(),
            };
        }
    }
    return { ...actual, HubConnectionBuilder: FakeBuilder };
});

// Screens' fake APIs mostly have no scan feed or shopping list (#84): their "couldn't connect / load" warnings are
// expected noise there.
const warn = console.warn.bind(console);
console.warn = (...args: unknown[]) => {
    if (
        typeof args[0] === "string" &&
        (args[0].startsWith("Scan feed") ||
            args[0].startsWith("The shopping list couldn't connect") ||
            args[0].startsWith("Couldn't load the shopping list"))
    )
        return;
    warn(...args);
};

// Aisles start collapsed unless they have something to use first (#71). Most tests are about rows, so every aisle
// starts open; the aisle tests clear this to check the real defaults.
beforeEach(() => {
    window.localStorage.setItem(
        "cupboard.aisles.open",
        JSON.stringify(
            Object.fromEntries(
                [
                    "fruitVeg",
                    "bakery",
                    "meatFish",
                    "dairyEggs",
                    "chilledDeli",
                    "frozen",
                    "pastaRiceGrains",
                    "tinsJars",
                    "saucesOils",
                    "bakingSpices",
                    "breakfastCereal",
                    "snacksSweets",
                    "drinks",
                    "other",
                ].map((id) => [id, true]),
            ),
        ),
    );
});
