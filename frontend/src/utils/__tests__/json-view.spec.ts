import { describe, it, expect } from "vitest";
import { parseJsonString, unwrapJsonString } from "../json-view";

describe("parseJsonString", () => {
  it("parses a string holding a JSON object", () => {
    expect(parseJsonString('{"name":"Mono-Black Zombie Swarm","totalCards":60}')).toEqual({
      name: "Mono-Black Zombie Swarm",
      totalCards: 60,
    });
  });

  it("parses a string holding a JSON array, leading whitespace included", () => {
    expect(parseJsonString('  [1, 2, 3]')).toEqual([1, 2, 3]);
  });

  it("returns undefined for plain text, scalars and broken JSON", () => {
    expect(parseJsonString("just some text")).toBeUndefined();
    expect(parseJsonString("42")).toBeUndefined();
    expect(parseJsonString('"quoted"')).toBeUndefined();
    expect(parseJsonString('{"unterminated": ')).toBeUndefined();
    expect(parseJsonString(undefined)).toBeUndefined();
  });

  it("returns undefined for values that are not strings", () => {
    expect(parseJsonString({ a: 1 })).toBeUndefined();
    expect(parseJsonString(null)).toBeUndefined();
  });
});

describe("unwrapJsonString", () => {
  it("unwraps encoded JSON", () => {
    expect(unwrapJsonString('{"a":[1,2]}')).toEqual({ a: [1, 2] });
  });

  it("passes everything else through untouched", () => {
    const obj = { a: 1 };
    expect(unwrapJsonString(obj)).toBe(obj);
    expect(unwrapJsonString("plain text")).toBe("plain text");
    expect(unwrapJsonString(null)).toBe(null);
  });
});
