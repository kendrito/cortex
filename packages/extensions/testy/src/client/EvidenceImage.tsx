/** Raster evidence with its selected control drawn in the captured coordinate space. */
import type { JsonValue } from '../types.ts'
import { number, record } from './wire.ts'
import css from './Testy.module.css'

function bounds(value: JsonValue | undefined): { x: number; y: number; width: number; height: number } {
  if (Array.isArray(value)) return { x: number(value[0]), y: number(value[1]), width: number(value[2]), height: number(value[3]) }
  const box = record(value)
  return { x: number(box.x), y: number(box.y), width: number(box.width), height: number(box.height) }
}

/** Align physical screen coordinates with a target-only screenshot. */
export function EvidenceImage({ image, label, control, capture }: {
  image: string
  label: string
  control?: JsonValue | undefined
  capture?: JsonValue | undefined
}) {
  const target = bounds(control)
  const area = bounds(capture)
  const shown = area.width > 0 && area.height > 0 && target.width > 0 && target.height > 0
  return <div className={css.evidenceImage}>
    <img src={image} alt={label} />
    {shown && <div className={css.controlOutline} aria-hidden="true" style={{
      left: `${(target.x - area.x) / area.width * 100}%`, top: `${(target.y - area.y) / area.height * 100}%`,
      width: `${target.width / area.width * 100}%`, height: `${target.height / area.height * 100}%`,
    }} />}
  </div>
}
