import { describe, it, expect } from "vitest";
import { loadSession } from "./helpers";
import { detectLLMProvider } from "../llm-detection";
import {
  isGeminiInteractionsRequest,
  parseGeminiInteractionsResponse,
  parseGeminiInteractionsRequest,
  geminiInteractionsStepsToParts,
  geminiInteractionsPreviewText,
  geminiInteractionsToolCallNames,
  geminiInteractionsTrailingResults,
} from "../gemini-interactions";

// 6816 first turn: user_input only, requires_action, steps = thought + function_call
// 6825 completed: model_output step, tool loop in input, no tools[] key
// 6827 multi-turn: model_output items in input
// 6848 no tools[] key, long tool loop, function_call with real arguments
const FIRST_TURN = "interactions-6816.json";
const COMPLETED = "interactions-6825.json";
const MULTI_TURN = "interactions-6827.json";
const NO_TOOLS = "interactions-6848.json";

/** parseGeminiInteractionsResponse, asserting it parsed. */
function mustParse(body: string) {
  const r = parseGeminiInteractionsResponse(body);
  expect(r).not.toBeNull();
  return r!;
}

describe("isGeminiInteractionsRequest", () => {
  it("matches every captured interactions request", () => {
    for (const f of [FIRST_TURN, COMPLETED, MULTI_TURN, NO_TOOLS]) {
      expect(isGeminiInteractionsRequest(loadSession(f).requestBody), f).toBe(true);
    }
  });

  it("does not match a classic generateContent request", () => {
    const body = JSON.stringify({
      contents: [{ role: "user", parts: [{ text: "hi" }] }],
      tools: [{ functionDeclarations: [] }],
    });
    expect(isGeminiInteractionsRequest(body)).toBe(false);
  });

  it("does not match malformed or empty bodies", () => {
    expect(isGeminiInteractionsRequest("")).toBe(false);
    expect(isGeminiInteractionsRequest("not json")).toBe(false);
    expect(isGeminiInteractionsRequest("{}")).toBe(false);
  });

  it("is reached via the gemini provider detection", () => {
    expect(detectLLMProvider(loadSession(FIRST_TURN))).toBe("gemini");
  });
});

describe("parseGeminiInteractionsResponse", () => {
  it("maps the usage block onto the normalized token fields", () => {
    const r = mustParse(loadSession(FIRST_TURN).responseBody);
    expect(r.model).toBe("gemini-3.8-flash");
    expect(r.status).toBe("requires_action");
    expect(r.promptTokens).toBe(2051);
    expect(r.responseTokens).toBe(10);
    expect(r.cachedTokens).toBe(0);
    expect(r.thoughtTokens).toBe(47);
  });

  it("keeps total_tokens = input + output + thought across fixtures", () => {
    for (const f of [FIRST_TURN, COMPLETED, MULTI_TURN, NO_TOOLS]) {
      const session = loadSession(f);
      const total = JSON.parse(session.responseBody).usage.total_tokens;
      const r = mustParse(session.responseBody);
      expect(r.promptTokens + r.responseTokens + r.thoughtTokens, f).toBe(total);
    }
  });

  it("returns null for a body that isn't usable", () => {
    // null lets the detail view show its raw-body fallback instead of an empty transcript
    expect(parseGeminiInteractionsResponse("<html>502</html>")).toBeNull();
    expect(parseGeminiInteractionsResponse("")).toBeNull();
    expect(parseGeminiInteractionsResponse("data: {\"partial\": true}\n")).toBeNull();
  });

  it("reads the last steps-bearing frame out of an SSE body", () => {
    const body = [
      "event: interaction.in_progress",
      'data: {"object":"interaction","status":"in_progress"}',
      "",
      "event: interaction.completed",
      'data: {"object":"interaction","model":"gemini-3.8-flash","status":"completed",' +
        '"steps":[{"type":"model_output","content":[{"type":"text","text":"hoi"}]}],' +
        '"usage":{"total_input_tokens":5,"total_output_tokens":2,"total_cached_tokens":1,"total_thought_tokens":3}}',
      "",
    ].join("\n");
    const r = mustParse(body);
    expect(r.status).toBe("completed");
    expect(r.promptTokens).toBe(5);
    expect(r.thoughtTokens).toBe(3);
    expect(geminiInteractionsPreviewText(r.steps)).toBe("hoi");
  });
});

