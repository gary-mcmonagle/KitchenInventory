import { render, screen } from "@testing-library/react";
import type { ApiClient } from "./api";
import { Shell } from "./Shell";
import { SignIn } from "./SignIn";

const api = {
    me: async () => ({ name: "Cupboard test", objectId: "o" }),
    ping: async () => ({ status: "ok", commit: "abc1234" }),
    stock: async () => ({ generatedAt: "", totalUnits: 0, products: [] }),
} as unknown as ApiClient;

test("the shell shows the stock list and a footer with the user, API and UI versions", async () => {
    render(<Shell api={api} buildSha="def5678" />);

    expect(screen.getByRole("heading", { level: 1, name: "Cupboard" })).toBeInTheDocument();
    expect(
        await screen.findByText("Signed in as Cupboard test · API abc1234 · UI def5678"),
    ).toBeInTheDocument();
    expect(await screen.findByText(/Nothing in stock yet/)).toBeInTheDocument();
});

test("the sign-in button starts sign-in", () => {
    const onSignIn = vi.fn();
    render(<SignIn onSignIn={onSignIn} />);

    screen.getByRole("button", { name: "Sign in" }).click();
    expect(onSignIn).toHaveBeenCalled();
});
