/** Local Three.js scene. No remote assets, persistent animation loop, or model calls. */
import * as THREE from 'three';
import type { BrainEdge, BrainNode } from '../../../../agent-contract/brain';
import { BRAIN_COLORS, brainNeighbors } from './brain-model';
import type { BrainPoint3D, BrainProjection } from './brain-space';

export interface BrainFrame {
  points: BrainProjection[];
  width: number;
  height: number;
  zoom: number;
  axes: Array<{ name: string; x: number; y: number; depth: number }>;
}
interface NodeObject { id: string; mesh: THREE.Mesh<THREE.SphereGeometry, THREE.MeshStandardMaterial>; glow: THREE.Sprite; degree: number; radius: number; point: BrainPoint3D }
interface EdgeObject { source: string; target: string; line: THREE.Line<THREE.BufferGeometry, THREE.LineBasicMaterial> }
const INITIAL_YAW = -.48, INITIAL_PITCH = .26;

/** Owns the camera, recorded geometry and their disposable GPU resources. */
export class BrainRenderer {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.PerspectiveCamera(44, 1, 1, 5000);
  private readonly sphere = new THREE.SphereGeometry(1, 20, 14);
  private readonly texture: THREE.CanvasTexture;
  private readonly nodeObjects: NodeObject[] = [];
  private readonly edgeObjects: EdgeObject[] = [];
  private readonly selection: THREE.Sprite;
  private edges: readonly BrainEdge[] = [];
  private width = 800;
  private height = 450;
  private yaw = INITIAL_YAW;
  private pitch = INITIAL_PITCH;
  private zoom = 1;
  private target = new THREE.Vector3();
  private frameRequest = 0;
  private disposed = false;
  private selected: string | null = null;

