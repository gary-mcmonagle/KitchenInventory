/** The aisles in shop order (#71): the same list as BE/src/CupboardTracker.Domain/Products/Aisles.cs. Each has its
 * IconPark icon (#72), shown for products whose own icon Jev wasn't sure of. */
export interface AisleInfo {
    id: string;
    name: string;
    icon: string;
}

export const AISLES: AisleInfo[] = [
    { id: "fruitVeg", name: "Fruit & veg", icon: "vegetables" },
    { id: "bakery", name: "Bakery", icon: "bread" },
    { id: "meatFish", name: "Meat & fish", icon: "fish" },
    { id: "dairyEggs", name: "Dairy & eggs", icon: "milk" },
    { id: "chilledDeli", name: "Chilled & deli", icon: "sandwich" },
    { id: "frozen", name: "Frozen", icon: "snowflake" },
    { id: "pastaRiceGrains", name: "Pasta, rice & grains", icon: "rice" },
    { id: "tinsJars", name: "Tins & jars", icon: "canned-fruit" },
    { id: "saucesOils", name: "Sauces, oils & condiments", icon: "bottle-one" },
    { id: "bakingSpices", name: "Baking & spices", icon: "chili" },
    { id: "breakfastCereal", name: "Breakfast & cereal", icon: "bowl" },
    { id: "snacksSweets", name: "Snacks & sweets", icon: "candy" },
    { id: "drinks", name: "Drinks", icon: "tea-drink" },
    { id: "other", name: "Other", icon: "shopping-cart" },
];

export const aisleOf = (id: string | null | undefined): AisleInfo =>
    AISLES.find((a) => a.id === id) ?? AISLES[AISLES.length - 1];