describe("geminiInteractionsStepsToParts", () => {
  it("turns a function_call step into a functionCall part", () => {
    const r = mustParse(loadSession(FIRST_TURN).responseBody);
    const parts = geminiInteractionsStepsToParts(r.steps);
    expect(parts).toHaveLength(1); // the thought step contributes nothing
    expect(parts[0].functionCall).toEqual({ id: "call_1082291", name: "get_deck", args: {} });
  });

  it("turns a model_output step into a text part", () => {
    const r = mustParse(loadSession(COMPLETED).responseBody);
    const parts = geminiInteractionsStepsToParts(r.steps);
    expect(parts).toHaveLength(1);
    expect(parts[0].text).toContain("mono-blauw");
  });

  it("drops thought steps entirely - the signature is opaque", () => {
    const parts = geminiInteractionsStepsToParts([
      { type: "thought", signature: "EpICCo8CARFNMg" },
    ]);
    expect(parts).toEqual([]);
  });

  it("keeps function_call arguments as an object rather than parsing them", () => {
    const r = mustParse(loadSession(NO_TOOLS).responseBody);
    const call = geminiInteractionsStepsToParts(r.steps)[0].functionCall;
    expect(call?.name).toBe("search_cards");
    expect(call?.args).toEqual({ query: 'id<=u o:"deals 1 damage" t:creature order:edhrec' });
  });
});

describe("parseGeminiInteractionsRequest", () => {
  it("maps a first-turn request to one user turn plus tools and system prompt", () => {
    const req = JSON.parse(loadSession(FIRST_TURN).requestBody);
    const { turns, system, tools } = parseGeminiInteractionsRequest(req);

    expect(turns).toEqual([
      { role: "user", parts: [{ text: "heb je leuk suggesties voor dit deck>" }] },
    ]);
    expect(system).toContain("Magic: The Gathering");
    expect(tools).toHaveLength(8);
    expect(tools[0].name).toBe("get_deck");
    expect(tools[0].description).toBeTruthy();
    // get_deck takes no arguments and ships no parameters key at all
    expect(tools[0].parameters).toBeUndefined();

    const search = tools.find((t) => t.name === "search_cards");
    expect(search?.parameters).toMatchObject({ type: "object" });
  });

  it("alternates user and model turns for a multi-turn conversation", () => {
    const req = JSON.parse(loadSession(MULTI_TURN).requestBody);
    const { turns } = parseGeminiInteractionsRequest(req);
    expect(turns.map((t) => t.role)).toEqual(["user", "model", "user"]);
  });

  it("maps the tool loop to functionCall and functionResponse turns", () => {
    const req = JSON.parse(loadSession(COMPLETED).requestBody);
    const { turns } = parseGeminiInteractionsRequest(req);

    const calls = turns.flatMap((t) => t.parts).filter((p) => p.functionCall);
    const results = turns.flatMap((t) => t.parts).filter((p) => p.functionResponse);
    expect(calls).toHaveLength(5);
    expect(results).toHaveLength(5);

    // arguments arrive as an object, unlike OpenAI's JSON string
    const search = calls.find((p) => p.functionCall?.name === "search_cards");
    expect(search?.functionCall?.args).toEqual({ query: '"Willbreaker"' });

    // function_result is attributed to its function, not its call id
    expect(results.at(-1)?.functionResponse?.name).toBe("propose_swap_card");
    expect(results.at(-1)?.functionResponse?.response).toBeTypeOf("string");
  });

  it("falls back to the call id map when a result has no name", () => {
    const { turns } = parseGeminiInteractionsRequest({
      input: [
        { type: "function_call", id: "call_1", name: "get_deck", arguments: {} },
        { type: "function_result", call_id: "call_1", result: [{ type: "text", text: "ok" }] },
      ],
    });
    expect(turns[1].parts[0].functionResponse).toEqual({ name: "get_deck", response: "ok" });
  });

  it("produces no turns for thought items", () => {
    const { turns } = parseGeminiInteractionsRequest({
      input: [{ type: "thought", signature: "EpICCo8CARFNMg" }],
    });
    expect(turns).toEqual([]);
  });

  it("handles a request without a tools key", () => {
    const req = JSON.parse(loadSession(NO_TOOLS).requestBody);
    expect(req.tools).toBeUndefined();
    const { tools, turns } = parseGeminiInteractionsRequest(req);
    expect(tools).toEqual([]);
    expect(turns.length).toBeGreaterThan(0);
  });
});

describe("list helpers", () => {
  it("previews the assistant text of a completed interaction", () => {
    const r = mustParse(loadSession(COMPLETED).responseBody);
    expect(geminiInteractionsPreviewText(r.steps)).toContain("mono-blauw");
  });

  it("has no preview while the interaction still requires a tool call", () => {
    const r = mustParse(loadSession(FIRST_TURN).responseBody);
    expect(geminiInteractionsPreviewText(r.steps)).toBeNull();
  });

  it("lists the tool calls made in the response", () => {
    const r = mustParse(loadSession(FIRST_TURN).responseBody);
    expect(geminiInteractionsToolCallNames(r.steps)).toEqual(["get_deck"]);
  });

  it("returns the trailing run of function results fed back into the request", () => {
    const req = JSON.parse(loadSession(COMPLETED).requestBody);
    const trailing = geminiInteractionsTrailingResults(req.input);
    expect(trailing).toHaveLength(1);
    expect(trailing[0]).toBeTypeOf("string");
  });

  it("returns nothing when the request ends on a user message", () => {
    const req = JSON.parse(loadSession(MULTI_TURN).requestBody);
    expect(geminiInteractionsTrailingResults(req.input)).toEqual([]);
  });
});
