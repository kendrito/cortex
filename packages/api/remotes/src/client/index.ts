/** Platform-neutral assembly of generated Host Remote contributions. */

import type { Context } from '@cortex/cordis'
import agentPresetsRemote from '@cortex/agent-preset-registry/remote'
import userQuestionsRemote from '@cortex/user-questions/remote'
import commandsRemote from '@cortex/commands/remote'
import settingsControllerRemote from '@cortex/api-settings-controller/remote'
import officeToPdfRemote from '@cortex/office-to-pdf/remote'
import goalsRemote from '@cortex/goal/remote'
import scheduleRemote from '@cortex/schedule/remote'
import llmRemote from '@cortex/llm/remote'
import dynamicRemote from '@cortex/cordis-host-runner/remote'
import pluginManagerRemote from '@cortex/plugin-manager/remote'
import pluginRegistryProbeRemote from '@cortex/client-ui-plugin-manager/remote'
import pluginInventoryRemote from '@cortex/host-plugin-inventory/remote'
import messageFeedbackRemote from '@cortex/message-feedback/remote'
import permissionPresetsRemote from '@cortex/permission-presets/remote'
import sessionFeedbackRemote from '@cortex/command-feedback/remote'
import fileUploadsRemote from '@cortex/client-file-upload/remote'
import sessionReferencesRemote from '@cortex/session-reference/remote'
import subagentsRemote from '@cortex/subagent/remote'
import sessionRemote from '@cortex/api-session-controller/remote'
import jobRemote from '@cortex/api-job-controller/remote'
import workspaceRemote from '@cortex/api-workspace-controller/remote'
import terminalRemote from '@cortex/api-terminal-controller/remote'
import workspaceFilesRemote from '@cortex/api-workspace-files/remote'
import type { ClientRemote } from '@cortex/api-gateway/client'

export type { ClientRemote } from '@cortex/api-gateway/client'
export type {
  BundleInfo, BundleRowInfo, ChangeResult, IncompatiblePlugin, InspectOptions, InstallBundleOptions, InstallSpecKind, ManagementError,
  PackageResult,
  PluginChange, PluginEntryId, PluginInfo, PluginInspectProblem, PluginInstallCancellation, PluginInstallFailureKind,
  PluginInstallLogChunk, PluginInstallProgress, PluginInstallRequestId, PluginRegistries, PluginSpecInspection, ReadOnlyReason, Registry,
} from '@cortex/plugin-manager/types'
export type {} from '@cortex/plugin-manager/remote'
export type {} from '@cortex/client-ui-plugin-manager/remote'
export type { PluginInventorySnapshot } from '@cortex/host-plugin-inventory/types'
export type {} from '@cortex/agent-preset-registry/remote'
export type {} from '@cortex/user-questions/remote'
export type {} from '@cortex/commands/remote'
export type {} from '@cortex/api-settings-controller/remote'
export type {} from '@cortex/goal/remote'
export type {} from '@cortex/schedule/remote'
export type {} from '@cortex/office-to-pdf/remote'
export type {} from '@cortex/llm/remote'
export type {} from '@cortex/host-plugin-inventory/remote'
export type {} from '@cortex/message-feedback/remote'
export type {} from '@cortex/permission-presets/remote'
export type {} from '@cortex/command-feedback/remote'
export type {} from '@cortex/client-file-upload/remote'
export type {} from '@cortex/session-reference/remote'
export type {} from '@cortex/subagent/remote'
export type * from '@cortex/subagent/client'
export type {} from '@cortex/api-session-controller/remote'
export type * from '@cortex/api-session-controller/types'
export type {} from '@cortex/api-job-controller/remote'
export type * from '@cortex/api-job-controller/types'
export type {} from '@cortex/api-workspace-controller/remote'
export type * from '@cortex/api-workspace-controller/types'
export type {} from '@cortex/api-workspace-files/remote'
export type * from '@cortex/api-workspace-files/types'
export type {} from '@cortex/api-terminal-controller/remote'
export type * from '@cortex/api-terminal-controller/types'
// The forwarded-event allowlist's selection seat: without it in the consumer's
// compilation face `TypertRemoteEvent` is `never` and every `$on` call fails.
export type { ApiRemoteForwardedEvent } from '../types.ts'
// The owner packages' client-safe `./types` exports supply the `Events`
// signatures `$on` hands to a listener, so a consumer reads the very
// declaration the Host emits rather than a flattened restatement of it.
export type {} from '@cortex/commands/types'
export type {} from '@cortex/cordis-host-runner/types'
export type {} from '@cortex/credentials/types'
export type {} from '@cortex/llm/types'
export type {} from '@cortex/agent-preset-registry/types'
export type {} from '@cortex/permission-presets/types'
export type {} from '@cortex/settings/types'
export type {} from '@cortex/user-approval/types'
export type {} from '@cortex/user-questions/types'
export type {} from '@cortex/api-session-controller/types'

