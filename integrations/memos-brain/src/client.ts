import { createElement, type ReactNode } from 'react';
import type { Context } from '@cortex/cordis';
import type { MainPanelId } from '@cortex/client-ui-layout/client';
import type { PropsRuntime } from '@cortex/client-ui-slots';
import type {} from '@cortex/client-ui-sidebar/client';
import type {} from '@cortex/client-ui-renderer/client';

export const name = '@cortex/memos-brain';
export const inject = ['slots'];
export const PANEL_ID = 'memos-brain' as MainPanelId;
export const VIEW_URL = '/api/memos-brain/view/brain.html';

export function BrainPanel(): ReactNode {
  return createElement('iframe', {
    src: VIEW_URL,
    title: 'MemOS Brain',
    sandbox: 'allow-scripts allow-same-origin',
    style: { display: 'block', width: '100%', height: '100%', minHeight: 0, border: 0, background: '#101925' },
  });
}

export function BrainIcon({ size }: PropsRuntime<'sidebar.panellist'>): ReactNode {
  return createElement('svg', { width: size, height: size, viewBox: '0 0 24 24', fill: 'none', 'aria-hidden': true },
    createElement('path', { d: 'M5 16 9 6l10 5-5 8-9-3m4-10 5 13M5 16l14-5', stroke: 'currentColor', strokeWidth: 1.25, strokeLinejoin: 'round' }),
    ...[[5, 16], [9, 6], [19, 11], [14, 19]].map(([cx, cy]) => createElement('circle', { key: `${cx}-${cy}`, cx, cy, r: 2, fill: 'currentColor' })),
  );
}

export function apply(ctx: Context): void {
  ctx.slots.inject('main', () => ctx.slots.register({ name: 'main', key: PANEL_ID }, BrainPanel));
  ctx.slots.inject('sidebar.panellist', () => ctx.slots.register({
    name: 'sidebar.panellist', id: PANEL_ID, order: 2, label: () => 'Brain',
  }, BrainIcon));
}
