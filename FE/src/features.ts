/** Feature toggles, read at build time from VITE_FEATURE_* (like the rest of the build config). On unless set to "false". */

/** Scan in and out with the phone camera (#133): `VITE_FEATURE_CAMERA_SCAN`. Unset means on; only `false` turns it off. */
export const cameraScanEnabled = (env: Record<string, unknown> = import.meta.env): boolean =>
    String(env.VITE_FEATURE_CAMERA_SCAN ?? "")
        .trim()
        .toLowerCase() !== "false";
