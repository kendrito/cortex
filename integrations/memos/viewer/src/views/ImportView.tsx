/** Explicit local JSON backup import and export for Cortex memory. */
import { useState } from "preact/hooks";
import { api } from "../api/client";
import { t } from "../stores/i18n";
import { Icon } from "../components/Icon";

interface EmbeddingRepairResult {
  updated: number;
  failed: number;
  done: boolean;
  statsAfter: { needsRepair: number };
  error?: string;
}

export function ImportView() {
  return (
    <>
      <div class="view-header">
        <div class="view-header__title">
          <h1>{t("import.title")}</h1>
          <p>{t("import.subtitle")}</p>
        </div>
      </div>

      <div class="vstack" style="gap:var(--sp-4)">
        <ExportCard />
        <ImportCard />
      </div>
    </>
  );
}

function ExportCard() {
  const [busy, setBusy] = useState(false);

  const run = async () => {
    setBusy(true);
    try {
      const blob = await api.blob("/api/v1/export");
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      const ts = new Date().toISOString().slice(0, 10);
      a.href = url;
      a.download = `memos-export-${ts}.json`;
      a.click();
      URL.revokeObjectURL(url);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section class="card">
      <div class="card__header">
        <div class="hstack">
          <span
            aria-hidden="true"
            style="width:36px;height:36px;display:flex;align-items:center;justify-content:center;border-radius:var(--radius-md);background:var(--accent-soft);color:var(--accent)"
          >
            <Icon name="download" size={18} />
          </span>
          <div>
            <h3 class="card__title" style="margin:0">
              {t("import.export.title")}
            </h3>
            <p class="card__subtitle" style="margin:0">
              {t("import.export.desc")}
            </p>
          </div>
        </div>
      </div>
      <button class="btn btn--primary" onClick={run} disabled={busy}>
        {busy ? <Icon name="loader-2" size={14} class="spin" /> : <Icon name="download" size={14} />}
        {t("import.export.btn")}
      </button>
    </section>
  );
}

function ImportCard() {
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<{
    kind: "ok" | "error";
    text: string;
  } | null>(null);

  const run = async (file: File) => {
    setBusy(true);
    setStatus(null);
    try {
      const form = new FormData();
      form.append("bundle", file);
      const r = await api.postRaw<{ imported: number; skipped: number }>(
        "/api/v1/import",
        form,
      );
      setStatus({
        kind: "ok",
        text: `Imported ${r.imported} / skipped ${r.skipped}`,
      });
    } catch (err) {
      setStatus({ kind: "error", text: (err as Error).message });
    } finally {
      setBusy(false);
    }
  };

  return (
    <section class="card">
      <div class="card__header">
        <div class="hstack">
          <span
            aria-hidden="true"
            style="width:36px;height:36px;display:flex;align-items:center;justify-content:center;border-radius:var(--radius-md);background:var(--info-soft);color:var(--info)"
          >
            <Icon name="upload" size={18} />
          </span>
          <div>
            <h3 class="card__title" style="margin:0">
              {t("import.import.title")}
            </h3>
            <p class="card__subtitle" style="margin:0">
              {t("import.import.desc")}
            </p>
          </div>
        </div>
      </div>
      <label class="btn">
        <Icon name="upload" size={14} />
        {t("import.import.btn")}
        <input
          type="file"
          accept="application/json,.json"
          hidden
          onChange={(e) => {
            const f = (e.target as HTMLInputElement).files?.[0];
            if (f) void run(f);
          }}
          disabled={busy}
        />
      </label>
      {status && (
        <div
          role="status"
          style={`margin-top:var(--sp-3);font-size:var(--fs-sm);color:${
            status.kind === "ok" ? "var(--success)" : "var(--danger)"
          }`}
        >
          {status.text}
        </div>
      )}
      {status?.kind === "ok" && <EmbeddingRepairButton />}
    </section>
  );
}

function EmbeddingRepairButton() {
  const [running, setRunning] = useState(false);
  const [status, setStatus] = useState<{ kind: "ok" | "error" | "muted"; text: string } | null>(null);

  const run = async () => {
    setRunning(true);
    setStatus({ kind: "muted", text: t("import.embeddingRepair.running") });
    let updated = 0;
    let failed = 0;
    try {
      for (;;) {
        const r = await api.post<EmbeddingRepairResult>(
          "/api/v1/embeddings/rebuild",
          { mode: "repair", limit: 100 },
        );
        updated += r.updated;
        failed += r.failed;
        if (r.error) {
          setStatus({ kind: "error", text: r.error });
          break;
        }
        if (r.done) {
          setStatus({
            kind: failed > 0 ? "error" : "ok",
            text: t("import.embeddingRepair.done", { updated, failed }),
          });
          break;
        }
        setStatus({
          kind: "muted",
          text: t("import.embeddingRepair.progress", {
            updated,
            failed,
            remaining: r.statsAfter.needsRepair,
          }),
        });
      }
    } catch (err) {
      setStatus({ kind: "error", text: (err as Error).message });
    } finally {
      setRunning(false);
    }
  };

  return (
    <div style="margin-top:var(--sp-3)">
      <button class="btn btn--sm" onClick={run} disabled={running}>
        <Icon name={running ? "loader-2" : "plug"} size={14} class={running ? "spin" : ""} />
        {t("import.embeddingRepair.btn")}
      </button>
      {status && (
        <span
          role="status"
          style={`margin-left:var(--sp-2);font-size:var(--fs-xs);color:${
            status.kind === "ok"
              ? "var(--success)"
              : status.kind === "error"
                ? "var(--danger)"
                : "var(--fg-muted)"
          }`}
        >
          {status.text}
        </span>
      )}
    </div>
  );
}
