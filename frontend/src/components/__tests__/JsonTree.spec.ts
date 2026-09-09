import { describe, it, expect } from "vitest";
import { createSSRApp, h } from "vue";
import { renderToString } from "@vue/server-renderer";
import JsonTree from "../JsonTree.vue";

function render(props: Record<string, unknown>): Promise<string> {
  return renderToString(createSSRApp({ render: () => h(JsonTree, props) }));
}

describe("JsonTree default open state", () => {
  it("opens the root so it says more than '{ 2 keys }'", async () => {
    const html = await render({ data: { a: 1, b: 2, c: 3, d: 4 } });
    expect(html).toContain("&quot;d&quot;");
    expect(html).not.toContain("4 keys");
  });

  it("keeps a huge root collapsed instead of rendering thousands of rows", async () => {
    const data = Array.from({ length: 500 }, (_, i) => ({ i }));
    const html = await render({ data });
    expect(html).toContain("500 items");
    expect(html).not.toContain("&quot;i&quot;");
  });

  it("still collapses wide nodes below the root", async () => {
    const html = await render({ data: { nested: { a: 1, b: 2, c: 3, d: 4 } } });
    expect(html).toContain("4 keys");
  });
});

describe("JsonTree JSON-in-string expansion", () => {
  it("renders a decoded JSON string one level deeper than the string node", async () => {
    // The decoded tree sits inside the string's node, so it inherits depth + 1
    // and falls under the size heuristics rather than the root's always-open rule.
    const html = await render({
      data: '{"a":1,"b":2,"c":3,"d":4}',
      autoJson: true,
    });
    expect(html).toContain("4 keys");
  });

  it("expands JSON held in a nested string without a click", async () => {
    const html = await render({
      data: { result: '{"totalCards":60}' },
      autoJson: true,
    });
    expect(html).toContain("&quot;totalCards&quot;");
    expect(html).toContain(">60<");
  });
});
