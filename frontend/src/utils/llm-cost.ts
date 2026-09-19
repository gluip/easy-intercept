// Pricing per million tokens (USD), sourced May 2026 (Gemini image models: September 2026)
// Gemini: https://ai.google.dev/gemini-api/docs/pricing
// Anthropic: https://platform.claude.com/docs/en/about-claude/pricing

export interface ModelPricing {
  inputPerMTok: number;
  outputPerMTok: number;
  cachedPerMTok: number; // cache read price
  // Image models bill generated images at their own, much higher rate; the text and
  // thinking tokens of the same answer stay on outputPerMTok.
  imageOutputPerMTok?: number;
}

// Ordered longest-prefix-first so more specific models match first
const GEMINI_PRICING: Array<[string, ModelPricing]> = [
  // Gemini 3.6/3.7/3.8 Flash share an introductory rate through 2026-12-31;
  // on 2027-01-01 it doubles to 1.50 / 7.50 / 0.15.
  ["gemini-3.8-flash",      { inputPerMTok: 0.75,  outputPerMTok: 3.75,  cachedPerMTok: 0.075  }],
  ["gemini-3.7-flash",      { inputPerMTok: 0.75,  outputPerMTok: 3.75,  cachedPerMTok: 0.075  }],
  ["gemini-3.6-flash",      { inputPerMTok: 0.75,  outputPerMTok: 3.75,  cachedPerMTok: 0.075  }],
  ["gemini-3.5-flash",      { inputPerMTok: 1.50,  outputPerMTok: 9.00,  cachedPerMTok: 0.15   }],
  // Image models ("Nano Banana"). No context caching, so cached reads cost as input.
  // A 1K image is 1120 output tokens: $0.067 on 3.1 Flash Image, $0.134 on 3 Pro Image.
  ["gemini-3.1-flash-lite-image", { inputPerMTok: 0.25, outputPerMTok: 1.50,  cachedPerMTok: 0.25, imageOutputPerMTok: 30.00  }],
  ["gemini-3.1-flash-image",      { inputPerMTok: 0.50, outputPerMTok: 3.00,  cachedPerMTok: 0.50, imageOutputPerMTok: 60.00  }],
  ["gemini-3-pro-image",          { inputPerMTok: 2.00, outputPerMTok: 12.00, cachedPerMTok: 2.00, imageOutputPerMTok: 120.00 }],
  // Listed as $0.039 per image, which is 1290 tokens at $30/M
  ["gemini-2.5-flash-image",      { inputPerMTok: 0.30, outputPerMTok: 2.50,  cachedPerMTok: 0.30, imageOutputPerMTok: 30.00  }],
  ["gemini-3.1-pro",        { inputPerMTok: 2.00,  outputPerMTok: 12.00, cachedPerMTok: 0.20   }],
  ["gemini-3.1-flash-lite", { inputPerMTok: 0.25,  outputPerMTok: 1.50,  cachedPerMTok: 0.025  }],
  ["gemini-3.1-flash",      { inputPerMTok: 0.50,  outputPerMTok: 3.00,  cachedPerMTok: 0.05   }],
  ["gemini-3-flash",        { inputPerMTok: 0.50,  outputPerMTok: 3.00,  cachedPerMTok: 0.05   }],
  ["gemini-2.5-flash-lite", { inputPerMTok: 0.10,  outputPerMTok: 0.40,  cachedPerMTok: 0.01   }],
  ["gemini-2.5-flash",      { inputPerMTok: 0.30,  outputPerMTok: 2.50,  cachedPerMTok: 0.03   }],
  ["gemini-2.5-pro",        { inputPerMTok: 1.25,  outputPerMTok: 10.00, cachedPerMTok: 0.125  }],
  ["gemini-2.0-flash-lite", { inputPerMTok: 0.075, outputPerMTok: 0.30,  cachedPerMTok: 0      }],
  ["gemini-2.0-flash",      { inputPerMTok: 0.10,  outputPerMTok: 0.40,  cachedPerMTok: 0.025  }],
];

