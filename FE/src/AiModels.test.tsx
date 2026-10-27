import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { ApiError, type AiModels, type ApiClient } from "./api";
import { AiModelsScreen } from "./AiModelsScreen";
import { AiModelsSection } from "./AiModelsSection";
import { aiModels, gpt4o, luna } from "./aiModelsFixture";
import { activeModel, limitMessage, newModelId, pence } from "./aiModels";
import { AI_MODEL_ADD_PATH, AI_MODELS_PATH } from "./router";

// 08:00 UTC on Tuesday 27 October 2026.
const NOW = new Date("2026-10-27T08:00:00.000Z");

beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(NOW);
    window.history.replaceState(null, "", "/");
});
afterEach(() => vi.useRealTimers());

const asApi = (api: object) => api as unknown as ApiClient;

describe("Settings: AI models section", () => {
    test("one row per job with its model and deployment, and a Manage button with the count", async () => {
        render(<AiModelsSection api={asApi({ aiModels: vi.fn(async () => aiModels()) })} />);

        const section = await screen.findByRole("region", { name: "AI models" });
        const rows = within(section).getAllByText(/Recipes and ideas|Photos|Generic names/);
        expect(rows.map((r) => r.textContent)).toEqual(["Recipes and ideas", "Photos", "Generic names"]);
        expect(section).toHaveTextContent("Recipes and ideasGPT-6 Luna · gpt-6-luna");
        expect(section).toHaveTextContent("PhotosGPT-4o · gpt-4o"); // the default, matched to a registered model
        const manage = within(section).getByRole("link", { name: /Manage AI models/ });
        expect(manage).toHaveTextContent("3 models");

        fireEvent.click(manage);
        expect(window.location.pathname).toBe(AI_MODELS_PATH);
    });

    test("a job still on an unregistered default shows the deployment name", async () => {
        const data = aiModels({ models: [], active: { recipes: null, photos: null, genericNames: null } });
        render(<AiModelsSection api={asApi({ aiModels: vi.fn(async () => data) })} />);

        expect(await screen.findByText("0 models")).toBeInTheDocument();
        expect(screen.getAllByText("gpt-4o")).toHaveLength(3);
    });

    test("a failure says so and can be retried", async () => {
        const aiModelsCall = vi.fn().mockRejectedValueOnce(new Error("down")).mockResolvedValue(aiModels());
        render(<AiModelsSection api={asApi({ aiModels: aiModelsCall })} />);

        expect(await screen.findByRole("alert")).toHaveTextContent("Couldn't load the AI models.");
        fireEvent.click(screen.getByRole("button", { name: "Try again" }));
        expect(await screen.findByText(/GPT-6 Luna/)).toBeInTheDocument();
    });

    test("no temperature or data-location wording anywhere", async () => {
        render(<AiModelsSection api={asApi({ aiModels: vi.fn(async () => aiModels()) })} />);
        await screen.findByText(/GPT-6 Luna/);

        expect(document.body.textContent).not.toMatch(/temperature|region|data zone|global standard/i);
    });
});

function setup(data: AiModels = aiModels(), overrides: Record<string, unknown> = {}) {
    let current = data;
    const api = {
        aiModels: vi.fn(async () => current),
        setActiveAiModel: vi.fn(async (job: string, modelId: string) => {
            current = {
                ...current,
                active: {
                    ...current.active,
                    [job]: {
                        modelId,
                        changedBy: "u1",
                        changedByName: "You",
                        changedAt: NOW.toISOString(),
                        previousModelId: current.active[job as "recipes"]?.modelId ?? null,
                    },
                },
                testRuns: null,
            };
            return current;
        }),
        revertAiModel: vi.fn(async (job: string) => {
            const a = current.active[job as "recipes"];
            current = {
                ...current,
                active: {
                    ...current.active,
                    [job]: a?.previousModelId
                        ? { ...a, modelId: a.previousModelId, previousModelId: a.modelId }
                        : null,
                },
                testRuns: null,
            };
            return current;
        }),
        removeAiModel: vi.fn(async (id: string) => {
            current = { ...current, models: current.models.filter((m) => m.id !== id), testRuns: null };
            return current;
        }),
        ...overrides,
    };
    render(<AiModelsScreen api={asApi(api)} pollMs={20} />);
    return api;
}

