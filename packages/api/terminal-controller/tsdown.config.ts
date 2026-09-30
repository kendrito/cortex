import { clientBundle } from '../../client/tsdown.client.ts'

export default clientBundle(
  '@cortex/api-terminal-controller',
  ['lib/types/index.js'],
  { hostPhase: true },
)
