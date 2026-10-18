/** The longest edge sent for recognition (#58, spike #41): enough for gpt-4o's high detail, and well under 1 MB. */
export const MAX_EDGE = 1024;
export const MAX_BYTES = 1024 * 1024;

/** Turns a camera photo into a JPEG of at most 1024 px on the long edge (upright, as the phone took it). */
export async function toJpeg(file: Blob, maxEdge = MAX_EDGE): Promise<Blob> {
    const bitmap = await createImageBitmap(file, { imageOrientation: "from-image" });
    const scale = Math.min(1, maxEdge / Math.max(bitmap.width, bitmap.height));
    const canvas = document.createElement("canvas");
    canvas.width = Math.max(1, Math.round(bitmap.width * scale));
    canvas.height = Math.max(1, Math.round(bitmap.height * scale));
    const context = canvas.getContext("2d");
    if (!context) throw new Error("No canvas");
    context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    bitmap.close();
    for (const quality of [0.8, 0.6, 0.4]) {
        const blob = await new Promise<Blob | null>((resolve) =>
            canvas.toBlob(resolve, "image/jpeg", quality),
        );
        if (blob && blob.size <= MAX_BYTES) return blob;
    }
    throw new Error("The photo is too big even after resizing.");
}
