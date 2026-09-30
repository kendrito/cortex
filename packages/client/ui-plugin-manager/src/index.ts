/** Compatibility service for clients that previously requested registry recommendations. */

import type { Context } from '@cortex/cordis'
import z from '@cortex/schemastery'
import { MAX_TIMER_DELAY_MS } from '@cortex/timeout'
import { Remote, TypertRemoteService } from '@cortex/typert-protocol'

declare module '@cortex/cordis' {
  interface Context {
    pluginRegistryProbe: PluginRegistryProbe
  }
}

/** Legacy configuration accepted without issuing network requests. */
export interface Config {
  /** Retained for existing profile patches; registry probes are always disabled. */
  registryProbeEnabled: boolean
  /** Retained for existing profile patches; no probe deadline is scheduled. */
  registryProbeTimeoutMs: number
  /** Retained for existing profile patches; no network result is cached. */
  registryProbeCacheTtlMs: number
}

/** Keeps older Client API calls inert; registry selection is explicit. */
export default class PluginRegistryProbe extends TypertRemoteService {
  static Config: z<Partial<Config>, Config> = z.object({
    registryProbeEnabled: z.boolean().default(false),
    registryProbeTimeoutMs: z.natural().min(1).max(MAX_TIMER_DELAY_MS).default(1500),
    registryProbeCacheTtlMs: z.natural().default(300000),
  })

  constructor(ctx: Context) {
    super(ctx, 'pluginRegistryProbe')
  }

  /**
   * Preserve the existing Client API without contacting external registries.
   * @returns null; the configured registry is never changed by a network probe.
   */
  @Remote
  fastest(): Promise<string | null> {
    return Promise.resolve(null)
  }
}