const ANTHROPIC_PRICING: Array<[string, ModelPricing]> = [
  // Match by model ID prefix (hyphens in actual IDs)
  ["claude-opus-4",    { inputPerMTok: 5.00,  outputPerMTok: 25.00, cachedPerMTok: 0.50 }],
  ["claude-opus-3",    { inputPerMTok: 15.00, outputPerMTok: 75.00, cachedPerMTok: 1.50 }],
  ["claude-sonnet-4",  { inputPerMTok: 3.00,  outputPerMTok: 15.00, cachedPerMTok: 0.30 }],
  ["claude-haiku-4-5", { inputPerMTok: 1.00,  outputPerMTok: 5.00,  cachedPerMTok: 0.10 }],
  ["claude-haiku-4",   { inputPerMTok: 1.00,  outputPerMTok: 5.00,  cachedPerMTok: 0.10 }],
  ["claude-haiku-3",   { inputPerMTok: 0.80,  outputPerMTok: 4.00,  cachedPerMTok: 0.08 }],
];

const COPILOT_PRICING: Array<[string, ModelPricing]> = [
  ["mai-code-1-flash-picker", { inputPerMTok: 0.75, outputPerMTok: 4.50, cachedPerMTok: 0 }],
];

// Ordered longest-prefix-first; cached = 0.5x input price for OpenAI
const OPENAI_PRICING: Array<[string, ModelPricing]> = [
  // gpt-5.6 (Jul 2026) — https://developers.openai.com/api/docs/pricing
  ["gpt-5.6-sol",   { inputPerMTok: 5.00,  outputPerMTok: 30.00,  cachedPerMTok: 0.50  }],
  ["gpt-5.6-terra", { inputPerMTok: 2.50,  outputPerMTok: 15.00,  cachedPerMTok: 0.25  }],
  ["gpt-5.6-luna",  { inputPerMTok: 1.00,  outputPerMTok: 6.00,   cachedPerMTok: 0.10  }],
  // gpt-5.x flagship (May 2026)
  ["gpt-5.5-pro",   { inputPerMTok: 30.00, outputPerMTok: 180.00, cachedPerMTok: 0     }],
  ["gpt-5.5",       { inputPerMTok: 5.00,  outputPerMTok: 30.00,  cachedPerMTok: 0.50  }],
  ["gpt-5.4-pro",   { inputPerMTok: 30.00, outputPerMTok: 180.00, cachedPerMTok: 0     }],
  ["gpt-5.4-mini",  { inputPerMTok: 0.75,  outputPerMTok: 4.50,   cachedPerMTok: 0.075 }],
  ["gpt-5.4-nano",  { inputPerMTok: 0.20,  outputPerMTok: 1.25,   cachedPerMTok: 0.02  }],
  ["gpt-5.4",       { inputPerMTok: 2.50,  outputPerMTok: 15.00,  cachedPerMTok: 0.25  }],
  // gpt-4.1 series
  ["gpt-4.1-mini",  { inputPerMTok: 0.40,  outputPerMTok: 1.60,   cachedPerMTok: 0.10  }],
  ["gpt-4.1-nano",  { inputPerMTok: 0.10,  outputPerMTok: 0.40,   cachedPerMTok: 0.025 }],
  ["gpt-4.1",       { inputPerMTok: 2.00,  outputPerMTok: 8.00,   cachedPerMTok: 0.50  }],
  // gpt-4o series
  ["gpt-4o-mini",   { inputPerMTok: 0.15,  outputPerMTok: 0.60,   cachedPerMTok: 0.075 }],
  ["gpt-4o",        { inputPerMTok: 2.50,  outputPerMTok: 10.00,  cachedPerMTok: 1.25  }],
  // o-series reasoning
  ["o4-mini",       { inputPerMTok: 1.10,  outputPerMTok: 4.40,   cachedPerMTok: 0.275 }],
  ["o3-mini",       { inputPerMTok: 1.10,  outputPerMTok: 4.40,   cachedPerMTok: 0.275 }],
  ["o3",            { inputPerMTok: 10.00, outputPerMTok: 40.00,  cachedPerMTok: 2.50  }],
];

