/** A message for the next screen to show once (e.g. "Added 3 items" on the stock list after adding, #58). */
let pending: string | null = null;

export function setFlash(message: string): void {
    pending = message;
}

export function takeFlash(): string | null {
    const message = pending;
    pending = null;
    return message;
}
