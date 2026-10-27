import type { AiJob, AiModel, AiModels, ModelCheck, ModelCosts, ModelTestRun } from "./api";
import { boughtDate } from "./format";

/** AI models (#123, project/AIModels.dc.html): wording and small calculations shared by Settings, the AI models page and
 * the test screen. Nothing here mentions temperature or where a model runs. */

export const JOBS: readonly AiJob[] = ["recipes", "photos", "genericNames"];

/** The job's name as a heading ("Recipes and ideas") and inside a sentence ("recipes"). */
export const JOB_TITLE: Record<AiJob, string> = {
    recipes: "Recipes and ideas",
    photos: "Photos",
    genericNames: "Generic names",
};
export const JOB_WORDS: Record<AiJob, string> = {
    recipes: "recipes",
    photos: "photos",
    genericNames: "generic names",
};

/** "recipes and generic names". */
export function listWords(jobs: readonly AiJob[]): string {
    const words = jobs.map((j) => JOB_WORDS[j]);
    return words.length <= 1
        ? (words[0] ?? "")
        : `${words.slice(0, -1).join(", ")} and ${words[words.length - 1]}`;
}

/** The ten checks, in the order a run reports them. */
export const CHECK_NAMES: Record<string, string> = {
    reachable: "Reachable",
    temperature: "Temperature",
    strictSchema: "Strict JSON",
    recipe: "A recipe (half & half)",
    readyNow: "Ready now ideas",
    adjust: "An adjustment",
    photo: "A photo",
    genericNames: "Generic names",
    speed: "Speed",
    cost: "Cost",
};

/** What a passing check says, when the API has no reason of its own. */
const PASS_TEXT: Record<string, string> = {
    reachable: "Answered.",
    temperature: "Accepts the temperature we send.",
    strictSchema: "Valid JSON for our schema.",
    recipe: "A valid recipe: equipment and half & half checks passed.",
    readyNow: "Valid ideas.",
    adjust: "Added what was asked; still a valid recipe.",
    photo: "Read the test pack.",
    genericNames: "Wrote a valid name.",
};

/** A check that didn't run because an earlier one failed. */
export const isSkipped = (c: ModelCheck) => c.state === "fail" && (c.reason ?? "").startsWith("Skipped");

/** The line under a check's name. */
export function checkText(c: ModelCheck): string {
    if (c.state === "waiting") return "Waiting";
    if (c.state === "running") return "Running…";
    if (isSkipped(c)) return c.reason ?? "Skipped.";
    if (c.state === "pass" || c.state === "warn") return c.reason ?? PASS_TEXT[c.id] ?? "Passed.";
    const reason = c.reason ?? "It didn't work.";
    return /^No deployment with that name/.test(reason)
        ? `${reason} Check the Name column in Foundry.`
        : reason;
}

/** "0.9 s", or nothing while a check hasn't finished. */
export function checkTime(c: ModelCheck): string {
    return c.ms === null || c.state === "waiting" || c.state === "running" || isSkipped(c) || c.id === "cost"
        ? ""
        : `${(c.ms / 1000).toFixed(1)} s`;
}

export const checksDone = (run: ModelTestRun) =>
    run.checks.filter((c) => c.state !== "waiting" && c.state !== "running").length;

/** Pounds as pence under a pound ("0.4p"), else "£1.50"; null: unknown. */
export function pence(pounds: number | null | undefined): string | null {
    if (pounds === null || pounds === undefined) return null;
    return pounds >= 1 ? `£${pounds.toFixed(2)}` : `${(pounds * 100).toFixed(1)}p`;
}

/** The estimated cost for one use of the job, from a run's costs; null for generic names (not estimated) or unknown. */
export function jobCost(costs: ModelCosts | null | undefined, job: AiJob): number | null {
    if (!costs) return null;
    return job === "recipes" ? costs.perRecipe : job === "photos" ? costs.perPhoto : null;
}

const PER: Record<AiJob, string> = { recipes: "a recipe", photos: "a photo", genericNames: "a name" };

/** "about 0.4p a recipe", or "unknown". */
export function costPhrase(model: AiModel | undefined, job: AiJob): string {
    const p = pence(jobCost(model?.tests?.costs, job));
    if (p) return `about ${p} ${PER[job]}`;
    return job === "genericNames" ? "not estimated" : "unknown";
}

