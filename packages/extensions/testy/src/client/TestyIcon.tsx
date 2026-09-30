/** Sidebar icon follows the standard single-pixel Cortex icon family. */
import { IconCheckCircleFillRegular } from '@cortex/client-ui-primitives'
import type { PropsRuntime } from '@cortex/client-ui-slots'

/** Render the Testy navigation mark at the shell-provided size. */
export function TestyIcon({ size }: PropsRuntime<'sidebar.panellist'>) {
  return <IconCheckCircleFillRegular size={size} />
}
