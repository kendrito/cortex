import { clientBundle } from '../../client/tsdown.client.ts'

export default clientBundle(
  '@cortex/api-job-controller',
  ['lib/types/index.js'],
  { hostPhase: true },
)
