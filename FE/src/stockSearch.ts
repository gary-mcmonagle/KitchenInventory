import { aisleOf } from "./aisles";
import type { StockProduct } from "./api";

/** Lower case, accents stripped, "&" as a space, single spaces (#91). */
export function normalise(text: string): string {
    return text
        .normalize("NFD")
        .replace(/[̀-ͯ]/g, "")
        .toLowerCase()
        .replace(/&/g, " ")
        .replace(/\s+/g, " ")
        .trim();
}

/** The query's words; none means no search. */
export const searchWords = (query: string): string[] => normalise(query).split(" ").filter(Boolean);

/**
 * The stock search (#91): a product matches when **every** word is part of its display name, brand, generic name or
 * aisle name, or of its barcode's digits. Case and accents don't matter, and part of a word is enough.
 */
export function matchesSearch(product: StockProduct, words: string[]): boolean {
    if (words.length === 0) return true;
    const fields = [product.displayName, product.brand, product.genericName, aisleOf(product.aisleId).name]
        .filter((f): f is string => !!f)
        .map(normalise);
    const digits = (product.barcode ?? "").replace(/\D/g, "");
    return words.every((word) => {
        const wordDigits = word.replace(/\s/g, "");
        return (
            fields.some((f) => f.includes(word)) || (/^\d+$/.test(wordDigits) && digits.includes(wordDigits))
        );
    });
}
