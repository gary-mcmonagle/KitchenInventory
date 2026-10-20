import type { StockProduct } from "./api";

/**
 * What the stock list already knows about a product (#100), kept in memory when a row is tapped, so item detail can
 * show its heading, estimate and Add to list at once while the full product loads. Never stored on the device.
 */
const previews = new Map<string, StockProduct>();

export function rememberItemPreview(product: StockProduct) {
    previews.set(product.productId, product);
}

export const itemPreview = (productId: string): StockProduct | undefined => previews.get(productId);
