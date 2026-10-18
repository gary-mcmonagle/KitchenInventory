import { aisleOf } from "./aisles";
import { initial } from "./format";
import icons from "./productIcons.json";

const SET = icons as { width: number; height: number; icons: Record<string, string> };

/** The curated icon names a product can have (#72), for the picker. */
export const ICON_NAMES = Object.keys(SET.icons);

/** An icon from the bundled IconPark Outline set, drawn in the current text colour (the tile's). */
export function Icon({ name, size = 26 }: { name: string; size?: number }) {
    const body = SET.icons[name];
    if (!body) return null;
    return (
        <svg
            width={size}
            height={size}
            viewBox={`0 0 ${SET.width} ${SET.height}`}
            aria-hidden="true"
            // The bodies are our own bundled files (FE/scripts/build-icons.mjs), never user input.
            dangerouslySetInnerHTML={{ __html: body }}
        />
    );
}

/**
 * A product's picture (#72): its icon; else its aisle's icon (#71) once it has a real aisle; else its initial letter.
 * Keeps the caller's tile styling (44 px, 12 px radius, accent on tint).
 */
export function ProductTile({
    className,
    name,
    icon,
    aisleId,
}: {
    className: string;
    name: string;
    icon: string | null | undefined;
    aisleId: string | null | undefined;
}) {
    const shown =
        icon && SET.icons[icon] ? icon : aisleId && aisleId !== "other" ? aisleOf(aisleId).icon : null;
    return (
        <div className={className} aria-hidden="true">
            {shown ? <Icon name={shown} /> : initial(name)}
        </div>
    );
}
