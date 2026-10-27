import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { ApiError, type AiModels, type ApiClient, type ModelTestRun } from "./api";
import { aiModels, checks, gpt4o, luna, run, typo } from "./aiModelsFixture";
import { ModelTestScreen } from "./ModelTestScreen";

const NOW = new Date("2026-10-27T08:00:00.000Z");

beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(NOW);
    window.history.replaceState(null, "", "/");
});
afterEach(() => vi.useRealTimers());

const asApi = (api: object) => api as unknown as ApiClient;

const running = (
    states: Partial<Record<string, "pass" | "warn" | "fail" | "running">>,
    extra: Partial<ModelTestRun> = {},
) =>
    run({
        status: "running",
        finishedAt: null,
        jobs: [],
        startedAt: NOW.toISOString(),
        checks: checks("waiting").map((c) => ({
            ...c,
            state: states[c.id] ?? c.state,
            ms: states[c.id] && states[c.id] !== "running" ? 900 : null,
            reason:
                c.id === "temperature" && states[c.id] === "warn"
                    ? "This model needs its default temperature; we'll leave it out."
                    : null,
        })),
        ...extra,
    });

describe("Add a model", () => {
    const form = (data: AiModels = aiModels(), overrides: Record<string, unknown> = {}) => {
        const api = { aiModels: vi.fn(async () => data), addAiModel: vi.fn(async () => data), ...overrides };
        render(<ModelTestScreen api={asApi(api)} modelId={null} />);
        return api;
    };

    test("the form, as in the mockup: two fields, the help, the cost note with the runs left, and Add and test", async () => {
        form();

        expect(screen.getByRole("heading", { level: 1, name: "Add a model" })).toBeInTheDocument();
        expect(screen.getByRole("link", { name: "Back to AI models" })).toBeInTheDocument();
        expect(screen.getByLabelText("Name to show")).toHaveAttribute("placeholder", "e.g. GPT-6 Luna");
        const deployment = screen.getByLabelText("Deployment name");
        expect(deployment).toHaveAttribute("autocapitalize", "off");
        expect(deployment).toHaveAttribute("spellcheck", "false");
        expect(deployment).toHaveAccessibleDescription(
            /It must already be deployed. In Foundry, open Deployments and copy the Name column./,
        );
        expect(await screen.findByText(/You have 4 of 5 test runs left this hour./)).toHaveTextContent(
            "We'll run 10 quick checks with made-up stock (no household data). A run takes about a minute and costs about 5 to 10p.",
        );
        expect(screen.getByRole("button", { name: "Add and test" })).toBeEnabled();
    });

    test("both fields are needed", async () => {
        const api = form();
        fireEvent.click(screen.getByRole("button", { name: "Add and test" }));
        expect(await screen.findByText("Give it a name to show.")).toBeInTheDocument();

        fireEvent.change(screen.getByLabelText("Name to show"), { target: { value: "Luna" } });
        fireEvent.click(screen.getByRole("button", { name: "Add and test" }));
        expect(await screen.findByText("Enter the deployment name from Foundry.")).toBeInTheDocument();
        expect(api.addAiModel).not.toHaveBeenCalled();
    });

    test("a deployment that's already added is refused without asking the API", async () => {
        const api = form();
        await screen.findByText(/test runs left/);
        fireEvent.change(screen.getByLabelText("Name to show"), { target: { value: "Another" } });
        fireEvent.change(screen.getByLabelText("Deployment name"), { target: { value: "GPT-4O" } });
        fireEvent.click(screen.getByRole("button", { name: "Add and test" }));

        expect(await screen.findByRole("alert")).toHaveTextContent("That deployment is already added.");
        expect(screen.getByLabelText("Deployment name")).toHaveAttribute("aria-invalid", "true");
        expect(api.addAiModel).not.toHaveBeenCalled();
    });

    test("Add and test registers the model by deployment name and opens its test", async () => {
        const api = form();
        fireEvent.change(screen.getByLabelText("Name to show"), { target: { value: " GPT-6 Luna " } });
        fireEvent.change(screen.getByLabelText("Deployment name"), { target: { value: " gpt-6-luna-2 " } });
        fireEvent.click(screen.getByRole("button", { name: "Add and test" }));

        await waitFor(() => expect(api.addAiModel).toHaveBeenCalledTimes(1));
        const [id, label, deployment] = api.addAiModel.mock.calls[0] as unknown as [string, string, string];
        expect(id).toMatch(/^gpt-6-luna-[a-z0-9]{4}$/);
        expect([label, deployment]).toEqual(["GPT-6 Luna", "gpt-6-luna-2"]);
        expect(window.location.pathname).toBe(`/settings/ai-models/${id}/test`);
    });

    test("the API's own refusal is shown: a duplicate, or a bad name", async () => {
        const api = form(undefined, {
            addAiModel: vi
                .fn()
                .mockRejectedValueOnce(
                    new ApiError(409, "x", { error: "That deployment is already registered as GPT-4o." }),
                )
                .mockRejectedValueOnce(
                    new ApiError(400, "x", { error: "The deployment name must be 1 to 64 characters." }),
                ),
        });
        fireEvent.change(screen.getByLabelText("Name to show"), { target: { value: "X" } });
        fireEvent.change(screen.getByLabelText("Deployment name"), { target: { value: "x" } });
        fireEvent.click(screen.getByRole("button", { name: "Add and test" }));
        expect(await screen.findByRole("alert")).toHaveTextContent("That deployment is already added.");

        fireEvent.click(screen.getByRole("button", { name: "Add and test" }));
        await waitFor(() =>
            expect(screen.getByRole("alert")).toHaveTextContent(
                "The deployment name must be 1 to 64 characters.",
            ),
        );
        expect(api.addAiModel).toHaveBeenCalledTimes(2);
    });

    test("with no test runs left, it says so and can't be submitted", async () => {
        form(aiModels({ testRuns: { perHour: 5, left: 0 } }));

        expect(await screen.findByRole("alert")).toHaveTextContent("Test runs are limited to 5 an hour.");
        expect(screen.getByRole("button", { name: "Add and test" })).toBeDisabled();
    });
});

