import { render, screen } from "@testing-library/react";
import { NoAccessError, type ApiClient } from "./api";
import { Shell } from "./Shell";
import { SignIn } from "./SignIn";

const api = (me: () => Promise<unknown>) =>
    ({ me, ping: async () => ({ status: "ok", commit: "abc1234" }) }) as unknown as ApiClient;

test("shows the stock list placeholder and a footer with the user, API and UI versions", async () => {
    render(<Shell api={api(async () => ({ name: "Cupboard test", objectId: "o" }))} buildSha="def5678" />);

    expect(screen.getByRole("heading", { level: 1, name: "Cupboard" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Stock list" })).toBeInTheDocument();
    expect(screen.getByText("Your stock will appear here.")).toBeInTheDocument();
    expect(
        await screen.findByText("Signed in as Cupboard test · API abc1234 · UI def5678"),
    ).toBeInTheDocument();
});

test("a user without access sees a short message", async () => {
    render(
        <Shell
            api={api(async () => {
                throw new NoAccessError("You're signed in, but this account can't use Cupboard Tracker.");
            })}
            buildSha="x"
        />,
    );

    expect(await screen.findByRole("alert")).toHaveTextContent("this account can't use Cupboard Tracker");
});

test("the sign-in button starts sign-in", () => {
    const onSignIn = vi.fn();
    render(<SignIn onSignIn={onSignIn} />);

    screen.getByRole("button", { name: "Sign in" }).click();
    expect(onSignIn).toHaveBeenCalled();
});
