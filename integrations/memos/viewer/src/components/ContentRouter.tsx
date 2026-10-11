import { route } from "../stores/router";
import { OverviewView } from "../views/OverviewView";
import { BrainView } from "../views/BrainView";
import { MemoriesView } from "../views/MemoriesView";
import { TasksView } from "../views/TasksView";
import { SkillsView } from "../views/SkillsView";
import { PoliciesView } from "../views/PoliciesView";
import { WorldModelsView } from "../views/WorldModelsView";
import { AnalyticsView } from "../views/AnalyticsView";
import { LogsView } from "../views/LogsView";
import { ImportView } from "../views/ImportView";
import { SettingsView } from "../views/SettingsView";
import { HelpView } from "../views/HelpView";
import { Icon } from "./Icon";
import { t } from "../stores/i18n";

export function ContentRouter() {
  const path = route.value.path;
  switch (path) {
    case "/brain":        return <BrainView />;
    case "/overview":     return <OverviewView />;
    case "/memories":     return <MemoriesView />;
    case "/tasks":        return <TasksView />;
    case "/skills":       return <SkillsView />;
    case "/policies":     return <PoliciesView />;
    case "/world-models": return <WorldModelsView />;
    case "/analytics":    return <AnalyticsView />;
    case "/logs":         return <LogsView />;
    case "/import":       return <ImportView />;
    case "/admin":
    case "/settings":     return <SettingsView />;
    case "/help":         return <HelpView />;
    default:
      return (
        <div class="empty">
          <div class="empty__icon">
            <Icon name="info" size={24} />
          </div>
          <div class="empty__title">{t("common.empty")}</div>
          <div class="empty__hint">
            <code class="mono">{path}</code>
          </div>
        </div>
      );
  }
}