function matchPricing(
  modelId: string,
  table: Array<[string, ModelPricing]>,
): ModelPricing | null {
  const id = modelId.toLowerCase();
  for (const [prefix, pricing] of table) {
    if (id.startsWith(prefix)) return pricing;
  }
  return null;
}

export interface CostBreakdown {
  inputCost: number;
  outputCost: number;
  cachedCost: number;
  total: number;
}

/**
 * Calculate the cost of an LLM request.
 *
 * For Gemini: promptTokens includes cachedTokens, so we subtract them from
 * the base input cost and price them separately.
 * For Anthropic: promptTokens = non-cached input; cachedTokens = cache reads.
 * thoughtTokens (Gemini) are billed as output tokens.
 * imageOutputTokens (Gemini image models) is the part of responseTokens that is
 * generated images; it is billed at the model's image rate when it has one.
 */
export function calcCost(
  provider: "gemini" | "anthropic" | "openai" | "copilot",
  modelVersion: string,
  promptTokens: number,
  responseTokens: number,
  cachedTokens: number,
  thoughtTokens: number,
  imageOutputTokens = 0,
): CostBreakdown | null {
  let pricing: ModelPricing | null = null;

  if (provider === "gemini") {
    pricing = matchPricing(modelVersion, GEMINI_PRICING);
  } else if (provider === "anthropic") {
    pricing = matchPricing(modelVersion, ANTHROPIC_PRICING);
  } else if (provider === "openai") {
    pricing = matchPricing(modelVersion, OPENAI_PRICING);
  } else if (provider === "copilot") {
    pricing = matchPricing(modelVersion, COPILOT_PRICING);
  }

  if (!pricing) return null;

  const M = 1_000_000;

  if (provider === "gemini") {
    // promptTokens includes cachedTokens
    const nonCachedInput = Math.max(0, promptTokens - cachedTokens);
    const imageOutput = Math.min(Math.max(0, imageOutputTokens), responseTokens);
    const textOutput = responseTokens - imageOutput + thoughtTokens;
    const inputCost  = (nonCachedInput / M) * pricing.inputPerMTok;
    const cachedCost = (cachedTokens   / M) * pricing.cachedPerMTok;
    const outputCost =
      (textOutput  / M) * pricing.outputPerMTok +
      (imageOutput / M) * (pricing.imageOutputPerMTok ?? pricing.outputPerMTok);
    return { inputCost, outputCost, cachedCost, total: inputCost + outputCost + cachedCost };
  } else if (provider === "openai") {
    // promptTokens = total input (includes cached); cachedTokens = subset already cached
    const nonCachedInput = Math.max(0, promptTokens - cachedTokens);
    const inputCost  = (nonCachedInput / M) * pricing.inputPerMTok;
    const cachedCost = (cachedTokens   / M) * pricing.cachedPerMTok;
    const outputCost = (responseTokens / M) * pricing.outputPerMTok;
    return { inputCost, outputCost, cachedCost, total: inputCost + outputCost + cachedCost };
  } else {
    // Anthropic: promptTokens = non-cached input, cachedTokens = cache reads
    const inputCost  = (promptTokens  / M) * pricing.inputPerMTok;
    const cachedCost = (cachedTokens  / M) * pricing.cachedPerMTok;
    const outputCost = (responseTokens / M) * pricing.outputPerMTok;
    return { inputCost, outputCost, cachedCost, total: inputCost + outputCost + cachedCost };
  }
}

export function formatCost(cost: CostBreakdown): string {
  const t = cost.total;
  if (t < 0.0001) return "<$0.0001";
  if (t < 0.01)   return `$${t.toFixed(4)}`;
  if (t < 1)      return `$${t.toFixed(3)}`;
  return `$${t.toFixed(2)}`;
}
