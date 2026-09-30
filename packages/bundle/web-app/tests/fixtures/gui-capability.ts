/** Load the selected shipped profile layers around a real Agent and preset scope. */
import { readFileSync } from 'node:fs'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { Context } from '@cortex/cordis'
import Loader from '@cortex/cordis-plugin-loader'
import Include, { applyEntryPatches, type PatchOptions } from '@cortex/cordis-plugin-include'
import { loadOverlayPatches, PROFILE_TEMPLATES } from '@cortex/app-boot'
import AgentRegistry from '@cortex/agent'
import AgentLoop from '@cortex/agent-loop'
import AgentPreset from '@cortex/agent-preset'
import AgentPresets from '@cortex/agent-preset-registry'
import LlmRuntime from '@cortex/llm'
import SessionStore, { SessionId } from '@cortex/session'
import SessionProjections from '@cortex/session-projection'
import SystemPrompt from '@cortex/system-prompt'
import ToolRuntime from '@cortex/tools'
import { onTestFinished } from 'vitest'

export async function bootGuiCapability(options: {
  profile: string | undefined
  plugins: Record<string, unknown>
  bundles?: readonly string[]
  patches?: PatchOptions[]
}): Promise<Context> {
  const directory = await mkdtemp(join(tmpdir(), 'cortex-gui-capability-'))
  const ctx = new Context()
  onTestFinished(async () => {
    try { await ctx.fiber.dispose() }
    finally { await rm(directory, { recursive: true, force: true }) }
  })
  ctx.baseUrl = pathToFileURL(directory).href + '/'
  if (options.profile !== undefined) {
    ctx.provide('profileContext', {
      name: options.profile, dir: directory, patchPath: join(directory, 'cordis.patch.yml'),
      installAnchor: join(directory, 'package.json'), cwd: directory, home: directory,
      startedBundles: [], overlays: [], telemetryDisabledEnv: undefined,
    })
  }
  await ctx.plugin(Loader)
  Object.assign(ctx.loader.builtins, {
    include: Include, testLlm: LlmRuntime, testSessions: SessionStore,
    testProjections: SessionProjections, testPrompt: SystemPrompt, testTools: ToolRuntime,
    testAgents: AgentRegistry, testLoop: AgentLoop, testPresets: AgentPresets, testPreset: AgentPreset,
  })
  const bundles = options.bundles ?? PROFILE_TEMPLATES[options.profile ?? '']?.bundles ?? ['@cortex/base', '@cortex/web-app']
  const layers = bundles.flatMap((bundle) => {
    const base = new URL(`../../../${bundle.slice('@cortex/'.length)}/`, import.meta.url)
    const manifest = JSON.parse(readFileSync(new URL('package.json', base), 'utf8')) as {
      cortex: { bundle: { patch: string | string[] } }
    }
    const paths = manifest.cortex.bundle.patch
    return (Array.isArray(paths) ? paths : [paths]).flatMap(path =>
      loadOverlayPatches('gui-capability-defaults', fileURLToPath(new URL(path, base))))
  })
  const rows = applyEntryPatches([], [...layers, ...options.patches ?? []], (message) => { throw new Error(message) })
    .filter(row => row.name !== undefined && Object.hasOwn(options.plugins, row.name))
  for (const row of rows) ctx.loader.builtins[row.id] = options.plugins[row.name]
  const configPath = join(directory, 'cordis.yml')
  await writeFile(configPath, JSON.stringify([
    { name: 'cordis:testLlm' }, { name: 'cordis:testSessions' }, { name: 'cordis:testProjections' },
    { name: 'cordis:testPrompt' }, { name: 'cordis:testTools' }, { name: 'cordis:testAgents' },
    { name: 'cordis:testLoop', config: { agents: [] } },
    { name: 'cordis:testPresets', config: { default: 'standard' } },
    // Preserve the actual preset parent scope without unrelated coding tools.
    { name: 'cordis:testPreset', config: { id: 'standard', plugins: [] } },
    ...rows.map(row => ({ ...row, name: `cordis:${row.id}` })),
  ]))
  await ctx.loader.create({ name: 'cordis:include', config: { path: pathToFileURL(configPath).href } })
  await ctx.loader.await()
  for (const entry of ctx.loader.entries()) await entry.fiber?.await()
  return ctx
}

export async function standardAgentOn(ctx: Context, id: string) {
  return ctx.agents.create({
    sessionId: SessionId(id),
    setup: async (agentCtx: Context) => { await ctx.agentPresets.mount(agentCtx, 'standard') },
  })
}
