/** Accessible 3D orbit surface over a locally bundled, on-demand WebGL scene. */
import { useEffect, useMemo, useRef, useState } from 'preact/hooks';
import type { BrainEdge, BrainNode } from '../../../../agent-contract/brain';
import { BRAIN_COLORS, brainNeighbors } from './brain-model';
import { layoutBrain3D, placeBrainLabels } from './brain-space';
import type { BrainFrame, BrainRenderer } from './brain-renderer';
import { bt } from './brain-copy';

interface Props { nodes: BrainNode[]; edges: BrainEdge[]; selected: string | null; onSelect: (id: string | null) => void; onList: () => void }
const EMPTY_FRAME: BrainFrame = { points: [], axes: [], width: 800, height: 450, zoom: 1 };

/** @returns An orbitable evidence volume with precise projected node buttons. */
export function BrainGraph({ nodes, edges, selected, onSelect, onList }: Props) {
  const surface = useRef<HTMLDivElement>(null), canvas = useRef<HTMLCanvasElement>(null), svg = useRef<SVGSVGElement>(null);
  const renderer = useRef<BrainRenderer | null>(null);
  const pointers = useRef(new Map<number, { x: number; y: number; pan: boolean }>());
  const [frame, setFrame] = useState<BrainFrame>(EMPTY_FRAME);
  const [ready, setReady] = useState(false), [failed, setFailed] = useState(false), [hovered, setHovered] = useState<string | null>(null);
  const geometry = useMemo(() => layoutBrain3D(nodes, edges), [nodes, edges]);
  const latest = useRef({ nodes, edges, geometry, selected }); latest.current = { nodes, edges, geometry, selected };
  const neighbors = useMemo(() => selected ? brainNeighbors(selected, edges) : null, [selected, edges]);
  const byId = useMemo(() => new Map(nodes.map(node => [node.id, node])), [nodes]);
  // A filter can render before the pending GPU frame; never expose stale node IDs.
  const currentPoints = useMemo(() => frame.points.filter(point => byId.has(point.id)), [frame, byId]);
  const labels = useMemo(() => placeBrainLabels(nodes, currentPoints, selected ?? hovered, frame.width, frame.height), [nodes, currentPoints, frame, selected, hovered]);
  const visible = currentPoints.filter(point => point.visible);
  const tabTarget = visible.some(point => point.id === selected) ? selected : visible[0]?.id;
  const targets = new Map(visible.map(point => {
    let nearest = Infinity;
    for (const other of visible) if (other.id !== point.id) nearest = Math.min(nearest, Math.hypot(other.x - point.x, other.y - point.y));
    return [point.id, Math.max(2, Math.min(18, Math.max(8, point.radius + 5), nearest / 2 - 1))];
  }));

  useEffect(() => {
    const element = surface.current, drawing = canvas.current;
    if (!element || !drawing) return;
    let disposed = false;
    const observer = new ResizeObserver(entries => {
      const rect = entries[0]?.contentRect;
      if (rect?.width && rect.height) renderer.current?.resize(rect.width, rect.height);
    });
    observer.observe(element);
    const wheel = (event: WheelEvent) => { event.preventDefault(); renderer.current?.zoomBy(Math.exp(-event.deltaY * .0016)); };
    element.addEventListener('wheel', wheel, { passive: false });
    const lost = (event: Event) => {
      event.preventDefault(); if (disposed) return;
      const scene = renderer.current; renderer.current = null;
      scene?.dispose(); setReady(false); setFailed(true);
    };
    drawing.addEventListener('webglcontextlost', lost);
    void import('./brain-renderer').then(({ BrainRenderer }) => {
      if (disposed) return;
      try {
        const scene = new BrainRenderer(drawing, setFrame); renderer.current = scene;
        const current = latest.current;
        scene.setGraph(current.nodes, current.edges, current.geometry); scene.setSelected(current.selected);
        scene.resize(element.clientWidth, element.clientHeight); setReady(true);
      } catch (error) {
        renderer.current?.dispose(); renderer.current = null;
        // Graphics support is a browser capability; List remains a complete data view.
        console.warn('Brain 3D graphics unavailable', error instanceof Error ? error.message : 'Graphics initialization failed');
        setFailed(true);
      }
    }).catch(() => { if (!disposed) setFailed(true); });
    return () => { disposed = true; observer.disconnect(); drawing.removeEventListener('webglcontextlost', lost); element.removeEventListener('wheel', wheel); renderer.current?.dispose(); renderer.current = null; pointers.current.clear(); };
  }, []);
  useEffect(() => { renderer.current?.setGraph(nodes, edges, geometry); }, [nodes, edges, geometry]);
  useEffect(() => { renderer.current?.setSelected(selected); }, [selected]);

  const moveFocus = (id: string, key: string) => {
    const point = currentPoints.find(candidate => candidate.id === id);
    if (!point) return;
    const direction = key === 'ArrowRight' ? [1, 0] : key === 'ArrowLeft' ? [-1, 0] : key === 'ArrowUp' ? [0, -1] : [0, 1];
    let next = '', score = Infinity;
    for (const other of visible) {
      const dx = other.x - point.x, dy = other.y - point.y;
      if (dx * direction[0] + dy * direction[1] <= 0) continue;
      const distance = Math.hypot(dx, dy) + Math.abs(dx * direction[1] - dy * direction[0]) * 2;
      if (distance < score) { next = other.id; score = distance; }
    }
    if (next) {
      onSelect(next);
      [...(svg.current?.querySelectorAll<SVGCircleElement>('[data-brain-node]') ?? [])].find(node => node.dataset.brainNode === next)?.focus();
    }
  };
  const keyboard = (event: KeyboardEvent) => {
    if (event.key === 'Escape') onSelect(null);
    else if (event.key === 'ArrowLeft') renderer.current?.orbit(-.12, 0);
    else if (event.key === 'ArrowRight') renderer.current?.orbit(.12, 0);
    else if (event.key === 'ArrowUp') renderer.current?.orbit(0, .1);
    else if (event.key === 'ArrowDown') renderer.current?.orbit(0, -.1);
    else if (event.key === '+' || event.key === '=') renderer.current?.zoomBy(1.2);
    else if (event.key === '-') renderer.current?.zoomBy(1 / 1.2);
    else if (event.key === 'Home' || event.key === '0') renderer.current?.reset();
    else return;
    event.preventDefault();
  };

  return <>
    <div ref={surface} class="brain__space" data-brain-renderer="three" onContextMenu={event => event.preventDefault()}
      onKeyDown={keyboard}
      onPointerDown={event => {
        if ((event.target as Element).closest('[data-brain-node]')) return;
        pointers.current.set(event.pointerId, { x: event.clientX, y: event.clientY, pan: event.shiftKey || event.button === 2 });
        event.currentTarget.setPointerCapture(event.pointerId);
      }}
      onPointerMove={event => {
        const previous = pointers.current.get(event.pointerId); if (!previous) return;
        const before = [...pointers.current.values()];
        pointers.current.set(event.pointerId, { ...previous, x: event.clientX, y: event.clientY });
        const after = [...pointers.current.values()];
        if (after.length === 2) {
          const oldDistance = Math.hypot(before[0].x - before[1].x, before[0].y - before[1].y);
          const newDistance = Math.hypot(after[0].x - after[1].x, after[0].y - after[1].y);
          if (oldDistance > 0) renderer.current?.zoomBy(newDistance / oldDistance);
        } else if (previous.pan || event.shiftKey) renderer.current?.pan(event.clientX - previous.x, event.clientY - previous.y);
        else renderer.current?.orbit(-(event.clientX - previous.x) * .008, (event.clientY - previous.y) * .007);
      }}
      onPointerUp={event => pointers.current.delete(event.pointerId)} onPointerCancel={event => pointers.current.delete(event.pointerId)}>
      <canvas ref={canvas} class="brain__webgl" aria-hidden="true" />
      {ready && <svg ref={svg} class="brain__projection" viewBox={`0 0 ${frame.width} ${frame.height}`} aria-label={bt('graph3d')} aria-describedby="brain-keyboard" tabindex={0}>
        <g aria-hidden="true">{labels.map(label => <text key={label.id} class="brain__node-label" x={label.x} y={label.y} text-anchor={label.anchor}
          opacity={neighbors && !neighbors.has(label.id) ? .3 : 1}>{label.text}</text>)}</g>
        {[...visible].reverse().map(point => {
          const node = byId.get(point.id)!;
          return <circle key={node.id} role="button" data-brain-node={node.id} tabindex={node.id === tabTarget ? 0 : -1}
            class="brain__node-target" cx={point.x} cy={point.y} r={targets.get(node.id)} fill="transparent"
            aria-label={`${bt('select', { title: node.title })}, ${bt(node.kind)}, ${bt('count', { n: point.degree })}`} aria-pressed={selected === node.id}
            style={{ '--node-color': BRAIN_COLORS[node.kind] }}
            onPointerEnter={() => setHovered(node.id)} onPointerLeave={() => setHovered(null)}
            onClick={() => onSelect(node.id)} onKeyDown={event => {
              if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); event.stopPropagation(); onSelect(node.id); }
              if (event.key.startsWith('Arrow') && !event.altKey) { event.preventDefault(); event.stopPropagation(); moveFocus(node.id, event.key); }
            }}><title>{node.title}</title></circle>;
        })}
      </svg>}
      {ready && <div class="brain__space-label" aria-hidden="true"><span class="brain__space-cube">◇</span>{bt('orbitView')}</div>}
      {ready && <svg class="brain__compass" viewBox="0 0 76 76" aria-hidden="true"><circle cx="38" cy="38" r="31" fill="#0c1824c0" stroke="#31485b" stroke-width=".6" />
        {[...frame.axes].sort((a, b) => a.depth - b.depth).map(axis => <g key={axis.name} opacity={axis.depth < 0 ? .42 : 1}>
          <line x1="38" y1="38" x2={38 + axis.x} y2={38 + axis.y} stroke={axis.name === 'x' ? '#e49385' : axis.name === 'y' ? '#93c9ac' : '#77cbd6'} stroke-width="1.3" />
          <text x={38 + axis.x * 1.25} y={42 + axis.y * 1.25} text-anchor="middle" fill="#b6c7d5" font-size="9">{axis.name}</text>
        </g>)}<circle cx="38" cy="38" r="2" fill="#8aa3b8" /></svg>}
      {!ready && !failed && <div class="brain__space-loading" role="status">{bt('opening3d')}</div>}
      {failed && <div class="brain__space-loading" role="status"><p>{bt('graphicsUnavailable')}</p><button class="brain__button" onClick={onList}>{bt('openList')}</button></div>}
    </div>
    <div class="brain__canvas-footer"><p class="brain__hint">{bt('guidance3d')}</p><div class="brain__camera-controls">
      <div class="brain__orbit-buttons"><button type="button" aria-label={bt('orbitLeft')} title={bt('orbitLeft')} onClick={() => renderer.current?.orbit(-.22, 0)}>↶</button>
        <button type="button" aria-label={bt('orbitRight')} title={bt('orbitRight')} onClick={() => renderer.current?.orbit(.22, 0)}>↷</button></div>
      <div class="brain__zoom"><button type="button" title={bt('zoomOut')} aria-label={bt('zoomOut')} onClick={() => renderer.current?.zoomBy(1 / 1.2)}>−</button>
        <output aria-label={bt('graph3d')}>{Math.round(frame.zoom * 100)}%</output>
        <button type="button" title={bt('zoomIn')} aria-label={bt('zoomIn')} onClick={() => renderer.current?.zoomBy(1.2)}>+</button>
        <button type="button" title={bt('reset3d')} aria-label={bt('reset3d')} onClick={() => renderer.current?.reset()}>↺</button>
      </div></div></div><span id="brain-keyboard" class="brain__screenreader">{bt('keyboard3d')}</span>
  </>;
}
