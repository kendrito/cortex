/** Local viewer preferences and embedding maintenance for Cortex-managed memory. */
import { useEffect, useState } from "preact/hooks";
import { api } from "../api/client";
import { t, locale, setLocale } from "../stores/i18n";
import { theme, setTheme } from "../stores/theme";
import { Icon } from "../components/Icon";

interface EmbeddingMaintenanceStats {
  dimension: number;
  available: boolean;
  totalSlots: number;
  ready: number;
  missing: number;
  dimMismatch: number;
  needsRepair: number;
}

interface EmbeddingMaintenanceRunResult {
  mode: "repair" | "rebuild";
  processed: number;
  updated: number;
  failed: number;
  offset: number;
  nextOffset: number;
  done: boolean;
  statsAfter: EmbeddingMaintenanceStats;
  error?: string;
}

/** Render local preferences; Cortex owns model selection and memory configuration. */
export function SettingsView() {
  return (
    <>
      <div class="view-header">
        <div class="view-header__title"><h1>{t("settings.title")}</h1></div>
      </div>
      <div class="vstack" style="gap:var(--sp-4)">
        <section class="card">
          <div class="card__header">
            <div>
              <h3 class="card__title">{t("settings.cortex.model.title")}</h3>
              <p class="card__subtitle">{t("settings.cortex.model.desc")}</p>
            </div>
          </div>
        </section>
        <section class="card">
          <div class="card__header">
            <div>
              <h3 class="card__title">{t("settings.cortex.embeddings.title")}</h3>
              <p class="card__subtitle">{t("settings.cortex.embeddings.desc")}</p>
            </div>
          </div>
          <EmbeddingMaintenancePanel />
        </section>
        <section class="card">
          <div class="card__header"><h3 class="card__title">{t("settings.general.lang")}</h3></div>
          <div class="segmented">
            <button class="segmented__item" aria-pressed={locale.value === "en"} onClick={() => setLocale("en")}>
              {t("settings.cortex.language.en")}
            </button>
            <button class="segmented__item" aria-pressed={locale.value === "zh"} onClick={() => setLocale("zh")}>
              {t("settings.cortex.language.zh")}
            </button>
          </div>
        </section>
        <section class="card">
          <div class="card__header"><h3 class="card__title">{t("settings.general.theme")}</h3></div>
          <div class="segmented">
            {[
              { v: "auto" as const, k: "settings.general.theme.auto" as const, icon: "monitor" as const },
              { v: "light" as const, k: "settings.general.theme.light" as const, icon: "sun" as const },
              { v: "dark" as const, k: "settings.general.theme.dark" as const, icon: "moon" as const },
            ].map((opt) => (
              <button key={opt.v} class="segmented__item" aria-pressed={theme.value === opt.v} onClick={() => setTheme(opt.v)}>
                <Icon name={opt.icon} size={14} />{t(opt.k)}
              </button>
            ))}
          </div>
        </section>
      </div>
    </>
  );
}

function EmbeddingMaintenancePanel() {
  const [stats, setStats] = useState<EmbeddingMaintenanceStats | null>(null);
  const [running, setRunning] = useState<"repair" | "rebuild" | null>(null);
  const [status, setStatus] = useState<{ kind: "ok" | "error" | "muted"; text: string } | null>(null);

  const refresh = async () => {
    try {
      setStats(await api.get<EmbeddingMaintenanceStats>("/api/v1/embeddings/maintenance"));
    } catch (err) {
      setStatus({ kind: "error", text: (err as Error).message });
    }
  };

  useEffect(() => {
    void refresh();
  }, []);

  const run = async (mode: "repair" | "rebuild") => {
    setRunning(mode);
    setStatus({ kind: "muted", text: t("settings.embedding.rebuild.running") });
    let offset = 0;
    let updated = 0;
    let failed = 0;
    try {
      for (;;) {
        const r = await api.post<EmbeddingMaintenanceRunResult>(
          "/api/v1/embeddings/rebuild",
          { mode, offset },
        );
        updated += r.updated;
        failed += r.failed;
        offset = r.nextOffset;
        setStats(r.statsAfter);
        setStatus({
          kind: "muted",
          text: t("settings.embedding.rebuild.progress", {
            updated,
            failed,
            remaining: r.statsAfter.needsRepair,
          }),
        });
        if (r.error) {
          setStatus({ kind: "error", text: r.error });
          break;
        }
        if (r.done) {
          setStatus({
            kind: failed > 0 ? "error" : "ok",
            text: t("settings.embedding.rebuild.done", { updated, failed }),
          });
          break;
        }
      }
    } catch (err) {
      setStatus({ kind: "error", text: (err as Error).message });
    } finally {
      setRunning(null);
      void refresh();
    }
  };

  const healthText = stats
    ? t("settings.embedding.maintenance.stats", {
        ready: stats.ready,
        total: stats.totalSlots,
        missing: stats.missing,
        mismatch: stats.dimMismatch,
        dim: stats.dimension,
      })
    : t("common.loading");
  const disabled = !!running || stats?.available === false;

  return (
    <div
      style="margin-top:var(--sp-4);padding:var(--sp-3);border:1px solid var(--border);border-radius:var(--radius-md);background:var(--bg-canvas)"
    >
      <div class="hstack" style="justify-content:space-between;gap:var(--sp-3);align-items:flex-start;flex-wrap:wrap">
        <div>
          <div style="font-weight:var(--fw-semi);font-size:var(--fs-sm)">
            {t("settings.embedding.maintenance.title")}
          </div>
          <div class="muted" style="font-size:var(--fs-xs);margin-top:2px">
            {healthText}
          </div>
        </div>
        <div class="hstack" style="gap:var(--sp-2);flex-wrap:wrap">
          <button class="btn btn--sm" onClick={() => void refresh()} disabled={!!running}>
            <Icon name="refresh-cw" size={14} />
            {t("common.refresh")}
          </button>
          <button class="btn btn--sm" onClick={() => void run("repair")} disabled={disabled || stats?.needsRepair === 0}>
            <Icon name={running === "repair" ? "loader-2" : "plug"} size={14} class={running === "repair" ? "spin" : ""} />
            {t("settings.embedding.repair")}
          </button>
          <button class="btn btn--primary btn--sm" onClick={() => void run("rebuild")} disabled={disabled || stats?.totalSlots === 0}>
            <Icon name={running === "rebuild" ? "loader-2" : "refresh-cw"} size={14} class={running === "rebuild" ? "spin" : ""} />
            {t("settings.embedding.rebuild")}
          </button>
        </div>
      </div>
      {stats?.available === false && (
        <div class="muted" style="font-size:var(--fs-xs);margin-top:var(--sp-2)">
          {t("settings.embedding.maintenance.unavailable")}
        </div>
      )}
      {status && (
        <div
          role="status"
          style={`margin-top:var(--sp-2);font-size:var(--fs-xs);color:${
            status.kind === "ok"
              ? "var(--success)"
              : status.kind === "error"
                ? "var(--danger)"
                : "var(--fg-muted)"
          }`}
        >
          {status.text}
        </div>
      )}
    </div>
  );
}
