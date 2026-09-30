/** Cortex retains the message-occurrence seam without product-event collection. */
import type { Context } from '@cortex/cordis'
import type { MessageSubmission } from '../contract/composer-submission.ts'

/**
 * Discard product analytics under Cortex's no-telemetry policy.
 * @param _ctx - client context, never read for a collector.
 * @param _submission - submitted occurrence, never stored or transmitted here.
 */
export function reportMessageSubmission(_ctx: Context, _submission: MessageSubmission): void {}
