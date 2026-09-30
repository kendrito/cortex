/** Type-only optional service declaration. Cortex never installs this service. */
import type { ProductEventMap } from '../events.ts'
export type { ProductEvent, ProductEventMap, TrackProductEvent } from '../events.ts'

declare module '@cortex/cordis' {
  interface Context {
    /** Absent in Cortex: no collection, storage, transport, or runtime implementation. */
    productAnalytics: DisabledProductAnalytics
  }
}

/** Upstream optional call sites compile against this inert declaration. */
export interface DisabledProductAnalytics {
  readonly enabled: false
  /**
   * Declare the upstream optional call signature; Cortex provides no implementation.
   * @param name - Upstream event identifier.
   * @param attributes - Upstream event attributes.
   * @param timestamp - Optional upstream event timestamp.
   */
  track<K extends keyof ProductEventMap>(name: K, attributes: ProductEventMap[K], timestamp?: number): void
}
