import css from "./tokens.css?raw";

// The exact values from the mockups (project/Main.dc.html) and CLAUDE.md.
const expected: Record<string, string> = {
    "--ground": "#f4f7f3",
    "--ink": "#1c2a24",
    "--muted": "#5b6b63",
    "--border": "#d5ded8",
    "--border-soft": "#e1e8e3",
    "--accent": "#2f6b4f",
    "--accent-dark": "#1f5238",
    "--accent-tint": "#e4f0e9",
    "--warn-bg": "#ffe9c7",
    "--warn-fg": "#7a4300",
    "--danger-bg": "#f9e3e1",
    "--danger-fg": "#8e1f17",
    "--radius-input": "14px",
    "--radius-card": "16px",
    "--touch": "44px",
};

test("design tokens match the mockups", () => {
    for (const [name, value] of Object.entries(expected)) {
        expect(css, name).toMatch(new RegExp(`${name}:\\s*${value};`));
    }
    expect(css).toContain('"Figtree"');
});
