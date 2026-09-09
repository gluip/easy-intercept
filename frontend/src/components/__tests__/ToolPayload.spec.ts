import { describe, it, expect } from "vitest";
import { createSSRApp, h } from "vue";
import { renderToString } from "@vue/server-renderer";
import ToolPayload from "../ToolPayload.vue";

function render(value: unknown): Promise<string> {
  return renderToString(createSSRApp({ render: () => h(ToolPayload, { value }) }));
}

describe("ToolPayload", () => {
  it("renders a tool result that is encoded JSON as a tree, not escaped text", async () => {
    const html = await render('{"name":"Mono-Black Zombie Swarm","totalCards":60}');
    // Rendered as key/value nodes, not as one escaped `"{\"name\":…}"` blob
    expect(html).toContain("&quot;name&quot;");
    expect(html).toContain("Mono-Black Zombie Swarm");
    expect(html).toContain(">60<");
    expect(html).not.toContain("\\&quot;");
  });

  it("decodes JSON nested one level deeper", async () => {
    const html = await render([{ type: "text", text: '{"totalCards":60}' }]);
    expect(html).toContain("&quot;totalCards&quot;");
    expect(html).toContain(">60<");
  });

  it("renders plain text results as text, keeping their line breaks", async () => {
    const html = await render("first line\nsecond line");
    expect(html).toContain("payload-text");
    expect(html).toContain("first line\nsecond line");
  });

  it("renders structured arguments as a tree", async () => {
    const html = await render({ deckId: "abc", limit: 10 });
    expect(html).toContain("&quot;deckId&quot;");
    expect(html).toContain(">10<");
  });
});
