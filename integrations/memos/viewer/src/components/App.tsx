/**
 * Top-level viewer app shell. Wires:
 *   - Topbar (brand + search + notifications)
 *   - Sidebar (primary nav + theme / language controls)
 *   - Main content area (routed views)
 *
 * State subscriptions (health polling, theme) mount here so they run
 * once per page load.
 */
import { Header } from "./Header";
import { Sidebar } from "./Sidebar";
import { ContentRouter } from "./ContentRouter";
import { AuthGate } from "./AuthGate";
import { useEffect } from "preact/hooks";
import { startHealthPolling, stopHealthPolling } from "../stores/health";
import { route } from "../stores/router";

export function App() {
  useEffect(() => {
    startHealthPolling();
    return stopHealthPolling;
  }, []);

  return (
    <AuthGate>
      <div class="shell">
        <Header />
        <Sidebar />
        <main class={route.value.path === "/brain" ? "main main--brain" : "main"}>
          <ContentRouter />
        </main>
      </div>
    </AuthGate>
  );
}
