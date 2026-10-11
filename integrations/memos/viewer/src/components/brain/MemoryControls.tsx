/**
 * The person's memory controls in the Cortex Brain tab: switch learning off or on,
 * and erase everything learned. Memory itself ships with Cortex; these act on it
 * through the Brain host's /api/v1/memory routes, which hand each request to MemOS.
 */
import { useEffect, useState } from "preact/hooks";
import { api, ApiError } from "../../api/client";
import { bt } from "./brain-copy";

interface MemoryStatus {
  enabled: boolean;
  running: boolean;
  error?: string;
}

type Phase = "loading" | "ready" | "busy" | "unavailable";

export function MemoryControls({ onReset }: { onReset: () => void }) {
  const [status, setStatus] = useState<MemoryStatus | null>(null);
  const [phase, setPhase] = useState<Phase>("loading");
  const [isConfirming, setConfirming] = useState(false);
  const [notice, setNotice] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    api.get<MemoryStatus>("/api/v1/memory", { signal: controller.signal })
      .then((value) => { setStatus(value); setPhase("ready"); })
      .catch((error: unknown) => { if (!controller.signal.aborted) failed(error); });
    return () => controller.abort();
  }, []);

  function failed(error: unknown): void {
    setPhase(error instanceof ApiError && error.status === 503 ? "unavailable" : "ready");
    setNotice(error instanceof Error ? error.message : bt("memoryFailed"));
  }

  async function send(path: string, body: unknown, done: (value: MemoryStatus) => void): Promise<void> {
    setPhase("busy");
    setNotice("");
    try {
      const value = await api.post<MemoryStatus>(path, body);
      setStatus(value);
      setPhase("ready");
      done(value);
    } catch (error) {
      failed(error);
    }
  }

  const toggle = (): Promise<void> => send("/api/v1/memory/enabled", { enabled: !status?.enabled }, (value) => {
    setNotice(value.error ? bt("memoryStartFailed", { error: value.error }) : "");
  });
  const reset = (): Promise<void> => send("/api/v1/memory/reset", { confirm: "reset" }, () => {
    setConfirming(false);
    setNotice(bt("memoryResetDone"));
    onReset();
  });

  if (phase === "unavailable") return <span class="memory-controls__note">{bt("memoryUnavailable")}</span>;

  const isOn = status?.enabled ?? false;
  const isBusy = phase !== "ready";

  return <div class="memory-controls">
    {notice && <span class="memory-controls__note" role="status">{notice}</span>}
    {isConfirming
      ? <div class="memory-controls__confirm" role="alertdialog" aria-label={bt("memoryResetTitle")}>
        <span>{bt("memoryResetQuestion")}</span>
        <button class="brain__button" disabled={isBusy} onClick={() => setConfirming(false)}>{bt("memoryCancel")}</button>
        <button class="brain__button memory-controls__danger" disabled={isBusy} onClick={reset}>{bt("memoryResetConfirm")}</button>
      </div>
      : <>
        <label class="memory-controls__switch" title={bt(isOn ? "memoryOnHint" : "memoryOffHint")}>
          <span>{bt("memoryLabel")}</span>
          <button
            type="button"
            role="switch"
            aria-checked={isOn}
            class="memory-controls__toggle"
            data-on={isOn}
            disabled={isBusy || status === null}
            onClick={toggle}
          ><span class="memory-controls__knob" /></button>
          <span class="memory-controls__state">{status === null ? "…" : bt(isOn ? "memoryOn" : "memoryOff")}</span>
        </label>
        <button class="brain__button" disabled={isBusy || status === null} onClick={() => setConfirming(true)}>{bt("memoryReset")}</button>
      </>}
  </div>;
}
