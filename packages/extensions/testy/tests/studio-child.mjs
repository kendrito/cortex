/** Studio-shaped fixture with one long-lived native-worker-shaped descendant. */
import { spawn } from 'node:child_process'
const worker = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { windowsHide: true, stdio: 'ignore' })
worker.once('spawn', () => process.stdout.write(`${worker.pid}\n`))
setInterval(() => {}, 1000)
