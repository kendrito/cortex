import { clientBundle } from '../../client/tsdown.client.ts'

export default clientBundle(
  '@cortex/api-workspace-files',
  ['lib/types/index.js'],
  { hostPhase: true },
)
