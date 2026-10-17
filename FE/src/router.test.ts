import { matchNamePath, namePath, navigate } from "./router";

test("naming paths", () => {
    expect(namePath("abc")).toBe("/products/abc/name");
    expect(matchNamePath("/products/abc/name")).toBe("abc");
    expect(matchNamePath("/products/abc/name/")).toBe("abc");
    expect(matchNamePath("/")).toBeNull();
    expect(matchNamePath("/products/abc")).toBeNull();
});

test("navigate pushes history and tells listeners", () => {
    const heard = vi.fn();
    window.addEventListener("popstate", heard);
    navigate("/products/x/name");
    expect(window.location.pathname).toBe("/products/x/name");
    expect(heard).toHaveBeenCalledTimes(1);
    navigate("/products/x/name"); // same place: nothing
    expect(heard).toHaveBeenCalledTimes(1);
    window.removeEventListener("popstate", heard);
    navigate("/");
});
