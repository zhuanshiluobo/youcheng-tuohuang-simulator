import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const assetRoot = path.join(root, 'assets');
for (const folder of ['bases', 'icons']) fs.mkdirSync(path.join(assetRoot, folder), { recursive: true });
const manifest = {
  version: '3.2',
  design: 'Frontier field operations / graphite, ivory, engineering yellow',
  pixelAlpha: 'straight',
  sourceSizes: { button: [256, 80], icon: [64, 64] },
  assets: {},
  roles: {}
};
const svgRoot = (w, h, body) => `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">${body}</svg>`;
function emit(id, category, w, h, body, props = {}) {
  const relative = `assets/${category}/${id}`;
  fs.writeFileSync(path.join(root, `${relative}.svg`), svgRoot(w, h, body));
  manifest.assets[id] = {
    path: `${relative}.png`, path2x: `${relative}@2x.png`, svg: `${relative}.svg`,
    viewBox: [w, h], ...props
  };
}

const colors = {
  action: {
    default: ['#343A3B','#777D79','#525957','#252B2D','#7C857D'],
    hover: ['#424A49','#CBCBBC','#747D73','#343C3B','#D7BE65'],
    pressed: ['#252D2D','#BEA55E','#59625A','#1E2525','#D5B751'],
    disabled: ['#2B3031','#4D5452','#3C4240','#252A2B','#515854']
  },
  tab: {
    default: ['#323839','#747B77','#4C5551','#262D2D','#6F7B71'],
    hover: ['#424A47','#C3C6B7','#727C70','#343C38','#D4BB64'],
    pressed: ['#282F2D','#B2BBA8','#56634F','#1E2524','#D8B851'],
    disabled: ['#2B3030','#4D5350','#3B433D','#252B27','#4D574E'],
    selected: ['#D9D9CB','#F2F0DE','#B4B9AA','#B8BCAA','#D7B94D'],
    'selected-hover': ['#E5E4D5','#FFFEEC','#C5CABA','#C8CDB8','#E4C858']
  },
  primary: {
    default: ['#DDC063','#F2D889','#C1A74D','#B79D49','#282E2C'],
    hover: ['#EFD275','#FFF0B3','#D7BC62','#C3AA55','#282E2C'],
    pressed: ['#C4A546','#EDD28B','#A38734','#A08431','#262C29'],
    disabled: ['#565950','#72756A','#62665A','#464A42','#828575']
  }
};

function buttonBody(family, state) {
  const paletteFamily = family === 'undo' ? 'action' : family;
  const palette = colors[paletteFamily][state];
  const [fill, edge, inner, bottom, accent] = palette;
  const selected = state.startsWith('selected');
  const disabled = state === 'disabled';
  // Every silhouette is a complete rectangle with four intact rounded corners.
  if (family === 'tab') {
    return `<rect x="2" y="2" width="252" height="76" rx="4" fill="${fill}" stroke="${edge}" stroke-width="1.4"/>
<path d="M8 8H248" fill="none" stroke="${edge}" stroke-opacity=".24" stroke-width="1"/>
${selected ? `<path d="M8 70H248" stroke="#93833E" stroke-width="5"/><path d="M8 69H248" stroke="${accent}" stroke-width="4"/>` : `<path d="M10 70H246" stroke="${accent}" stroke-opacity="${disabled ? '.2' : '.45'}" stroke-width="1"/>`}`;
  }
  if (family === 'undo') {
    const outline = disabled ? '#4E5652' : state === 'hover' ? '#D7D8C6' : state === 'pressed' ? '#BAAD76' : '#9CA697';
    return `<rect x="2.5" y="2.5" width="251" height="75" rx="8" fill="${fill}" fill-opacity="${disabled ? '.6' : '.82'}" stroke="${outline}" stroke-width="1.6"/>
<path d="M13 71H243" stroke="${outline}" stroke-opacity=".18" stroke-width="1"/>`;
  }
  if (family === 'primary') {
    return `<rect x="1" y="2" width="254" height="77" rx="8" fill="#151A1B" fill-opacity=".55"/>
<rect x="2.5" y="2.5" width="251" height="73" rx="8" fill="${fill}" stroke="${edge}" stroke-width="1.5"/>
<rect x="7" y="7" width="242" height="64" rx="4.5" fill="none" stroke="${accent}" stroke-opacity="${disabled ? '.22' : '.3'}" stroke-width="1"/>
<path d="M12 72H244" stroke="${bottom}" stroke-width="2"/>`;
  }
  return `<rect x="1" y="1" width="254" height="78" rx="6" fill="#151A1B" fill-opacity=".7"/>
<rect x="2.5" y="2.5" width="251" height="74" rx="6" fill="${fill}" stroke="${edge}" stroke-width="1.5"/>
<rect x="6.5" y="6.5" width="243" height="66" rx="3" fill="none" stroke="${inner}" stroke-width="1"/>
<path d="M9 71H247" stroke="${bottom}" stroke-width="2"/>
<path d="M10 8.5H246" stroke="${edge}" stroke-opacity="${disabled ? '.2' : '.42'}" stroke-width="1"/>
<path d="M11 17V24M11 54V61M245 17V24M245 54V61" stroke="${accent}" stroke-opacity="${disabled ? '.45' : '.85'}" stroke-width="1.5"/>`;
}