describe("Testing a model", () => {
    /** A fake API whose run advances through `snapshots`, one per poll. */
    const testing = (
        snapshots: ModelTestRun[],
        data: AiModels = aiModels(),
        overrides: Record<string, unknown> = {},
    ) => {
        let poll = 0;
        const api = {
            aiModels: vi.fn(async () => data),
            startModelTest: vi.fn(async () => snapshots[0]),
            modelTestRun: vi.fn(async () => snapshots[Math.min(++poll, snapshots.length - 1)]),
            ...overrides,
        };
        render(<ModelTestScreen api={asApi(api)} modelId={snapshots[0].modelId} pollMs={10} />);
        return api;
    };

    test("starts a run, shows each check as it lands, then the summary with the jobs it passed (the luna case)", async () => {
        let current = running({});
        const api = testing([current], aiModels(), { modelTestRun: vi.fn(async () => current) });

        expect(
            await screen.findByRole("heading", { level: 1, name: "Testing GPT-6 Luna" }),
        ).toBeInTheDocument();
        expect(api.startModelTest).toHaveBeenCalledWith("luna");
        expect(await screen.findByText("0 of 10 done")).toBeInTheDocument();

        current = running({
            reachable: "pass",
            temperature: "warn",
            strictSchema: "pass",
            recipe: "running",
        });
        expect(await screen.findByText("3 of 10 done")).toBeInTheDocument();
        expect(screen.getByText("Answered.")).toBeInTheDocument();
        expect(
            screen.getByText("This model needs its default temperature; we'll leave it out."),
        ).toBeInTheDocument();
        expect(screen.getByText("Running…")).toBeInTheDocument();
        expect(screen.getAllByText("Waiting")).toHaveLength(6);

        current = luna.tests!;
        expect(await screen.findByText("Ready for recipes and generic names")).toBeInTheDocument();
        expect(screen.getByRole("heading", { level: 1, name: "GPT-6 Luna" })).toBeInTheDocument();
        const summary = screen.getByText("Ready for recipes and generic names").parentElement!;
        expect(summary).toHaveTextContent(
            "Passed with one warning. You can make it the model for recipes and generic names from AI models. It can’t be used for photos.",
        );
        expect(within(summary).getByText(/Photos/)).toHaveStyle({ textDecoration: "line-through" });
        expect(
            screen.getByText(
                /Foundry refused it \(HTTP 400\): invalid_request_error: Image input is not supported\./,
            ),
        ).toBeInTheDocument();
        expect(screen.getByText("10 of 10 done")).toBeInTheDocument();
        expect(screen.getAllByRole("link", { name: "Back to AI models" })).toHaveLength(2); // the header's and the button
        expect(screen.getByRole("button", { name: "Run again" })).toBeInTheDocument();
    });

    test("the run's id goes in the address, so leaving and coming back finds it again", async () => {
        testing([running({})]);
        await screen.findByRole("heading", { level: 1, name: "Testing GPT-6 Luna" });

        expect(window.location.search).toBe("?run=run-1");
    });

    test("a wrong deployment name fails the first check clearly, skips the rest, and says the model can't be used", async () => {
        const api = testing([
            typo.tests!.status === "done" ? { ...typo.tests!, modelId: "typo" } : typo.tests!,
        ]);

        expect(await screen.findByText("This model can’t be used")).toBeInTheDocument();
        expect(
            screen.getByText(
                "No deployment with that name on this Foundry account. Check the Name column in Foundry.",
            ),
        ).toBeInTheDocument();
        expect(screen.getAllByText(/^Skipped/).length).toBeGreaterThanOrEqual(1);
        expect(
            screen.getByText(/It’s saved so you can check the name and run the tests again, or remove it./),
        ).toBeInTheDocument();
        expect(api.startModelTest).toHaveBeenCalledWith("typo");
    });

    test("every check is named, in order, with its time", async () => {
        testing([gpt4o.tests!]);
        const list = await screen.findByRole("list");

        const names = within(list)
            .getAllByRole("listitem")
            .map((li) => li.querySelector("span[class*=checkName]")?.firstChild?.textContent);
        expect(names).toEqual([
            "Reachable",
            "Temperature",
            "Strict JSON",
            "A recipe (half & half)",
            "Ready now ideas",
            "An adjustment",
            "A photo",
            "Generic names",
            "Speed",
            "Cost",
        ]);
        expect(
            within(screen.getByRole("heading", { name: "GPT-4o" }).parentElement!).getByText(/tested/),
        ).toBeTruthy();
    });

    test("over the limit, it says when to try again and offers no re-run", async () => {
        testing([running({})], aiModels(), {
            startModelTest: vi.fn(async () => {
                throw new ApiError(429, "x", { error: "…", retryAt: "2026-10-27T13:05:00.000Z" });
            }),
        });

        expect(await screen.findByRole("alert")).toHaveTextContent(
            "Test runs are limited to 5 an hour. Try again at 13:05.",
        );
        expect(screen.queryByRole("button", { name: "Run again" })).toBeNull();
        expect(screen.getAllByRole("link", { name: "Back to AI models" })).toHaveLength(2);
    });

    test("a model already being tested is followed, not started again", async () => {
        const data = aiModels({ models: [gpt4o, { ...luna, testing: "run-9" }] });
        const api = testing([running({}, { runId: "run-9" })], data);

        await screen.findByRole("heading", { level: 1, name: "Testing GPT-6 Luna" });
        expect(api.modelTestRun).toHaveBeenCalledWith("run-9");
        expect(api.startModelTest).not.toHaveBeenCalled();
    });

    test("an address with ?run= follows that run; once it has expired the saved results show instead", async () => {
        window.history.replaceState(null, "", "/settings/ai-models/luna/test?run=old");
        const api = testing([running({})], aiModels(), {
            modelTestRun: vi.fn(async () => {
                throw new ApiError(404, "x");
            }),
        });

        expect(await screen.findByText("Ready for recipes and generic names")).toBeInTheDocument();
        expect(api.startModelTest).not.toHaveBeenCalled(); // the saved results were enough
    });

    test("with no saved results either, an expired run starts afresh", async () => {
        window.history.replaceState(null, "", "/settings/ai-models/luna/test?run=old");
        const api = testing([running({})], aiModels({ models: [{ ...luna, tests: null }] }), {
            modelTestRun: vi.fn(async (id: string) => {
                if (id === "old") throw new ApiError(404, "x");
                return running({});
            }),
        });

        await screen.findByRole("heading", { level: 1, name: "Testing GPT-6 Luna" });
        await waitFor(() => expect(api.startModelTest).toHaveBeenCalledWith("luna"));
    });

    test("Run again starts another run", async () => {
        const api = testing([luna.tests!]);
        fireEvent.click(await screen.findByRole("button", { name: "Run again" }));

        await waitFor(() => expect(api.startModelTest).toHaveBeenCalledTimes(2));
    });

    test("a model that's gone says so", async () => {
        const api = { aiModels: vi.fn(async () => aiModels({ models: [gpt4o] })), startModelTest: vi.fn() };
        render(<ModelTestScreen api={asApi(api)} modelId="luna" pollMs={10} />);

        expect(await screen.findByRole("alert")).toHaveTextContent("That model isn't there any more.");
        expect(api.startModelTest).not.toHaveBeenCalled();
    });

    test("if following the run keeps failing, it says so and can try again", async () => {
        const api = testing([running({})], aiModels(), {
            modelTestRun: vi.fn(async () => {
                throw new Error("offline");
            }),
        });

        expect(await screen.findByText("Couldn't follow the tests.")).toBeInTheDocument();
        expect(api.modelTestRun).toHaveBeenCalledTimes(3);
        fireEvent.click(screen.getByRole("button", { name: "Try again" }));
        await waitFor(() => expect(api.aiModels).toHaveBeenCalledTimes(2));
    });

    test("a run that stopped unexpectedly says so and can be run again", async () => {
        testing([
            run({
                status: "failed",
                jobs: [],
                checks: checks("waiting"),
                error: "The run stopped unexpectedly.",
            }),
        ]);

        expect(await screen.findByText("The test run stopped")).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Run again" })).toBeInTheDocument();
    });
});