describe("AI models page", () => {
    test("In use: a card per job with the model, deployment, who changed it, Change and Go back to", async () => {
        setup();

        expect(await screen.findByRole("heading", { level: 1, name: "AI models" })).toBeInTheDocument();
        expect(screen.getByRole("link", { name: "Back to settings" })).toBeInTheDocument();
        const recipes = screen.getByRole("group", { name: "Recipes and ideas" });
        expect(recipes).toHaveTextContent("GPT-6 Luna");
        expect(recipes).toHaveTextContent("gpt-6-luna · Changed by Gary · 24 Oct");
        expect(
            within(recipes).getByRole("button", { name: "Change the model for recipes" }),
        ).toBeInTheDocument();
        expect(within(recipes).getByRole("button", { name: "Go back to GPT-4o" })).toBeInTheDocument();
        const photos = screen
            .getByText("Photos", { selector: "span[class*=jobName]" })
            .closest("div[class*=card]") as HTMLElement;
        expect(photos).toHaveTextContent("Since setup");
        expect(within(photos).queryByRole("button", { name: /Go back/ })).toBeNull();
    });

    test("Models: Add a model, and a card per model with status, job chips, a note, who and when", async () => {
        setup();
        expect(await screen.findByRole("heading", { level: 2, name: /Models/ })).toHaveTextContent("3 added");
        expect(screen.getByRole("link", { name: "Add a model" })).toBeInTheDocument();
        const cards = ["GPT-4o", "GPT-6 Luna", "Mini"].map((name) => screen.getByRole("group", { name }));
        expect(cards[0]).toHaveTextContent("All passed");
        expect(cards[0]).toHaveTextContent("About 0.4p a recipe");
        expect(cards[0]).toHaveTextContent("Added by Gary, 20 Oct · tested 24 Oct");
        expect(cards[1]).toHaveTextContent("Passed, 1 warning");
        expect(cards[1]).toHaveTextContent("✕ Photos");
        expect(cards[1]).toHaveTextContent("About 0.6p a recipe · Not for photos");
        expect(cards[2]).toHaveTextContent("Failed");
        expect(cards[2]).toHaveTextContent(
            "No deployment with that name on this Foundry account. Check the Name column in Foundry.",
        );
        expect(cards[2]).toHaveTextContent("Added by Sam, 26 Oct · tested 26 Oct");
    });

    test("with no models it still lists the jobs on their defaults and offers Add a model", async () => {
        setup(aiModels({ models: [], active: { recipes: null, photos: null, genericNames: null } }));

        expect(await screen.findByText("0 added")).toBeInTheDocument();
        expect(screen.getAllByRole("button", { name: /^Change the model for/ })).toHaveLength(3);
        expect(screen.getByRole("link", { name: "Add a model" })).toBeInTheDocument();
        expect(screen.queryByRole("button", { name: /^Remove/ })).toBeNull();
    });

    test("Change lists every model; those that didn't pass the job's tests are greyed out and can't be chosen", async () => {
        setup();
        fireEvent.click(await screen.findByRole("button", { name: "Change the model for photos" }));

        const dialog = screen.getByRole("dialog", { name: "Model for photos" });
        expect(dialog).toHaveFocus();
        const options = within(dialog).getAllByRole("radio");
        expect(options.map((o) => o.textContent)).toEqual([
            "GPT-4oAbout 0.2p a photoIn use",
            "GPT-6 LunaDidn’t pass the photos tests",
            "MiniDidn’t pass the photos tests",
        ]);
        expect(options[1]).toHaveAttribute("aria-disabled", "true");
        fireEvent.click(options[1]);
        expect(within(dialog).queryByText(/Switch photos/)).toBeNull(); // nothing happened
        expect(
            within(dialog).getByText("Only models that passed this job's tests can be chosen."),
        ).toBeInTheDocument();
    });

    test("choosing asks first, with both costs and 'within a minute'; Switch changes it, with a toast and Undo", async () => {
        const api = setup();
        fireEvent.click(await screen.findByRole("button", { name: "Change the model for generic names" }));
        fireEvent.click(within(screen.getByRole("dialog")).getByRole("radio", { name: /GPT-6 Luna/ }));

        const dialog = screen.getByRole("dialog", { name: "Switch generic names to GPT-6 Luna?" });
        expect(dialog).toHaveTextContent("GPT-6 Luna" + "not estimated");
        expect(dialog).toHaveTextContent("GPT-4o (now)" + "not estimated");
        expect(dialog).toHaveTextContent("Estimated from its last test run.");
        expect(dialog).toHaveTextContent("Takes effect within a minute.");
        expect(api.setActiveAiModel).not.toHaveBeenCalled();

        fireEvent.click(within(dialog).getByRole("button", { name: "Switch" }));

        await waitFor(() => expect(api.setActiveAiModel).toHaveBeenCalledWith("genericNames", "luna"));
        expect(await screen.findByRole("status")).toHaveTextContent("Generic names now use GPT-6 Luna");
        expect(screen.queryByRole("dialog")).toBeNull();

        fireEvent.click(screen.getByRole("button", { name: "Undo" }));
        await waitFor(() => expect(api.revertAiModel).toHaveBeenCalledWith("genericNames"));
        // Undoing is itself a change, so it says so (and can be undone again).
        expect(await screen.findByText("Generic names now use GPT-4o")).toBeInTheDocument();
    });

    test("the confirmation compares the cost of a recipe on the new and the current model", async () => {
        setup(
            aiModels({
                active: { recipes: null, photos: null, genericNames: null }, // everything on gpt-4o
            }),
        );
        fireEvent.click(await screen.findByRole("button", { name: "Change the model for recipes" }));
        fireEvent.click(within(screen.getByRole("dialog")).getByRole("radio", { name: /GPT-6 Luna/ }));

        const dialog = screen.getByRole("dialog", { name: "Switch recipes to GPT-6 Luna?" });
        expect(dialog).toHaveTextContent("GPT-6 Luna" + "about 0.6p a recipe");
        expect(dialog).toHaveTextContent("GPT-4o (now)" + "about 0.4p a recipe");
        expect(dialog).not.toHaveTextContent(/temperature/i);
    });

    test("Back returns to the list; Cancel and Escape close the sheet", async () => {
        setup();
        fireEvent.click(await screen.findByRole("button", { name: "Change the model for generic names" }));
        fireEvent.click(within(screen.getByRole("dialog")).getByRole("radio", { name: /GPT-6 Luna/ }));
        fireEvent.click(screen.getByRole("button", { name: "Back" }));
        expect(screen.getByRole("dialog", { name: "Model for generic names" })).toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
        expect(screen.queryByRole("dialog")).toBeNull();

        fireEvent.click(screen.getByRole("button", { name: "Change the model for photos" }));
        fireEvent.keyDown(document, { key: "Escape" });
        expect(screen.queryByRole("dialog")).toBeNull();
    });

    test("Go back to <previous> reverts the job, with a toast that can be undone", async () => {
        const api = setup();
        fireEvent.click(await screen.findByRole("button", { name: "Go back to GPT-4o" }));

        await waitFor(() => expect(api.revertAiModel).toHaveBeenCalledWith("recipes"));
        expect(await screen.findByRole("status")).toHaveTextContent("Recipes and ideas now use GPT-4o");
        // Going back leaves the model it went back from as the way to undo it.
        expect(screen.getByRole("group", { name: "Recipes and ideas" })).toHaveTextContent("GPT-4o");
        expect(screen.getByRole("button", { name: "Go back to GPT-6 Luna" })).toBeInTheDocument();
    });

    test("a refusal from the API shows its reason", async () => {
        const api = setup(undefined, {
            setActiveAiModel: vi.fn(async () => {
                throw new ApiError(409, "x", { error: "GPT-6 Luna hasn't passed the tests for photos." });
            }),
        });
        fireEvent.click(await screen.findByRole("button", { name: "Change the model for generic names" }));
        fireEvent.click(within(screen.getByRole("dialog")).getByRole("radio", { name: /GPT-6 Luna/ }));
        fireEvent.click(screen.getByRole("button", { name: "Switch" }));

        expect(await screen.findByRole("status")).toHaveTextContent(
            "GPT-6 Luna hasn't passed the tests for photos.",
        );
        expect(api.setActiveAiModel).toHaveBeenCalled();
    });

    test("Remove is only offered for models that aren't in use, and asks twice", async () => {
        const api = setup();
        await screen.findByRole("group", { name: "Mini" });

        expect(screen.queryByRole("button", { name: "Remove GPT-6 Luna" })).toBeNull(); // active for recipes
        expect(screen.queryByRole("button", { name: "Remove GPT-4o" })).toBeNull(); // the default for photos and names
        fireEvent.click(screen.getByRole("button", { name: "Remove Mini" }));
        expect(api.removeAiModel).not.toHaveBeenCalled();
        fireEvent.click(screen.getByRole("button", { name: "Tap again to remove Mini" }));

        await waitFor(() => expect(api.removeAiModel).toHaveBeenCalledWith("typo"));
        expect(await screen.findByRole("status")).toHaveTextContent("Mini removed");
        expect(screen.queryByText("gpt-5-mnii")).toBeNull();
    });

    test("a model being tested says so and is looked at again until it's done", async () => {
        const testing = { ...luna, testing: "run-9" };
        let phase = 0;
        const api = setup(aiModels(), {
            aiModels: vi.fn(async () =>
                phase++ < 2 ? aiModels({ models: [gpt4o, testing] }) : aiModels({ models: [gpt4o, luna] }),
            ),
        });

        const cardEl = await screen.findByRole("group", { name: "GPT-6 Luna" });
        await within(cardEl).findByText("Testing…");
        expect(cardEl).toHaveTextContent("Tests are running.");
        expect(within(cardEl).getByRole("link", { name: "Run tests for GPT-6 Luna" })).toHaveTextContent(
            "See the tests",
        );
        await waitFor(() => expect(screen.queryByText(/^Testing…$/)).toBeNull());
        expect(api.aiModels.mock.calls.length).toBeGreaterThanOrEqual(3);
    });

    test("Run tests opens that model's test, and Add a model the form", async () => {
        setup();
        fireEvent.click(await screen.findByRole("link", { name: "Run tests for Mini" }));
        expect(window.location.pathname).toBe("/settings/ai-models/typo/test");

        fireEvent.click(screen.getByRole("link", { name: "Add a model" }));
        expect(window.location.pathname).toBe(AI_MODEL_ADD_PATH);
    });

    test("a failed load says so and can be retried", async () => {
        const failing = vi.fn().mockRejectedValueOnce(new Error("x")).mockResolvedValue(aiModels());
        setup(undefined, { aiModels: failing });

        expect(await screen.findByRole("alert")).toHaveTextContent("Couldn't load the AI models.");
        fireEvent.click(screen.getByRole("button", { name: "Try again" }));
        expect(await screen.findByRole("group", { name: "GPT-6 Luna" })).toBeInTheDocument();
    });
});

