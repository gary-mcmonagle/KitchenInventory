import { EventType, type AuthenticationResult } from "@azure/msal-browser";
import { MsalProvider } from "@azure/msal-react";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import { createMsal } from "./auth";
import { readConfig } from "./config";
import { registerServiceWorker } from "./pwa";
// Figtree, self-hosted so the installed app has it offline (#85, spike #83): latin, the weights the mockups use.
import "@fontsource/figtree/latin-400.css";
import "@fontsource/figtree/latin-500.css";
import "@fontsource/figtree/latin-600.css";
import "@fontsource/figtree/latin-700.css";
import "./tokens.css";

const config = readConfig();
const msal = createMsal(config);

await msal.initialize();
// Finishes a sign-in redirect (and returns the user to the page they started on).
const result = await msal.handleRedirectPromise();
msal.setActiveAccount(result?.account ?? msal.getActiveAccount() ?? msal.getAllAccounts()[0] ?? null);
msal.addEventCallback((event) => {
    if (event.eventType === EventType.LOGIN_SUCCESS && event.payload) {
        msal.setActiveAccount((event.payload as AuthenticationResult).account);
    }
});

createRoot(document.getElementById("root")!).render(
    <StrictMode>
        <MsalProvider instance={msal}>
            <App config={config} />
        </MsalProvider>
    </StrictMode>,
);

registerServiceWorker();
