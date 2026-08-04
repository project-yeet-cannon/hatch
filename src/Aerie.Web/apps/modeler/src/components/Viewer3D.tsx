import { useEffect, useMemo, useRef, useState } from 'react';
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { generateBuildingSolids } from '../model/solidGeneration';
import type { BuildingSolids, GeneratedMesh } from '../model/solidGeneration';
import type { ProjectDocument } from '../model/schema';

interface Viewer3DProps {
  project: ProjectDocument;
}

const AIR_VOLUME_COLOR = 0x4a90d9;
const WALL_COLOR = 0xc8beae;

function toBufferGeometry(mesh: GeneratedMesh): THREE.BufferGeometry {
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.BufferAttribute(mesh.positions, 3));
  geometry.setIndex(new THREE.BufferAttribute(mesh.indices, 1));
  geometry.computeVertexNormals();
  return geometry;
}

/**
 * The live 3D view (spec #7: "render a 3D view of the home to its best
 * knowledge at any time"). Generation is explicit ("Regenerate") rather than
 * tied to every keystroke elsewhere in the app - CSG is comparatively
 * expensive and there's no reason to re-run it while the user is mid-edit on
 * a 2D sketch. Model coordinates are meters, X/Y plan, Z up throughout (see
 * solidGeneration.ts); the three.js scene is set up to match that directly
 * (camera.up = Z) rather than remapping into three's default Y-up.
 */