describe("helpers", () => {
    test("costs read as pence under a pound, pounds above, and null when unknown", () => {
        expect(pence(0.004)).toBe("0.4p");
        expect(pence(0.1)).toBe("10.0p");
        expect(pence(1.5)).toBe("£1.50");
        expect(pence(null)).toBeNull();
    });

    test("a job on its default shows the registered model with that deployment, else the deployment name", () => {
        const data = aiModels({ active: { recipes: null, photos: null, genericNames: null } });
        expect(activeModel(data, "photos").label).toBe("GPT-4o");
        expect(activeModel({ ...data, models: [] }, "photos").label).toBe("gpt-4o");
    });

    test("new model ids satisfy the API's pattern", () => {
        for (const label of [
            "GPT-6 Luna",
            "  ",
            "Ünïcode ✨ model with a very long name that goes on and on and on",
        ]) {
            expect(newModelId(label)).toMatch(/^[A-Za-z0-9_-]{1,40}$/);
        }
        expect(newModelId("GPT-6 Luna", () => 0)).toBe("gpt-6-luna-0000");
    });

    test("the limit message gives the time to try again in UK time", () => {
        expect(limitMessage(5, "2026-10-27T13:05:00.000Z")).toBe(
            "Test runs are limited to 5 an hour. Try again at 13:05.",
        );
        expect(limitMessage(5, undefined)).toBe("Test runs are limited to 5 an hour.");
    });
});

// Keep act imported for tests that advance timers.
void act;
