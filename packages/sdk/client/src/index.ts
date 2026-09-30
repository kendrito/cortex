/**
 * TypeScript client SDK for the Cortex runtime: spawn the
 * same-version `cortex --profile sdk` runtime as a subprocess and drive agent
 * turns over stdio JSON-RPC. `CortexHarness` is the high-level run API;
 * `HarnessClient` is the lower-level protocol client. A pure library — it
 * registers nothing on a Cordis context; named profiles and ordered patch
 * files customize the runtime process it spawns.
 *
 * @module @cortex/sdk-client
 */

export { CortexHarness, HarnessSession } from './api.ts'
export type { RunOptions } from './api.ts'
export {
  HarnessClient,
  RequestTimeoutError,
  SdkProtocolError,
  TransportClosedError,
} from './client.ts'
export type { NotificationSubscription } from './client.ts'
export { JsonRpcResponseError } from '@cortex/sdk-protocol'
export type {
  ContentBlock,
  SdkPromptContentBlock,
  CortexHarnessOptions,
  HarnessClientOptions,
  HarnessNotification,
  NotificationFilter,
  RunResult,
} from './types.ts'