export function Viewer3D({ project }: Viewer3DProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const cameraRef = useRef<THREE.PerspectiveCamera | null>(null);
  const controlsRef = useRef<OrbitControls | null>(null);
  const modelGroupRef = useRef<THREE.Group | null>(null);
  const framedRef = useRef(false);

  const [solids, setSolids] = useState<BuildingSolids | null>(null);
  const [status, setStatus] = useState<'idle' | 'loading' | 'error'>('idle');
  const [error, setError] = useState<string | null>(null);
  const [hiddenFloors, setHiddenFloors] = useState<Set<number>>(new Set());
  const [showAirVolume, setShowAirVolume] = useState(true);
  const [showWalls, setShowWalls] = useState(true);

  async function regenerate() {
    setStatus('loading');
    setError(null);
    try {
      const result = await generateBuildingSolids(project);
      setSolids(result);
      setStatus('idle');
      framedRef.current = false;
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to generate the 3D model.');
      setStatus('error');
    }
  }

  // Generate once when the viewer is first opened; after that it's manual
  // (the Regenerate button) since re-running CSG on every 2D edit would be
  // both wasteful and janky mid-edit.
  useEffect(() => {
    regenerate();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Scene/renderer/controls setup, once.
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const scene = new THREE.Scene();

    const camera = new THREE.PerspectiveCamera(50, 1, 0.1, 1000);
    camera.up.set(0, 0, 1);
    camera.position.set(10, -10, 8);
    cameraRef.current = camera;

    const renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(window.devicePixelRatio || 1);
    container.appendChild(renderer.domElement);

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controlsRef.current = controls;

    scene.add(new THREE.AmbientLight(0xffffff, 0.6));
    const sun = new THREE.DirectionalLight(0xffffff, 0.8);
    sun.position.set(6, -8, 12);
    scene.add(sun);

    const grid = new THREE.GridHelper(60, 60);
    grid.rotateX(Math.PI / 2); // GridHelper is built flat in three's XZ plane; rotate it onto our XY (Z-up) ground plane.
    scene.add(grid);

    const modelGroup = new THREE.Group();
    modelGroupRef.current = modelGroup;
    scene.add(modelGroup);

    let raf = 0;
    const renderLoop = () => {
      controls.update();
      renderer.render(scene, camera);
      raf = requestAnimationFrame(renderLoop);
    };
    renderLoop();

    const resize = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (!entry) return;
      const { width, height } = entry.contentRect;
      if (width === 0 || height === 0) return;
      camera.aspect = width / height;
      camera.updateProjectionMatrix();
      renderer.setSize(width, height);
    });
    resize.observe(container);

    return () => {
      cancelAnimationFrame(raf);
      resize.disconnect();
      controls.dispose();
      renderer.dispose();
      container.removeChild(renderer.domElement);
      modelGroupRef.current = null;
      cameraRef.current = null;
      controlsRef.current = null;
    };
  }, []);

  // Rebuild the visible meshes whenever the generated solids or a toggle changes.
  useEffect(() => {
    const group = modelGroupRef.current;
    if (!group) return;

    for (const child of [...group.children]) {
      group.remove(child);
      const mesh = child as THREE.Mesh;
      mesh.geometry.dispose();
      (mesh.material as THREE.Material).dispose();
    }

    if (solids) {
      const airMaterial = new THREE.MeshStandardMaterial({ color: AIR_VOLUME_COLOR, transparent: true, opacity: 0.35, side: THREE.DoubleSide, depthWrite: false });
      const wallMaterial = new THREE.MeshStandardMaterial({ color: WALL_COLOR, side: THREE.DoubleSide });

      for (const floor of solids.floors) {
        if (hiddenFloors.has(floor.floorIndex)) continue;
        if (showAirVolume && floor.airVolume) group.add(new THREE.Mesh(toBufferGeometry(floor.airVolume), airMaterial));
        if (showWalls && floor.walls) group.add(new THREE.Mesh(toBufferGeometry(floor.walls), wallMaterial));
      }
    }

    if (!framedRef.current) {
      framedRef.current = true;
      frameCameraToGroup();
    }
  }, [solids, hiddenFloors, showAirVolume, showWalls]);

  function frameCameraToGroup() {
    const group = modelGroupRef.current;
    const camera = cameraRef.current;
    const controls = controlsRef.current;
    if (!group || !camera || !controls) return;
    const box = new THREE.Box3().setFromObject(group);
    if (box.isEmpty()) return;
    const center = box.getCenter(new THREE.Vector3());
    const size = box.getSize(new THREE.Vector3());
    const radius = Math.max(size.x, size.y, size.z, 1) * 1.2;
    controls.target.copy(center);
    camera.position.set(center.x + radius, center.y - radius, center.z + radius * 0.8);
    camera.near = radius / 100;
    camera.far = radius * 50;
    camera.updateProjectionMatrix();
    controls.update();
  }

  const floors = useMemo(() => solids?.floors ?? [], [solids]);

  return (
    <div className="viewer3d">
      <div className="viewer3d-toolbar">
        <button className="btn-primary" onClick={regenerate} disabled={status === 'loading'}>
          {status === 'loading' ? 'Generating…' : 'Regenerate 3D model'}
        </button>
        <label className="viewer3d-toggle">
          <input type="checkbox" checked={showAirVolume} onChange={(e) => setShowAirVolume(e.target.checked)} />
          Air volume
        </label>
        <label className="viewer3d-toggle">
          <input type="checkbox" checked={showWalls} onChange={(e) => setShowWalls(e.target.checked)} />
          Walls
        </label>
        {floors.map((floor) => (
          <label key={floor.floorIndex} className="viewer3d-toggle" title={floor.name}>
            <input
              type="checkbox"
              checked={!hiddenFloors.has(floor.floorIndex)}
              onChange={(e) =>
                setHiddenFloors((prev) => {
                  const next = new Set(prev);
                  if (e.target.checked) next.delete(floor.floorIndex);
                  else next.add(floor.floorIndex);
                  return next;
                })
              }
            />
            {floor.name}
          </label>
        ))}
        {status === 'error' && <span className="viewer3d-error">{error}</span>}
      </div>
      {solids && solids.warnings.length > 0 && <p className="text-muted viewer3d-warnings">{solids.warnings.join(' ')}</p>}
      <div ref={containerRef} className="viewer3d-canvas-container" />
    </div>
  );
}
