/**
 * Code: the session's workspace opened in a real VS Code workbench, inline.
 *
 * Deliberately single-rooted: the embedded editor always opens EXACTLY the
 * active session's directory, never a multi-root workspace, so when the same
 * submodule exists in several capability checkouts there is no ambiguity about
 * which copy is open — it is the one this session runs in, and the banner
 * shows its absolute path the whole time. Switching sessions switches the
 * folder; nothing else changes.
 *
 * The workbench itself is the local editor sidecar the web profile starts
 * (code-server preferred, else `code serve-web`; see @cortex/web-app). The
 * view only frames it and reports state.
 */
import { useEffect, useRef, useState } from 'react'
import css from './CodeView.module.css'

/** The editor sidecar's local origin; @cortex/web-app spawns it on this port. */
export const EDITOR_ORIGIN = 'http://127.0.0.1:3082'

/** Injected face: the active session's working directory, when it has one. */
export interface CodeViewInjected {
  cwd: string | undefined
}

/**
 * The resolved GUI scheme, read off the stamp boot-theme writes and observed
 * for changes. The host mirrors the same commit into the sidecar's settings,
 * so a scheme flip re-keys the frame and the freshly loaded workbench reads
 * the freshly mirrored theme.
 */
function useResolvedScheme(): string {
  const [scheme, setScheme] = useState(() => document.documentElement.style.colorScheme || 'dark')
  useEffect(() => {
    const observer = new MutationObserver(() => {
      const next = document.documentElement.style.colorScheme || 'dark'
      setScheme(previous => previous === next ? previous : next)
    })
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['style'] })
    return () => { observer.disconnect() }
  }, [])
  return scheme
}

/** Sidecar reachability, probed because a dead iframe renders as pure blank. */
type EditorState = 'probing' | 'up' | 'down'

/** Probe the sidecar; opaque success is enough (no CORS contract needed). */
function useEditorState(): EditorState {
  const [state, setState] = useState<EditorState>('probing')
  useEffect(() => {
    let alive = true
    let timer: number | undefined
    const probe = (): void => {
      fetch(`${EDITOR_ORIGIN}/`, { mode: 'no-cors', cache: 'no-store' })
        .then(() => { if (alive) setState('up') })
        .catch(() => {
          if (!alive) return
          setState('down')
          timer = window.setTimeout(probe, 3000)
        })
    }
    probe()
    return () => { alive = false; if (timer !== undefined) window.clearTimeout(timer) }
  }, [])
  return state
}

/**
 * Render the embedded per-session editor view.
 * @param props.cwd - the active session's workspace directory.
 * @returns the editor frame with its single-root banner.
 */
export function CodeView({ cwd }: CodeViewInjected) {
  const editor = useEditorState()
  const scheme = useResolvedScheme()
  // Give the host's settings mirror a beat to land before the frame reloads.
  const [frameEpoch, setFrameEpoch] = useState(0)
  const mounted = useRef(false)
  useEffect(() => {
    if (!mounted.current) { mounted.current = true; return }
    const timer = window.setTimeout(() => { setFrameEpoch(epoch => epoch + 1) }, 700)
    return () => { window.clearTimeout(timer) }
  }, [scheme])

  if (cwd === undefined || cwd === '') {
    return (
      <div className={css['empty']}>
        <p>This session has no workspace directory yet — pick one and the editor opens it here.</p>
      </div>
    )
  }

  return (
    <div className={css['root']}>
      <div className={css['banner']}>
        <span className={css['dot']} data-state={editor} />
        <code className={css['path']} title={cwd}>{cwd}</code>
        <span className={css['single']}>single root</span>
        <a className={css['popout']} href={`vscode://file${cwd}`}>Open in VS Code</a>
      </div>
      {editor === 'down'
        ? (
          <div className={css['empty']}>
            <p>
              The editor sidecar is not answering on <code>{EDITOR_ORIGIN}</code>.
              It starts with <code>cortex web</code> when VS Code or code-server is
              installed; retrying quietly.
            </p>
          </div>
        )
        : (
          <iframe
            key={`${scheme}:${String(frameEpoch)}`}
            className={css['frame']}
            title={`Editor — ${cwd}`}
            src={`${EDITOR_ORIGIN}/?folder=${encodeURIComponent(cwd)}`}
            allow="clipboard-read; clipboard-write"
          />
        )}
    </div>
  )
}
