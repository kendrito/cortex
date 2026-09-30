import { clientBundle } from '../tsdown.client.ts'

export default clientBundle(
  '@cortex/client-shortcuts',
  ['lib/types/index.js', 'lib/types/protocol.js'],
  { hostPhase: true },
)
