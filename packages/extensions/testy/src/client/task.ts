/** Follow explicitly started native work through its cancellable task record. */
import type { TestyInjected } from './source.ts'
import { record, resultValue, text, type RecordValue } from './wire.ts'

/**
 * Await a task started by a human action; immediate operations pass through unchanged.
 * @param invoke - native operation callback.
 * @param operation - catalog operation selected by the user.
 * @param args - reviewed native arguments.
 * @param onProgress - optional task observer for live run evidence and phase updates.
 * @returns the completed result, rejecting cancellation and native task failures.
 */
export async function executeTask(
  invoke: TestyInjected['invoke'], operation: string, args: RecordValue, onProgress?: (task: RecordValue) => void,
): Promise<RecordValue> {
  let value = record(resultValue(await invoke(operation, args)))
  const id = text(value.taskId)
  if (id) onProgress?.(value)
  while (id && value.running === true) {
    await new Promise<void>(resolve => setTimeout(resolve, 700))
    value = record(resultValue(await invoke('get_task', { taskId: id })))
    onProgress?.(value)
  }
  if (!id) return value
  if (value.error || value.status === 'cancelled' || value.status === 'failed') throw new Error(text(value.error) || text(value.status))
  return record(value.result)
}