  constructor(canvas: HTMLCanvasElement, private readonly onFrame: (frame: BrainFrame) => void) {
    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: 'low-power' });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    this.renderer.setClearColor(0x07101c, 0);
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;
    this.renderer.toneMapping = THREE.ACESFilmicToneMapping;
    this.renderer.toneMappingExposure = 1.35;
    this.scene.add(new THREE.AmbientLight(0x89a6c2, 1.6));
    const light = new THREE.DirectionalLight(0xffffff, 3.8); light.position.set(-180, 320, 480); this.scene.add(light);
    const rim = new THREE.DirectionalLight(0x76cde5, 2.4); rim.position.set(280, -80, -200); this.scene.add(rim);
    const image = document.createElement('canvas'); image.width = 96; image.height = 96;
    const context = image.getContext('2d');
    if (!context) { this.renderer.dispose(); throw new Error('Canvas graphics are unavailable'); }
    const gradient = context.createRadialGradient(48, 48, 0, 48, 48, 48);
    gradient.addColorStop(0, 'rgba(255,255,255,1)'); gradient.addColorStop(.12, 'rgba(255,255,255,.55)');
    gradient.addColorStop(.35, 'rgba(255,255,255,.13)'); gradient.addColorStop(1, 'rgba(255,255,255,0)');
    context.fillStyle = gradient; context.fillRect(0, 0, 96, 96);
    this.texture = new THREE.CanvasTexture(image);
    this.selection = new THREE.Sprite(new THREE.SpriteMaterial({ map: this.texture, color: 0xffffff, transparent: true, opacity: .85, blending: THREE.AdditiveBlending, depthWrite: false }));
    this.selection.visible = false; this.scene.add(this.selection);
  }

  /** Replace only the loaded graph; preserve the user's current orbit and zoom. */
  setGraph(nodes: readonly BrainNode[], edges: readonly BrainEdge[], points: ReadonlyMap<string, BrainPoint3D>): void {
    this.clearGraph(); this.edges = edges;
    for (const node of nodes) {
      const point = points.get(node.id); if (!point) continue;
      const color = new THREE.Color(BRAIN_COLORS[node.kind]);
      const radius = 6.5 + Math.min(6.5, Math.sqrt(point.degree) * 2);
      const mesh = new THREE.Mesh(this.sphere, new THREE.MeshStandardMaterial({ color, emissive: color, emissiveIntensity: .2, metalness: .22, roughness: .28 }));
      mesh.position.set(point.x, point.y, point.z); mesh.scale.setScalar(radius);
      const glow = new THREE.Sprite(new THREE.SpriteMaterial({ map: this.texture, color, transparent: true, opacity: .8, blending: THREE.AdditiveBlending, depthWrite: false }));
      glow.position.copy(mesh.position); glow.scale.setScalar(radius * 7);
      this.scene.add(mesh, glow); this.nodeObjects.push({ id: node.id, mesh, glow, radius, degree: point.degree, point });
    }
    const nodesById = new Map(nodes.map(node => [node.id, node]));
    for (const edge of edges) {
      const a = points.get(edge.source), b = points.get(edge.target);
      if (edge.targetStatus !== 'available' || !a || !b) continue;
      const geometry = new THREE.BufferGeometry();
      geometry.setAttribute('position', new THREE.Float32BufferAttribute([a.x, a.y, a.z, b.x, b.y, b.z], 3));
      const aColor = new THREE.Color(BRAIN_COLORS[nodesById.get(edge.source)!.kind]);
      const bColor = new THREE.Color(BRAIN_COLORS[nodesById.get(edge.target)!.kind]);
      geometry.setAttribute('color', new THREE.Float32BufferAttribute([...aColor.toArray(), ...bColor.toArray()], 3));
      const line = new THREE.Line(geometry, new THREE.LineBasicMaterial({ vertexColors: true, transparent: true, opacity: .42, depthWrite: false, blending: THREE.AdditiveBlending }));
      this.scene.add(line); this.edgeObjects.push({ source: edge.source, target: edge.target, line });
    }
    this.setSelected(this.selected);
  }

  setSelected(id: string | null): void {
    this.selected = id;
    const neighborhood = id ? brainNeighbors(id, this.edges) : null;
    for (const node of this.nodeObjects) {
      const muted = neighborhood !== null && !neighborhood.has(node.id);
      node.mesh.material.transparent = muted; node.mesh.material.opacity = muted ? .16 : 1;
      node.mesh.material.depthWrite = !muted;
      node.mesh.material.emissiveIntensity = node.id === id ? .75 : .2;
      node.glow.material.opacity = muted ? .07 : node.id === id ? 1.4 : .8;
      node.glow.scale.setScalar(node.radius * (node.id === id ? 10 : 7));
      if (node.id === id) { this.selection.position.copy(node.mesh.position); this.selection.scale.setScalar(node.radius * 4); }
    }
    this.selection.visible = id !== null && this.nodeObjects.some(node => node.id === id);
    for (const edge of this.edgeObjects) edge.line.material.opacity = id === null ? .42 : edge.source === id || edge.target === id ? .95 : .055;
    this.invalidate();
  }

  resize(width: number, height: number): void {
    this.width = Math.max(1, width); this.height = Math.max(1, height);
    this.renderer.setSize(this.width, this.height, false);
    this.camera.aspect = this.width / this.height; this.camera.updateProjectionMatrix(); this.invalidate();
  }

  orbit(dx: number, dy: number): void { this.yaw += dx; this.pitch = THREE.MathUtils.clamp(this.pitch + dy, -1.38, 1.38); this.invalidate(); }
  zoomBy(factor: number): void { this.zoom = THREE.MathUtils.clamp(this.zoom * factor, .5, 3.5); this.invalidate(); }
  pan(dx: number, dy: number): void {
    const scale = this.camera.position.distanceTo(this.target) * 2 * Math.tan(THREE.MathUtils.degToRad(this.camera.fov / 2)) / this.height;
    const right = new THREE.Vector3(1, 0, 0).applyQuaternion(this.camera.quaternion);
    const up = new THREE.Vector3(0, 1, 0).applyQuaternion(this.camera.quaternion);
    this.target.addScaledVector(right, -dx * scale).addScaledVector(up, dy * scale); this.invalidate();
  }
  reset(): void { this.yaw = INITIAL_YAW; this.pitch = INITIAL_PITCH; this.zoom = 1; this.target.set(0, 0, 0); this.invalidate(); }

  /** Schedule one frame; a resting view never consumes a continuous animation loop. */
  private invalidate(): void {
    if (this.disposed || this.frameRequest) return;
    this.frameRequest = requestAnimationFrame(() => { this.frameRequest = 0; this.draw(); });
  }

  private draw(): void {
    if (this.disposed) return;
    const verticalFov = THREE.MathUtils.degToRad(this.camera.fov / 2);
    const halfFov = Math.min(verticalFov, Math.atan(Math.tan(verticalFov) * this.camera.aspect));
    const distance = 252 / Math.sin(halfFov) / this.zoom;
    this.camera.position.set(Math.sin(this.yaw) * Math.cos(this.pitch) * distance, Math.sin(this.pitch) * distance, Math.cos(this.yaw) * Math.cos(this.pitch) * distance).add(this.target);
    this.camera.lookAt(this.target); this.camera.updateMatrixWorld();
    // Match the visible workspace with a volumetric ellipsoid. Spheres retain
    // their round shape; only positions and their recorded endpoints are fitted.
    const horizontal = THREE.MathUtils.clamp(this.camera.aspect * .8, .85, 1.85);
    const positions = new Map<string, THREE.Vector3>();
    for (const node of this.nodeObjects) {
      node.mesh.position.set(node.point.x * horizontal, node.point.y, node.point.z * 1.15);
      node.glow.position.copy(node.mesh.position); positions.set(node.id, node.mesh.position);
      if (node.id === this.selected) this.selection.position.copy(node.mesh.position);
    }
    for (const edge of this.edgeObjects) {
      const a = positions.get(edge.source)!, b = positions.get(edge.target)!;
      const attribute = edge.line.geometry.getAttribute('position');
      attribute.setXYZ(0, a.x, a.y, a.z); attribute.setXYZ(1, b.x, b.y, b.z); attribute.needsUpdate = true;
      edge.line.geometry.computeBoundingSphere();
    }
    this.renderer.render(this.scene, this.camera);
    const points = this.nodeObjects.map(node => {
      const screen = node.mesh.position.clone().project(this.camera);
      const depth = -node.mesh.position.clone().applyMatrix4(this.camera.matrixWorldInverse).z;
      return { id: node.id, x: (screen.x + 1) * this.width / 2, y: (1 - screen.y) * this.height / 2,
        radius: node.radius * this.height / (2 * Math.tan(verticalFov) * depth), degree: node.degree, depth,
        visible: screen.z > -1 && screen.z < 1 && Math.abs(screen.x) < .99 && Math.abs(screen.y) < .99 };
    }).sort((a, b) => a.depth - b.depth);
    // Hidden sphere centers must not receive overlay clicks through a foreground sphere.
    for (let i = 0; i < points.length; i++) {
      const point = points[i];
      if (points.slice(0, i).some(other => other.visible && Math.hypot(other.x - point.x, other.y - point.y) < other.radius * .9)) point.visible = false;
    }
    const inverse = this.camera.quaternion.clone().invert();
    const axes = [new THREE.Vector3(1, 0, 0), new THREE.Vector3(0, 1, 0), new THREE.Vector3(0, 0, 1)].map((axis, index) => {
      axis.applyQuaternion(inverse);
      return { name: ['x', 'y', 'z'][index], x: axis.x * 21, y: -axis.y * 21, depth: axis.z };
    });
    this.onFrame({ points, width: this.width, height: this.height, zoom: this.zoom, axes });
  }

  private clearGraph(): void {
    for (const node of this.nodeObjects.splice(0)) { this.scene.remove(node.mesh, node.glow); node.mesh.material.dispose(); node.glow.material.dispose(); }
    for (const edge of this.edgeObjects.splice(0)) { this.scene.remove(edge.line); edge.line.geometry.dispose(); edge.line.material.dispose(); }
  }

  dispose(): void {
    this.disposed = true; cancelAnimationFrame(this.frameRequest); this.clearGraph();
    this.sphere.dispose(); this.texture.dispose(); this.selection.material.dispose();
    this.renderer.dispose(); this.renderer.forceContextLoss();
  }
}
