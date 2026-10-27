import type { AiModel, AiModels, CheckState, ModelCheck, ModelTestRun } from "./api";

/** Test data for the AI models screens (#123): illustrative names, costs and dates, as in the mockups. */

export const CHECK_IDS = [
    "reachable",
    "temperature",
    "strictSchema",
    "recipe",
    "readyNow",
    "adjust",
    "photo",
    "genericNames",
    "speed",
    "cost",
] as const;

export const check = (
    id: string,
    state: CheckState,
    reason: string | null = null,
    ms: number | null = null,
): ModelCheck => ({
    id,
    state,
    reason,
    ms,
    promptTokens: null,
    completionTokens: null,
});

/** Every check at `state`, with some overridden. */
export const checks = (
    state: CheckState,
    overrides: Record<string, Partial<ModelCheck>> = {},
): ModelCheck[] => CHECK_IDS.map((id) => ({ ...check(id, state), ...overrides[id] }));

export const run = (overrides: Partial<ModelTestRun> = {}): ModelTestRun => ({
    runId: "run-1",
    modelId: "luna",
    deployment: "gpt-6-luna",
    startedBy: "u1",
    startedByName: "Gary",
    startedAt: "2026-10-24T09:00:00.000Z",
    finishedAt: "2026-10-24T09:01:00.000Z",
    status: "done",
    checks: checks("pass"),
    jobs: ["recipes", "photos", "genericNames"],
    temperatureMode: "usual",
    modelVersion: null,
    costs: null,
    totalMs: 60000,
    error: null,
    ...overrides,
});

export const gpt4o: AiModel = {
    id: "gpt-4o",
    label: "GPT-4o",
    deployment: "gpt-4o",
    jobs: ["recipes", "photos", "genericNames"],
    addedBy: "u1",
    addedByName: "Gary",
    addedAt: "2026-10-20T09:00:00.000Z",
    tests: run({
        modelId: "gpt-4o",
        deployment: "gpt-4o",
        finishedAt: "2026-10-24T09:01:00.000Z",
        costs: { perRecipe: 0.004, perReadyNow: 0.002, perPhoto: 0.002, perRun: 0.02, basis: "x" },
    }),
    testing: null,
};

export const luna: AiModel = {
    id: "luna",
    label: "GPT-6 Luna",
    deployment: "gpt-6-luna",
    jobs: ["recipes", "genericNames"],
    addedBy: "u1",
    addedByName: "Gary",
    addedAt: "2026-10-24T09:00:00.000Z",
    tests: run({
        jobs: ["recipes", "genericNames"],
        checks: checks("pass", {
            temperature: {
                state: "warn",
                reason: "This model needs its default temperature; we'll leave it out.",
            },
            photo: {
                state: "fail",
                reason: "Foundry refused it (HTTP 400): invalid_request_error: Image input is not supported.",
            },
        }),
        costs: { perRecipe: 0.006, perReadyNow: 0.003, perPhoto: null, perRun: 0.03, basis: "y" },
    }),
    testing: null,
};

export const typo: AiModel = {
    id: "typo",
    label: "Mini",
    deployment: "gpt-5-mnii",
    jobs: [],
    addedBy: "u2",
    addedByName: "Sam",
    addedAt: "2026-10-26T09:00:00.000Z",
    tests: run({
        modelId: "typo",
        deployment: "gpt-5-mnii",
        finishedAt: "2026-10-26T09:01:00.000Z",
        jobs: [],
        checks: checks("fail", {
            reachable: { reason: "No deployment with that name on this Foundry account." },
            temperature: { reason: "Skipped: the model wasn't reachable." },
        }),
    }),
    testing: null,
};

export const aiModels = (overrides: Partial<AiModels> = {}): AiModels => ({
    models: [gpt4o, luna, typo],
    active: {
        recipes: {
            modelId: "luna",
            changedBy: "u1",
            changedByName: "Gary",
            changedAt: "2026-10-24T10:00:00.000Z",
            previousModelId: "gpt-4o",
        },
        photos: null,
        genericNames: null,
    },
    defaults: { recipes: "gpt-4o", photos: "gpt-4o", genericNames: "gpt-4o" },
    testRuns: { perHour: 5, left: 4 },
    ...overrides,
});
