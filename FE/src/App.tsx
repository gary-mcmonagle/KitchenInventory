import { AuthenticatedTemplate, UnauthenticatedTemplate, useMsal } from "@azure/msal-react";
import type { PublicClientApplication } from "@azure/msal-browser";
import { useMemo } from "react";
import { ApiClient } from "./api";
import { signIn } from "./auth";
import type { AppConfig } from "./config";
import { Shell } from "./Shell";
import { SignIn } from "./SignIn";

export function App({ config }: { config: AppConfig }) {
    const { instance } = useMsal();
    const api = useMemo(() => new ApiClient(instance, config), [instance, config]);
    return (
        <>
            <AuthenticatedTemplate>
                <Shell api={api} buildSha={config.buildSha} />
            </AuthenticatedTemplate>
            <UnauthenticatedTemplate>
                <SignIn onSignIn={() => void signIn(instance as PublicClientApplication, config)} />
            </UnauthenticatedTemplate>
        </>
    );
}
