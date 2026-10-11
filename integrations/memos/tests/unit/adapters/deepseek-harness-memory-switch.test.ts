import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import {
  apply,
  deleteMemoryStore,
  MEMORY_CONTROL_EVENT,
  MEMORY_SWITCH_FILE,
  readMemorySwitch,
  writeMemorySwitch,
  type MemoryControlRequest,
  type MemoryControlStatus,
} from "../../../adapters/deepseek-harness/index.js";

type Listener = (request: MemoryControlRequest) => Promise<MemoryControlStatus>;

/** Just enough of a Cordis context for the switch: a logger and one event. */
function fakeContext() {
  const listeners = new Map<string, Listener>();
  const ctx = {
    logger: { info: () => undefined, warn: () => undefined },
    on: (name: string, listener: Listener) => {
      listeners.set(name, listener);
      return () => listeners.delete(name);
    },
  };
  const send = (request: MemoryControlRequest) => listeners.get(MEMORY_CONTROL_EVENT)!(request);
  return { ctx, send, listeners };
}

const CONFIG = {
  enabled: true, profileId: "test", home: "", recallEnabled: true, captureEnabled: true,
  toolsEnabled: true, hostLlmEnabled: true, viewerEnabled: false, viewerPort: 18_801,
  recallTimeoutMs: 3_000, contextMaxChars: 6_000, toolResultMaxChars: 1_200, failOnStartupError: true,
};

describe("Cortex memory switch", () => {
  let home: string;
  let previousHome: string | undefined;

  beforeEach(async () => {
    home = await mkdtemp(join(tmpdir(), "memos-switch-"));
    previousHome = process.env["MEMOS_HOME"];
    process.env["MEMOS_HOME"] = home;
  });
  afterEach(async () => {
    if (previousHome === undefined) delete process.env["MEMOS_HOME"];
    else process.env["MEMOS_HOME"] = previousHome;
    await rm(home, { recursive: true, force: true });
  });

  it("reads on by default and keeps what the person chose", async () => {
    expect(await readMemorySwitch(home)).toBe(true);
    await writeMemorySwitch(home, false);
    expect(await readMemorySwitch(home)).toBe(false);
    expect(JSON.parse(await readFile(join(home, MEMORY_SWITCH_FILE), "utf8"))).toEqual({ enabled: false });
    await writeFile(join(home, MEMORY_SWITCH_FILE), "not json");
    expect(await readMemorySwitch(home)).toBe(true);
  });

  it("deletes the store and its WAL files, and tolerates a store that is already gone", async () => {
    const db = join(home, "data", "memos.db");
    await mkdir(join(home, "data"), { recursive: true });
    for (const file of [db, `${db}-wal`, `${db}-shm`]) await writeFile(file, "x");
    await deleteMemoryStore(db);
    expect([db, `${db}-wal`, `${db}-shm`].some((file) => existsSync(file))).toBe(false);
    await expect(deleteMemoryStore(db)).resolves.toBeUndefined();
  });

  it("starts idle when switched off, and a reset wipes the store without starting memory", async () => {
    await writeMemorySwitch(home, false);
    const db = join(home, "data", "memos.db");
    await mkdir(join(home, "data"), { recursive: true });
    await writeFile(db, "learned");
    const { ctx, send, listeners } = fakeContext();
    const dispose = await apply(ctx as never, { ...CONFIG, home });

    expect(await send({ action: "status" })).toEqual({ enabled: false, running: false });
    expect(await send({ action: "reset" })).toEqual({ enabled: false, running: false });
    expect(existsSync(db)).toBe(false);
    expect(await send({ action: "set-enabled", enabled: false })).toEqual({ enabled: false, running: false });

    await dispose();
    expect(listeners.size).toBe(0);
  });

  it("leaves the administrator's off switch as before: nothing loads, nothing listens", async () => {
    const { ctx, listeners } = fakeContext();
    await apply(ctx as never, { ...CONFIG, enabled: false, home });
    expect(listeners.size).toBe(0);
  });
});
