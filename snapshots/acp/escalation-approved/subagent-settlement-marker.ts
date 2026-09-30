import { writeFileSync } from 'node:fs'
import { join } from 'node:path'
import type { Context } from '@cortex/cordis'
import type {} from '@cortex/subagent'

export const name = 'subagent-settlement-marker'

/** Publish a workspace marker after a subagent lifecycle end. */
export function apply(ctx: Context): void {
  ctx.on('subagent/end', () => {
    writeFileSync(join(process.cwd(), '.cortex-snapshot-subagent-settled'), '')
  })
}
