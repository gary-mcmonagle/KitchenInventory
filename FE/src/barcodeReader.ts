/**
 * Reads barcodes from the phone camera (#133). The native `BarcodeDetector` where the browser has one (Android Chrome),
 * otherwise ZXing (`@zxing/library`, loaded only when the scanner opens: iPhone Safari). Formats: EAN-13, EAN-8, UPC-A, UPC-E
 * (UPC-E is expanded to the 12-digit UPC-A the API checks). Frames stay on the phone: only the decoded number leaves it.
 */

export class CameraUnavailableError extends Error {
    constructor(message = "The camera isn't available.") {
        super(message);
        this.name = "CameraUnavailableError";
    }
}

export interface ReaderHandle {
    stop: () => void;
    torchSupported: boolean;
    setTorch: (on: boolean) => Promise<void>;
}

/** Starts the camera in `video` and calls `onRead` with each barcode seen (continuously, so the caller de-duplicates). */
export type StartReader = (video: HTMLVideoElement, onRead: (code: string) => void) => Promise<ReaderHandle>;

const POLL_MS = 150;

/** UPC-E (8 digits) as the 12-digit UPC-A it stands for; anything else is returned as it is. */
export function expandUpcE(code: string): string {
    if (!/^[01]\d{7}$/.test(code)) return code;
    const [system, a, b, c, d, e, f] = code.slice(0, 7);
    const check = code[7];
    const manufacturerAndProduct = "012".includes(f)
        ? `${a}${b}${f}00` + `00${c}${d}${e}`
        : f === "3"
          ? `${a}${b}${c}00` + `000${d}${e}`
          : f === "4"
            ? `${a}${b}${c}${d}0` + `0000${e}`
            : `${a}${b}${c}${d}${e}` + `0000${f}`;
    return `${system}${manufacturerAndProduct}${check}`;
}

interface NativeDetector {
    detect: (source: HTMLVideoElement) => Promise<Array<{ rawValue: string; format?: string }>>;
}

const nativeDetector = (): NativeDetector | null => {
    const Detector = (
        window as unknown as { BarcodeDetector?: new (o: { formats: string[] }) => NativeDetector }
    ).BarcodeDetector;
    return Detector ? new Detector({ formats: ["ean_13", "ean_8", "upc_a", "upc_e"] }) : null;
};

async function zxingDecoder(video: HTMLVideoElement): Promise<() => string | null> {
    const zxing = await import("@zxing/library");
    const reader = new zxing.MultiFormatReader();
    const hints = new Map<unknown, unknown>([
        [
            zxing.DecodeHintType.POSSIBLE_FORMATS,
            [
                zxing.BarcodeFormat.EAN_13,
                zxing.BarcodeFormat.EAN_8,
                zxing.BarcodeFormat.UPC_A,
                zxing.BarcodeFormat.UPC_E,
            ],
        ],
        [zxing.DecodeHintType.TRY_HARDER, true],
    ]);
    reader.setHints(hints as Map<import("@zxing/library").DecodeHintType, unknown>);
    const canvas = document.createElement("canvas");
    return () => {
        if (!video.videoWidth) return null;
        canvas.width = video.videoWidth;
        canvas.height = video.videoHeight;
        const context = canvas.getContext("2d", { willReadFrequently: true });
        if (!context) return null;
        context.drawImage(video, 0, 0, canvas.width, canvas.height);
        try {
            const bitmap = new zxing.BinaryBitmap(
                new zxing.HybridBinarizer(new zxing.HTMLCanvasElementLuminanceSource(canvas)),
            );
            const result = reader.decode(bitmap);
            const text = result.getText();
            return result.getBarcodeFormat() === zxing.BarcodeFormat.UPC_E ? expandUpcE(text) : text;
        } catch {
            return null; // nothing readable in this frame
        }
    };
}

export const startReader: StartReader = async (video, onRead) => {
    if (!navigator.mediaDevices?.getUserMedia) throw new CameraUnavailableError();
    let stream: MediaStream;
    try {
        stream = await navigator.mediaDevices.getUserMedia({
            video: { facingMode: { ideal: "environment" }, width: { ideal: 1280 }, height: { ideal: 720 } },
            audio: false,
        });
    } catch (error) {
        throw new CameraUnavailableError(error instanceof Error ? error.message : undefined);
    }

    video.setAttribute("playsinline", "true");
    video.muted = true;
    video.srcObject = stream;
    await video.play().catch(() => undefined);

    const track = stream.getVideoTracks()[0];
    const capabilities = (track?.getCapabilities?.() ?? {}) as { torch?: boolean };
    const native = nativeDetector();
    const decodeFrame = native ? null : await zxingDecoder(video);

    let busy = false;
    let stopped = false;
    const timer = setInterval(() => {
        if (busy || stopped || video.readyState < 2) return;
        busy = true;
        const done = native
            ? native
                  .detect(video)
                  .then((found) => {
                      const hit = found[0];
                      if (hit) onRead(hit.format === "upc_e" ? expandUpcE(hit.rawValue) : hit.rawValue);
                  })
                  .catch(() => undefined)
            : Promise.resolve().then(() => {
                  const text = decodeFrame!();
                  if (text) onRead(text);
              });
        void done.finally(() => {
            busy = false;
        });
    }, POLL_MS);

    return {
        torchSupported: capabilities.torch === true,
        setTorch: async (on) => {
            await track?.applyConstraints({ advanced: [{ torch: on } as MediaTrackConstraintSet] });
        },
        stop: () => {
            stopped = true;
            clearInterval(timer);
            stream.getTracks().forEach((t) => t.stop());
            video.srcObject = null;
        },
    };
};
