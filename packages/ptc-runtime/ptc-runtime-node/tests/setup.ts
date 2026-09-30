import { Context } from '@cortex/cordis'
import { onTestFinished } from 'vitest'
import SessionStore from '@cortex/session'
import FileSystem from '@cortex/fs-local'
import Subprocess from '@cortex/subprocess-local'
import Sandbox from '@cortex/sandbox-local'
import SandboxPolicy from '@cortex/sandbox-policy'
import SessionProjections from '@cortex/session-projection'
import type { SandboxMode } from '@cortex/sandbox'
import NodeRuntime from '../src/index.ts'
import type { Config } from '../src/index.ts'

export async function mountRuntime(ctx: Context, config: Config = {}, policy: { mode?: SandboxMode; workspaceRoot?: string } = {}) {
  onTestFinished(async () => { await ctx.fiber.dispose() })
  if (!ctx.get('sessions')) await ctx.plugin(SessionStore)
  if (!ctx.get('fs')) await ctx.plugin(FileSystem)
  if (!ctx.get('subprocess')) await ctx.plugin(Subprocess)
  if (!ctx.get('sandbox')) await ctx.plugin(Sandbox, {})
  if (!ctx.get('sessionProjections')) await ctx.plugin(SessionProjections)
  if (!ctx.get('sandboxPolicy')) await ctx.plugin(SandboxPolicy, { mode: 'danger-full-access', ...policy })
  await ctx.plugin(NodeRuntime, config)
  return ctx.ptcRuntime as NodeRuntime
}