/** Where a job's active model comes from: the one chosen, or the default deployment (matched to a registered model if any). */
export function activeModel(
    data: AiModels,
    job: AiJob,
): { label: string; deployment: string; model: AiModel | null } {
    const active = data.active[job];
    const chosen = active ? data.models.find((m) => m.id === active.modelId) : undefined;
    if (chosen) return { label: chosen.label, deployment: chosen.deployment, model: chosen };
    return defaultModel(data, job);
}

export function defaultModel(
    data: AiModels,
    job: AiJob,
): { label: string; deployment: string; model: AiModel | null } {
    const deployment = data.defaults[job];
    const match = data.models.find((m) => m.deployment.toLowerCase() === deployment.toLowerCase());
    return { label: match?.label ?? deployment, deployment, model: match ?? null };
}

/** "Changed by Gary · 24 Oct", or "Since setup". */
export function changedLine(data: AiModels, job: AiJob): string {
    const a = data.active[job];
    return a ? `Changed by ${a.changedByName} · ${boughtDate(a.changedAt)}` : "Since setup";
}

/** Who "Go back to" goes to: the previous model, or the default if the job was on it. Null: nothing to go back to. */
export function previousLabel(data: AiModels, job: AiJob): string | null {
    const a = data.active[job];
    if (!a) return null;
    const previous = a.previousModelId ? data.models.find((m) => m.id === a.previousModelId) : undefined;
    return previous?.label ?? defaultModel(data, job).label;
}

export type Tone = "ok" | "warn" | "fail" | "none";

/** The pill on a model's card. */
export function modelStatus(model: AiModel): { text: string; tone: Tone } {
    if (model.testing) return { text: "Testing…", tone: "none" };
    const t = model.tests;
    if (!t || t.status !== "done") return { text: "Not tested", tone: "none" };
    if (t.jobs.length === 0) return { text: "Failed", tone: "fail" };
    const warnings = t.checks.filter((c) => c.state === "warn").length;
    if (t.jobs.length === JOBS.length && warnings === 0) return { text: "All passed", tone: "ok" };
    return warnings > 0
        ? { text: `Passed, ${warnings} ${warnings === 1 ? "warning" : "warnings"}`, tone: "warn" }
        : { text: "Passed for some jobs", tone: "warn" };
}

/** The one-line note on a model's card: its cost, what it isn't for, or why it failed. */
export function modelNote(model: AiModel): string {
    if (model.testing) return "Tests are running.";
    const t = model.tests;
    if (!t || t.status !== "done") return "Run the tests to see which jobs it can do.";
    if (t.jobs.length === 0) {
        const failed = t.checks.find((c) => c.state === "fail" && !isSkipped(c));
        return failed ? checkText(failed) : "It didn't pass the tests.";
    }
    const parts: string[] = [];
    const cost = pence(t.costs?.perRecipe);
    if (cost) parts.push(`About ${cost} a recipe`);
    const notFor = JOBS.filter((j) => !t.jobs.includes(j));
    if (notFor.length > 0) parts.push(`Not for ${listWords(notFor)}`);
    return parts.length > 0 ? parts.join(" · ") : "Passed.";
}

/** "Added by Gary, 24 Oct · tested 24 Oct". */
export function modelMeta(model: AiModel): string {
    const added = `Added by ${model.addedByName}, ${boughtDate(model.addedAt)}`;
    const tested = model.tests?.finishedAt ? ` · tested ${boughtDate(model.tests.finishedAt)}` : "";
    return added + tested;
}

/** A model id for a new model: its name as a slug and a short random tail (the API wants 1 to 40 of A–Z a–z 0–9 - _). */
export function newModelId(label: string, random: () => number = Math.random): string {
    const slug =
        label
            .toLowerCase()
            .replace(/[^a-z0-9]+/g, "-")
            .replace(/^-+|-+$/g, "")
            .slice(0, 28) || "model";
    const tail = Math.floor(random() * 36 ** 4)
        .toString(36)
        .padStart(4, "0");
    return `${slug}-${tail}`;
}

/** "Test runs are limited to 5 an hour. Try again at 14:05." (UK time) */
export function limitMessage(perHour: number, retryAt: string | undefined): string {
    const time = retryAt
        ? new Date(retryAt).toLocaleTimeString("en-GB", {
              hour: "2-digit",
              minute: "2-digit",
              timeZone: "Europe/London",
          })
        : null;
    return `Test runs are limited to ${perHour} an hour.${time ? ` Try again at ${time}.` : ""}`;
}