for (const family of ['action', 'tab', 'primary', 'undo']) {
  const paletteFamily = family === 'undo' ? 'action' : family;
  for (const state of Object.keys(colors[paletteFamily])) {
    const id = `${family}-${state}`;
    emit(id, 'bases', 256, 80, buttonBody(family, state), {
      role: 'button-background', family, state,
      slices: [12, 12, 12, 12], sliceOrder: ['left','top','right','bottom'],
      contentInset: [18, 12, 18, 12], minSize: [72, 40],
      textBakedIn: false, iconBakedIn: false, cornerRadius: family === 'tab' ? 4 : (family === 'action' ? 6 : 8),
      recommendedInk: state === 'disabled' ? 'muted' : (family === 'primary' || state.startsWith('selected') ? 'dark' : 'light')
    });
  }
}

// Each mark has one purpose, a coherent optical stroke, and open space at 24–32 px.
const glyphs = {
  'main-actions': `<g stroke-width="3.8"><rect x="9" y="14" width="12" height="14" rx="1.5"/><rect x="26" y="14" width="12" height="14" rx="1.5"/><rect x="43" y="14" width="12" height="14" rx="1.5"/><rect x="9" y="36" width="12" height="14" rx="1.5"/><rect x="26" y="36" width="12" height="14" rx="1.5"/><rect x="43" y="36" width="12" height="14" rx="1.5"/></g>`,
  'quick-actions': `<path d="M36 7L14 35H29L25 57L51 27H36Z"/>`,
  'city-patterns': `<path d="M8 53H56M11 53V30H23V53M25 53V12H39V53M42 53V23H54V53"/><path d="M30 23H34M30 32H34M30 41H34M46 32H50M46 41H50M15 40H19" stroke-width="3.3"/>`,
  deploy: `<circle cx="32" cy="15" r="7"/><path d="M19 35C19 28 24 25 32 25S45 28 45 35M32 34V49M26 43L32 49L38 43M10 45V55H54V45"/>`,
  dispatch: `<path d="M10 21H53L45 13M53 21L45 29M54 43H11L19 35M11 43L19 51"/>`,
  explore: `<circle cx="32" cy="32" r="23"/><path d="M42 20L35 35L21 44L28 28Z"/><path d="M28 28L35 35M32 9V13M55 32H51M32 55V51M9 32H13" stroke-width="3.3"/>`,
  'city-move': `<path d="M9 42H43M12 42V25H23V42M25 42V12H38V42M29 20H34M29 29H34M19 51H54L47 44M54 51L47 58"/><path d="M6 16H15M6 22H10" stroke-width="3.3"/>`,
  build: `<path d="M33 29L12 50" stroke-width="7"/><path d="M30 9L54 33L45 42L21 18Z"/><path d="M10 56H33" stroke-width="3.3"/>`,
  special: `<path d="M12 55V9M13 12H49L42 22L49 32H13"/><path d="M35 42V56M28 49H42M30 44L40 54M40 44L30 54" stroke-width="3.5"/>`,
  undo: `<path d="M11 25H35C48 25 54 32 54 42S46 56 35 56H26M11 25L22 14M11 25L22 36"/>`,
  end: `<path d="M9 33L21 45L43 19M55 12V52" stroke-width="5"/>`
};
const inks = { light: '#ECEBDA', dark: '#263030', muted: '#747E77' };
for (const [name, markup] of Object.entries(glyphs)) {
  for (const [ink, color] of Object.entries(inks)) {
    emit(`${name}-${ink}`, 'icons', 64, 64,
      `<g fill="none" stroke="${color}" stroke-width="4.2" stroke-linecap="round" stroke-linejoin="round">${markup}</g>`,
      { role: 'button-icon', name, ink, color, slices: [0,0,0,0], safeArea: [4,4,56,56], recommendedDisplaySize: [28,32], textBakedIn: false });
    // Public ID is explicit while the on-disk name stays short for sprite imports.
    manifest.assets[`icon-${name}-${ink}`] = manifest.assets[`${name}-${ink}`];
    delete manifest.assets[`${name}-${ink}`];
  }
}

const actionRoles = ['deploy','dispatch','explore','city-move','build','special'];
for (const name of actionRoles) manifest.roles[name] = { baseFamily: 'action', icon: name, text: 'runtime', states: ['default','hover','pressed','disabled'] };
for (const name of ['main-actions','quick-actions','city-patterns']) manifest.roles[name] = { baseFamily: 'tab', icon: name, text: 'runtime', states: ['default','hover','pressed','disabled','selected','selected-hover'] };
manifest.roles.undo = { baseFamily: 'undo', icon: 'undo', text: 'runtime', states: ['default','hover','pressed','disabled'] };
manifest.roles.end = { baseFamily: 'primary', icon: 'end', text: 'runtime', states: ['default','hover','pressed','disabled'] };

fs.writeFileSync(path.join(assetRoot, 'button-assets.json'), JSON.stringify(manifest, null, 2) + '\n');

if (!process.argv.includes('--svg-only')) {
  const jobs = Object.values(manifest.assets).flatMap(a => [
    { svg: a.svg, png: a.path, w: a.viewBox[0], h: a.viewBox[1] },
    { svg: a.svg, png: a.path2x, w: a.viewBox[0]*2, h: a.viewBox[1]*2 }
  ]);
  for (const job of jobs) {
    const result = spawnSync('inkscape', [path.join(root, job.svg), '--export-type=png', `--export-filename=${path.join(root, job.png)}`, `--export-width=${job.w}`, `--export-height=${job.h}`], { encoding: 'utf8', timeout: 60000 });
    if (result.status !== 0) throw new Error(`Could not export ${job.png}: ${result.stderr}`);
  }
  console.log(`Exported ${jobs.length} PNG assets and ${Object.keys(manifest.assets).length} SVG sources.`);
}
