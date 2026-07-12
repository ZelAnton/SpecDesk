import { describe, expect, it, vi } from "vitest";
import { DEFAULT_DOCKS_STATE, DOCKS_STORAGE_KEY } from "../../src/workspace/dock-state.js";
import { DockStore } from "../../src/workspace/dock-store.js";

/** A minimal in-memory Storage double (the bits DockStore uses). */
function fakeStorage(seed: Record<string, string> = {}): Storage {
  const map = new Map(Object.entries(seed));
  return {
    getItem: (key: string) => map.get(key) ?? null,
    setItem: (key: string, value: string) => {
      map.set(key, value);
    },
    removeItem: (key: string) => {
      map.delete(key);
    },
    clear: () => map.clear(),
    key: (index: number) => Array.from(map.keys())[index] ?? null,
    get length() {
      return map.size;
    },
  } as Storage;
}

describe("DockStore", () => {
  it("loads defaults when the store is empty", () => {
    expect(new DockStore(fakeStorage()).load()).toEqual(DEFAULT_DOCKS_STATE);
  });

  it("round-trips a saved state", () => {
    const storage = fakeStorage();
    const store = new DockStore(storage);
    const state = {
      left: { open: true, size: 300, mode: "outline" },
      right: { open: false, size: 320, mode: "assistant" },
      bottom: { open: true, size: 200, mode: "log" },
    };
    store.save(state);
    expect(storage.getItem(DOCKS_STORAGE_KEY)).not.toBeNull();
    expect(new DockStore(storage).load()).toEqual(state);
  });

  it("loads defaults from a corrupt stored value", () => {
    const store = new DockStore(fakeStorage({ [DOCKS_STORAGE_KEY]: "{oops" }));
    expect(store.load()).toEqual(DEFAULT_DOCKS_STATE);
  });

  it("with a null store: loads defaults and save is a no-op", () => {
    const store = new DockStore(null);
    expect(store.load()).toEqual(DEFAULT_DOCKS_STATE);
    expect(() => store.save(DEFAULT_DOCKS_STATE)).not.toThrow();
  });

  it("swallows a getItem/setItem that throws (disabled/full storage)", () => {
    const throwing = {
      getItem: vi.fn(() => {
        throw new Error("blocked");
      }),
      setItem: vi.fn(() => {
        throw new Error("quota");
      }),
      removeItem: () => {},
      clear: () => {},
      key: () => null,
      length: 0,
    } as unknown as Storage;
    const store = new DockStore(throwing);
    expect(store.load()).toEqual(DEFAULT_DOCKS_STATE);
    expect(() => store.save(DEFAULT_DOCKS_STATE)).not.toThrow();
  });
});