/**
 * The carrier's Client-facing types, re-exported so a business package names one
 * assembly package instead of both this facade and the Connection plugin. Type-only:
 * the carrier's runtime values stay behind their own module edge.
 */
export type {
  ConnectionHandle, ConnectionSinks, ContentBlock,
  MessageId,
  RpcId, RpcRequest, RpcResponse, RpcResult, SessionId,
  StreamChunk,
} from '@cortex/client-connection/client'
export type {} from '@cortex/api-gateway/client'
export type {} from '@cortex/cordis-host-runner/remote'

// The payload vocabulary of the selected namespaces, re-exported so a Client
// contribution can name what it sends and receives without importing a Host
// package: this assembly is the one place both planes legitimately meet.
export type {
  ApprovalRequestId,
  CordisHalfState,
  CordisDynamicPackageId,
  CordisDynamicPluginId,
  CordisDynamicPluginRunId,
  CordisDynamicRunMode,
  CordisInspectMethodManifest,
  CordisInspectPlatform,
  CordisInspectProviderManifest,
  CordisInspectProviderView,
  CordisInspectQueryRequest,
  CordisInspectQueryResolution,
  CordisInspectQueryResolved,
  CordisInspectRequestId,
  CordisInspectResolveAck,
  CordisRunDiagnostic,
  CordisRunStatus,
  DynamicCordisClientSource,
  DynamicCordisHostHalfResult,
  DynamicCordisInventoryRow,
  DynamicCordisInvokeResult,
  DynamicCordisPackage,
  DynamicCordisRequestResolved,
  DynamicCordisResolveAck,
  DynamicCordisRetracted,
  DynamicCordisRunRequest,
  DynamicCordisRunResolution,
  DynamicCordisRunAttempt,
  DynamicCordisRunResponse,
  DynamicCordisStopResponse,
  DynamicCordisUndefineReceipt,
  RequestRunOutcome,
} from '@cortex/cordis-host-runner/types'
// Credential state vocabulary for the credentials namespace (values never ride it).
export type { CredentialInfo } from '@cortex/credentials/types'
// Redacted namespace vocabulary for the settings namespace (secrets never ride
// it). It travels with its seam, whose `./types` the Client face already reads.
export type {
  SettingsDescribeValue, SettingsNamespaceView, SettingsPathOpView, SettingsSecretView,
} from '@cortex/settings/types'
// Provider registry and discovery vocabulary for the llm namespace.
export type {
  LlmConfigurableProvider, LlmDiscoveredModel,
  LlmModelDiscoveryRequest, LlmProviderInfo,
} from '@cortex/llm/types'
// Reference-discovery result vocabulary for the fileReferences and
// sessionReferenceResolver namespaces.
export type { FileReferenceCandidate } from '@cortex/file-reference/types'
export type { SessionReferenceMentionCandidate } from '@cortex/session-reference/types'

// The Remote failure vocabulary, re-exported so business packages keep naming
// this assembly alone. Types only: a value export would make spec imports load
// this module's owner /remote artifacts; specs take RemoteError from
// cortex-client-test-runtime instead.
export type {
  RemoteErrorCode, RemoteErrorDetailsMap, RemoteFailure, RemoteResult,
} from '@cortex/typert-protocol'
export type { RemoteHostFacts } from '@cortex/api-gateway/client'

declare module '@cortex/cordis' {
  interface Context {
    /** Generated Remote namespaces selected by this Client assembly. */
    remote: ClientRemote
  }
}

/** Required service: the typed Client Remote contribution mount. */
export const inject = ['remote']

/**
 * Mount the Host capabilities explicitly selected for this Client assembly.
 * @param ctx - Client Cordis root carrying the typed API service.
 * @returns disposer after every selected Remote namespace is ready.
 */
export async function apply(ctx: Context): Promise<() => Promise<void>> {
  const disposers: Array<() => Promise<void>> = []
  try {
    for (const contribution of [
      agentPresetsRemote, commandsRemote, settingsControllerRemote,
      goalsRemote, llmRemote, dynamicRemote, scheduleRemote,
      pluginInventoryRemote, pluginManagerRemote, pluginRegistryProbeRemote, messageFeedbackRemote, sessionFeedbackRemote,
      fileUploadsRemote, sessionReferencesRemote,
      permissionPresetsRemote, subagentsRemote, sessionRemote, jobRemote, workspaceRemote, workspaceFilesRemote, terminalRemote,
      officeToPdfRemote, userQuestionsRemote,
    ]) {
      disposers.push(await ctx.remote.$mount(contribution))
    }
  } catch (error) {
    for (const dispose of disposers.reverse()) await dispose()
    throw error
  }
  // Unwound in reverse mount order, so a namespace never outlives one mounted
  // after it.
  return async () => {
    for (const dispose of disposers.reverse()) await dispose()
  }
}
