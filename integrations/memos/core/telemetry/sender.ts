/** Telemetry is deliberately absent from the Cortex memory distribution. */
import type { Logger } from "../logger/types.js";

export interface TelemetryConfig { enabled?: boolean; }

/** Compatibility facade: no identifiers, files, timers, endpoints, or sends. */
export class Telemetry {
  constructor(
    _config: TelemetryConfig,
    _stateDir: string,
    _pluginVersion: string,
    _log: Logger,
    _pluginDir?: string,
  ) {}

  trackPluginStarted(_agentName: string): void {}
  trackTurnStart(_agentName: string, _latencyMs: number, _hitCount: number): void {}
  trackTurnEnd(_agentName: string, _traceCount: number): void {}
  trackMemorySearch(_agentName: string, _latencyMs: number, _hitCount: number): void {}
  trackFeedback(_agentName: string, _feedbackType: string): void {}
  trackViewerOpened(): void {}
  trackError(_source: string, _errorType: string): void {}
  async shutdown(): Promise<void> {}
}
