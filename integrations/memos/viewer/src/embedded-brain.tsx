/** Brain-only entry hosted inside an authenticated Cortex panel. */
import "./styles/tokens.css";
import "./styles/embedded-brain.css";
import { render } from "preact";
import { useState } from "preact/hooks";
import { BrainView } from "./views/BrainView";
import { MemoryControls } from "./components/brain/MemoryControls";
import { bt } from "./components/brain/brain-copy";

const theme = new URLSearchParams(location.search).get("theme");
document.documentElement.dataset.theme = theme === "light" ? "light" : "dark";

/** The tab: memory controls above the explorer, which reloads after a reset. */
function EmbeddedBrain() {
  const [generation, setGeneration] = useState(0);

  return <main class="embedded-brain">
    <div class="embedded-brain__identity">
      <strong>{bt("embeddedBrand")}</strong>
      <MemoryControls onReset={() => setGeneration((value) => value + 1)} />
    </div>
    <BrainView key={generation} embedded />
  </main>;
}

const root = document.getElementById("app");
if (!root) throw new Error("#app root element missing from brain.html");
render(<EmbeddedBrain />, root);
