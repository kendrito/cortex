/** Producer-owned source metadata for Cortex memory messages. */

import type {} from "@cortex/llm";

/** Source kind shared by durable recall and auxiliary memory requests. */
export const MEMOS_MESSAGE_SOURCE = "memos-local-memory";

declare module "@cortex/llm" {
  interface MessageSourceMap {
    "memos-local-memory": { kind: "memos-local-memory"; form?: "recall" };
  }
}
