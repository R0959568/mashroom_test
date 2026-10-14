import {
  ACESFilmicToneMapping,
  Box3,
  Color,
  DirectionalLight,
  Euler,
  HemisphereLight,
  MathUtils,
  Mesh,
  MeshStandardMaterial,
  Object3D,
  PCFShadowMap,
  PerspectiveCamera,
  PMREMGenerator,
  PointLight,
  Quaternion,
  Scene,
  Timer,
  Vector2,
  Vector3,
  WebGLRenderer,
} from "three";
import { RoomEnvironment } from "three/examples/jsm/environments/RoomEnvironment.js";
import { GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";

// The sidebar's robot: the GLB of the Bleckmann AI portal (made in Blender,
// public/models/robot.glb), rendered with three.js and rigged in code to look
// at the pointer. This file is the portal's robotStage.ts with the model
// centred (MODEL_X 0) instead of pushed right of the hero copy.
//
// The contract with the model is a handful of node names. Each is an object
// (or armature bone) whose origin sits on the joint it turns around; every
// part is optional, a model with only a Head still turns its head:
//
//   Torso                 pivot at the waist; the slow, heavy follower
//   Head                  pivot where the neck meets the torso; the quick follower
//   Eye_L, Eye_R          emissive; pulse and blink
//   Arm_L, Arm_R          pivot at the shoulder; the arm on the pointer's side
//                         lifts toward it, both swing with the pointer's height
//   Forearm_L, Forearm_R  pivot at the elbow; bends when the arm reaches
//   Hand_L, Hand_R        pivot at the wrist; twists and tilts a little. The
//                         fingers ride along, they never move on their own
//
// The pointer is tracked over the whole page, not only the canvas: the robot
// turns and looks at the cursor wherever it is, and drifts back to an idle
// scan when it leaves the window. The card's glow (RobotHero) lies behind the
// canvas, and the scene's pointer light hangs behind the robot at the same
// spot, so the glow rims the robot instead of washing over it. Framing is
// automatic from the model's bounding box, so a different model needs no code
// change.

export interface RobotSceneCallbacks {
  onReady?: () => void;
  onError?: (message: string) => void;
}

// Per looking joint: how far it may turn (radians), how quickly it follows,
// and how much it rolls with a sideways pointer. Head fast and free, torso
// slow and narrow: the layering is what reads as mechanics.
interface JointSpec {
  name: string;
  yaw: number;
  pitch: number;
  roll: number;
  speed: number;
}

const JOINTS: JointSpec[] = [
  { name: "Head", yaw: MathUtils.degToRad(45), pitch: MathUtils.degToRad(24), roll: MathUtils.degToRad(4), speed: 7 },
  { name: "Torso", yaw: MathUtils.degToRad(24), pitch: MathUtils.degToRad(7), roll: MathUtils.degToRad(2), speed: 3 },
];

const EYES = ["Eye_L", "Eye_R"];

// The arms have their own rig. Degrees, per side (s = +1 for the robot's
// left, which is screen right): the arm on the pointer's side lifts outward
// (abduct) and reaches (swing forward, bend the elbow); both swing with the
// pointer's height; the wrists twist with the pointer's side and tilt with its
// height. A slow sway keeps them alive when nothing else happens.
const ARM_SIDES = [
  { side: "L", s: 1, phase: 0 },
  { side: "R", s: -1, phase: 2.1 },
];
const ARM = {
  abductNear: 32,
  abductFar: -5,
  swingHeight: 12,
  swingNear: 10,
  bendNear: 16,
  bendUp: 8,
  bendRest: 4,
  twist: 14,
  flex: 10,
  speed: 3,
  handSpeed: 4.5,
};

interface ArmPose {
  abduct: number;
  swing: number;
  bend: number;
  twist: number;
  flex: number;
}

interface ArmRig {
  s: number;
  phase: number;
  upper?: { node: Object3D; rest: Euler };
  fore?: { node: Object3D; rest: Euler };
  hand?: { node: Object3D; rest: Quaternion };
  cur: ArmPose;
}

// Where the model sits horizontally: a fraction of the half-width right of
// centre. Centred in the sidebar.
const MODEL_X = 0;
// Framing is by the body (everything but the arms): its top this far below the
// card's top edge, its bottom on the card's bottom edge. The canvas runs on
// below the card (the host's --scene-overhang), and the arms hang into that.
const HEADROOM = 0.08;
// The body takes this fraction of the box height; the rest is room below it for the arms, which hang lower than
// the body and were cut off at full size.
const BODY_FRACTION = 0.7;
const FOV = 28;
const ARMS = ["Arm_L", "Arm_R"];

interface Joint extends JointSpec {
  node: Object3D;
  rest: Euler;
  restY: number;
  cur: Vector2; // x = pitch, y = yaw, both as a fraction of the limits
}

// Colours come from the theme tokens, so the robot follows the house palette
// without a hex value in this file.
function token(name: string, fallback: string): Color {
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return new Color(value || fallback);
}

export function createRobotScene(host: HTMLElement, modelUrl: string, callbacks: RobotSceneCallbacks): () => void {
  const renderer = new WebGLRenderer({ alpha: true, antialias: true, powerPreference: "high-performance" });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.setClearColor(0x000000, 0);
  renderer.toneMapping = ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.25;
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = PCFShadowMap;
  host.appendChild(renderer.domElement);

  const scene = new Scene();
  const camera = new PerspectiveCamera(FOV, 1, 0.1, 100);

  // Image-based lighting from a procedural room: reflections and soft fill
  // on the metal, without shipping an HDR.
  const pmrem = new PMREMGenerator(renderer);
  scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
  scene.environmentIntensity = 1.1;

  const accent = token("--bm-red", "red");
  const fill = new HemisphereLight(0xffffff, 0x222222, 1.0);
  const key = new DirectionalLight(0xffffff, 3.6);
  key.castShadow = true;
  key.shadow.mapSize.set(1024, 1024);
  key.shadow.bias = -0.0005;
  const rim = new DirectionalLight(accent, 2.4);
  // The light the robot looks at: follows the pointer, behind the robot.
  const spot = new PointLight(0xffffff, 0, 0, 2);
  scene.add(fill, key, rim, spot);

  const timer = new Timer();
  const joints: Joint[] = [];
  const arms: ArmRig[] = [];
  const eyes: MeshStandardMaterial[] = [];
  const eyeMeshes: Mesh[] = [];
  let model: Object3D | null = null;
  const center = new Vector3(); // of the whole model; lights hang off it
  const size = new Vector3();
  const body = new Box3(); // the model without its arms; the camera frames this
  let unit = 1;
  // How far the canvas extends below the card, as a fraction of the card
  // height; set in CSS on the scene layer, inherited by the host.
  const overhang = parseFloat(getComputedStyle(host).getPropertyValue("--scene-overhang")) || 0;

  // Pointer, in viewport pixels; null until it moves, and again when it
  // leaves the window.
  let pointer: Vector2 | null = null;
  const aim = new Vector2(); // -1..1, x right, y up
  const aimTarget = new Vector2();
  let blinkAt = 3;
  let blinkUntil = 0;

  const v = new Vector3();
  const headAnchor = new Vector3();
  let width = 1;
  let height = 1;

  function fit() {
    const rect = host.getBoundingClientRect();
    width = Math.max(1, Math.round(rect.width));
    height = Math.max(1, Math.round(rect.height));
    renderer.setSize(width, height, false);
    camera.aspect = width / height;
    if (model) {
      const halfFov = MathUtils.degToRad(FOV / 2);
      // World height of the card: the body from HEADROOM to the bottom edge.
      const card = (body.max.y - body.min.y) / (1 - HEADROOM) / BODY_FRACTION;
      // The canvas is the card plus the overhang below it.
      const visible = card * (1 + overhang);
      const distance = visible / (2 * Math.tan(halfFov));
      const shift = MODEL_X * distance * Math.tan(halfFov) * camera.aspect;
      const cardTop = body.max.y + HEADROOM * card;
      const lookY = cardTop - visible / 2;
      camera.position.set(center.x - shift, lookY, center.z + distance);
      camera.lookAt(center.x - shift, lookY, center.z);
    }
    camera.updateProjectionMatrix();
  }

  function placeLights() {
    key.position.set(center.x - 1.4 * unit, center.y + 1.6 * unit, center.z + 1.4 * unit);
    key.target.position.copy(center);
    key.target.updateMatrixWorld();
    const cam = key.shadow.camera;
    cam.left = -unit;
    cam.right = unit;
    cam.top = unit;
    cam.bottom = -unit;
    cam.near = 0.1;
    cam.far = 6 * unit;
    cam.updateProjectionMatrix();
    rim.position.set(center.x + 1.6 * unit, center.y + 0.6 * unit, center.z - 1.4 * unit);
    rim.target.position.copy(center);
    rim.target.updateMatrixWorld();
    spot.distance = 6 * unit;
    spot.intensity = 12 * unit * unit;
  }

  const loader = new GLTFLoader();
  loader.load(
    modelUrl,
    (gltf) => {
      model = gltf.scene;
      model.traverse((node) => {
        if (node instanceof Mesh) {
          node.castShadow = true;
          node.receiveShadow = true;
        }
      });
      for (const spec of JOINTS) {
        const node = model.getObjectByName(spec.name);
        if (node) {
          joints.push({ ...spec, node, rest: node.rotation.clone(), restY: node.position.y, cur: new Vector2() });
        }
      }
      for (const { side, s, phase } of ARM_SIDES) {
        const upper = model.getObjectByName(`Arm_${side}`);
        const fore = model.getObjectByName(`Forearm_${side}`);
        const hand = model.getObjectByName(`Hand_${side}`);
        if (!upper && !fore && !hand) continue;
        arms.push({
          s,
          phase,
          upper: upper && { node: upper, rest: upper.rotation.clone() },
          fore: fore && { node: fore, rest: fore.rotation.clone() },
          hand: hand && { node: hand, rest: hand.quaternion.clone() },
          cur: { abduct: 0, swing: 0, bend: 0, twist: 0, flex: 0 },
        });
      }
      for (const name of EYES) {
        const node = model.getObjectByName(name);
        if (node instanceof Mesh && node.material instanceof MeshStandardMaterial) {
          node.material = node.material.clone();
          node.material.emissive = accent.clone();
          node.material.emissiveIntensity = 1.5;
          eyes.push(node.material);
          eyeMeshes.push(node);
        }
      }
      const box = new Box3().setFromObject(model);
      box.getCenter(center);
      box.getSize(size);
      unit = size.y;
      // The body box: every mesh that does not hang under an arm.
      model.updateWorldMatrix(true, true);
      const armRoots = ARMS.map((name) => model?.getObjectByName(name)).filter((n): n is Object3D => !!n);
      const partBox = new Box3();
      body.makeEmpty();
      model.traverse((node) => {
        if (!(node instanceof Mesh)) return;
        let ancestor: Object3D | null = node;
        while (ancestor && !armRoots.includes(ancestor)) ancestor = ancestor.parent;
        if (ancestor) return; // under an arm
        node.geometry.computeBoundingBox();
        if (node.geometry.boundingBox) {
          body.union(partBox.copy(node.geometry.boundingBox).applyMatrix4(node.matrixWorld));
        }
      });
      if (body.isEmpty()) body.copy(box);
      scene.add(model);
      placeLights();
      fit();
      renderer.render(scene, camera);
      callbacks.onReady?.();
    },
    undefined,
    (err) => callbacks.onError?.(err instanceof Error ? err.message : String(err)),
  );

  function tick() {
    timer.update();
    const dt = Math.min(timer.getDelta(), 0.1);
    const t = timer.getElapsed();
    if (!model) return;

    // Where to look: the pointer relative to the head on screen, softened so a
    // far cursor does not pin the joints to their limits; or an idle scan.
    const head = joints.find((j) => j.name === "Head")?.node ?? model;
    if (pointer) {
      const rect = renderer.domElement.getBoundingClientRect();
      head.getWorldPosition(headAnchor);
      v.copy(headAnchor).project(camera);
      const hx = rect.left + ((v.x + 1) / 2) * rect.width;
      const hy = rect.top + ((1 - v.y) / 2) * rect.height;
      aimTarget.set(
        Math.tanh(((pointer.x - hx) / (window.innerWidth / 2)) * 1.6),
        Math.tanh(((hy - pointer.y) / (window.innerHeight / 2)) * 1.6),
      );
    } else {
      aimTarget.set(Math.sin(t * 0.35) * 0.3, Math.sin(t * 0.52) * 0.12);
    }
    aim.lerp(aimTarget, 1 - Math.exp(-4 * dt));

    for (const j of joints) {
      const k = 1 - Math.exp(-j.speed * dt);
      j.cur.x += (aim.y - j.cur.x) * k;
      j.cur.y += (aim.x - j.cur.y) * k;
      // Blender's -Y front becomes +Z here: positive yaw turns the face toward
      // screen right, negative pitch tilts it up.
      j.node.rotation.set(j.rest.x - j.cur.x * j.pitch, j.rest.y + j.cur.y * j.yaw, j.rest.z + j.cur.y * j.roll);
    }

    // Arms. The joints' axes are the torso's (the empties carry no rotation):
    // a negative turn about X swings a hanging arm forward, a turn about Z
    // swings it sideways, outward being +X on the left arm and -X on the right.
    // The hand's own axes come from the model: Z along the forearm, X across
    // the palm.
    for (const arm of arms) {
      const near = Math.max(0, arm.s * aim.x);
      const far = Math.max(0, -arm.s * aim.x);
      const sway = Math.sin(t * 0.8 + arm.phase);
      const target: ArmPose = {
        abduct: near * ARM.abductNear + far * ARM.abductFar + sway * 2,
        swing: aim.y * ARM.swingHeight + near * ARM.swingNear + Math.sin(t * 0.6 + arm.phase) * 3,
        bend: ARM.bendRest + near * ARM.bendNear + Math.max(0, aim.y) * ARM.bendUp,
        twist: aim.x * ARM.twist + sway * 4,
        flex: aim.y * ARM.flex,
      };
      const k = 1 - Math.exp(-ARM.speed * dt);
      const kh = 1 - Math.exp(-ARM.handSpeed * dt);
      arm.cur.abduct += (target.abduct - arm.cur.abduct) * k;
      arm.cur.swing += (target.swing - arm.cur.swing) * k;
      arm.cur.bend += (target.bend - arm.cur.bend) * k;
      arm.cur.twist += (target.twist - arm.cur.twist) * kh;
      arm.cur.flex += (target.flex - arm.cur.flex) * kh;
      if (arm.upper) {
        const { node, rest } = arm.upper;
        node.rotation.set(
          rest.x - MathUtils.degToRad(arm.cur.swing),
          rest.y,
          rest.z + arm.s * MathUtils.degToRad(arm.cur.abduct),
        );
      }
      if (arm.fore) {
        const { node, rest } = arm.fore;
        node.rotation.set(rest.x - MathUtils.degToRad(arm.cur.bend), rest.y, rest.z);
      }
      if (arm.hand) {
        const { node, rest } = arm.hand;
        node.quaternion.copy(rest);
        node.rotateZ(MathUtils.degToRad(arm.cur.twist * arm.s));
        node.rotateX(MathUtils.degToRad(arm.cur.flex));
      }
    }

    // Breathing on the torso, when there is one.
    const torso = joints.find((j) => j.name === "Torso");
    if (torso) torso.node.position.y = torso.restY + Math.sin(t * 1.3) * 0.006 * unit;

    // The pointer light hangs behind the robot, on the line from the camera
    // through the cursor, so it sits where the card's glow is and lights the
    // robot's edges from behind.
    if (pointer) {
      const rect = renderer.domElement.getBoundingClientRect();
      const nx = MathUtils.clamp(((pointer.x - rect.left) / rect.width) * 2 - 1, -1.4, 1.4);
      const ny = MathUtils.clamp(-(((pointer.y - rect.top) / rect.height) * 2 - 1), -1.4, 1.4);
      v.set(nx, ny, 0.5).unproject(camera).sub(camera.position).normalize();
      const planeZ = center.z - 0.7 * unit;
      const s = (planeZ - camera.position.z) / v.z;
      spot.position.copy(camera.position).addScaledVector(v, s);
      spot.intensity += (20 * unit * unit - spot.intensity) * (1 - Math.exp(-6 * dt));
    } else {
      spot.intensity += (0 - spot.intensity) * (1 - Math.exp(-3 * dt));
    }

    // Eyes: a slow pulse and the occasional blink. Kept below the point where
    // the tone mapping bleaches the red to salmon.
    if (eyes.length) {
      const pulse = 0.85 + 0.3 * Math.sin(t * 2.1);
      for (const m of eyes) m.emissiveIntensity = pulse;
      if (t > blinkAt) {
        blinkUntil = t + 0.12;
        blinkAt = t + 2.5 + Math.random() * 4;
      }
      const closed = t < blinkUntil;
      for (const mesh of eyeMeshes) mesh.scale.y = closed ? 0.08 : 1;
    }

    renderer.render(scene, camera);
  }

  // Run only while the canvas can be seen.
  let visible = true;
  let inView = true;
  function updateLoop() {
    renderer.setAnimationLoop(visible && inView ? tick : null);
  }
  const onVisibility = () => {
    visible = document.visibilityState === "visible";
    updateLoop();
  };
  const io = new IntersectionObserver(
    (entries) => {
      inView = entries.some((e) => e.isIntersecting);
      updateLoop();
    },
    { threshold: 0 },
  );
  io.observe(host);
  const ro = new ResizeObserver(fit);
  ro.observe(host);

  const onMove = (e: PointerEvent) => {
    if (e.pointerType && e.pointerType !== "mouse") return;
    pointer = (pointer ?? new Vector2()).set(e.clientX, e.clientY);
  };
  const onLeave = () => {
    pointer = null;
  };
  window.addEventListener("pointermove", onMove, { passive: true });
  document.documentElement.addEventListener("pointerleave", onLeave);
  window.addEventListener("blur", onLeave);
  document.addEventListener("visibilitychange", onVisibility);

  fit();
  updateLoop();

  return () => {
    renderer.setAnimationLoop(null);
    io.disconnect();
    ro.disconnect();
    window.removeEventListener("pointermove", onMove);
    document.documentElement.removeEventListener("pointerleave", onLeave);
    window.removeEventListener("blur", onLeave);
    document.removeEventListener("visibilitychange", onVisibility);
    scene.traverse((node) => {
      if (node instanceof Mesh) {
        node.geometry.dispose();
        const materials = Array.isArray(node.material) ? node.material : [node.material];
        for (const m of materials) m.dispose();
      }
    });
    scene.environment?.dispose();
    pmrem.dispose();
    renderer.dispose();
    renderer.domElement.remove();
  };
}
