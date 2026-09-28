import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const assetRoot = path.join(root, 'assets');
for (const category of ['rows', 'status', 'toggle', 'icons']) fs.mkdirSync(path.join(assetRoot, category), { recursive: true });
const manifest = {
  version: '3.4',
  design: 'Frontier field operations / warm umber paper, pale linen outlines, engineering yellow',
  pixelAlpha: 'straight',
  sourceSizes: { row: [512,112], status: [64,64], toggle: [176,52], icon: [64,64] },
  states: ['default','hover','pressed','disabled'],
  sliceOrder: ['left','top','right','bottom'],
  assets: {},
  roles: {}
};
const svgRoot = (w,h,body) => `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">${body}</svg>`;
function emit(id, category, w, h, body, props={}) {
  const relative = `assets/${category}/${id}`;
  fs.writeFileSync(path.join(root, `${relative}.svg`), svgRoot(w,h,body));
  manifest.assets[id] = {
    path: `${relative}.png`, path2x: `${relative}@2x.png`, svg: `${relative}.svg`,
    viewBox: [w,h], textBakedIn: false, iconBakedIn: false,
    ...props
  };
}

const palettes = {
  default: { fill:'#453E35', bottom:'#3B352D', edge:'#9A8D78', glint:'#E5D6B8', accent:'#BDA45D' },
  hover: { fill:'#50463A', bottom:'#443B30', edge:'#C7B172', glint:'#F0E1C3', accent:'#E4C86F' },
  pressed: { fill:'#39332B', bottom:'#302A23', edge:'#C4A95B', glint:'#D2BF92', accent:'#C4A95B' },
  disabled: { fill:'#37332D', bottom:'#302C26', edge:'#716759', glint:'#B0A28A', accent:'#746851' }
};
manifest.palette = {
  states: palettes,
  text: { light:'#E8DDC3', dark:'#332E27', muted:'#A89B83' },
  glyph: { useLight:'#DFBE68', usedLight:'#CABB9D', unusedLight:'#E8DDC3' },
  divider:'#BAA88B',
  paper: { fiber:'#E7D6B5', grain:'#1F1B16', opacityRange:[0.025,0.08], deterministic:true },
  backgroundInteriorOpaque:true,
  frameStyle:'single fine rounded outline, no secondary complete inset frame'
};
// Fixed coordinates avoid flicker between states, and sparse short fibers avoid
// a visible repeating weave when the middle of a 9-slice texture stretches.
function paper(w,h,state) {
  let seed=9173;
  const rnd=()=>((seed=(Math.imul(seed,1664525)+1013904223)>>>0)/4294967296);
  const count=Math.round(w*h/235);
  let marks='';
  for(let i=0;i<count;i++) {
    const x=5+rnd()*(w-10), y=5+rnd()*(h-10);
    const angle=rnd()*Math.PI*2, len=.35+rnd()*1.7;
    const warm=i%3!==0, opacity=(.025+rnd()*.055)*(state==='disabled'?.7:1);
    marks+=`<path d="M${x.toFixed(2)} ${y.toFixed(2)}l${(Math.cos(angle)*len).toFixed(2)} ${(Math.sin(angle)*len).toFixed(2)}" stroke="${warm?'#E7D6B5':'#1F1B16'}" stroke-opacity="${opacity.toFixed(3)}" stroke-width="${(.3+rnd()*.3).toFixed(2)}" stroke-linecap="round"/>`;
  }
  return `<defs><clipPath id="paper-clip"><rect x="2" y="2" width="${w-4}" height="${h-4}" rx="${h===52?6:8}"/></clipPath></defs><g clip-path="url(#paper-clip)">${marks}</g>`;
}
for (const [state,p] of Object.entries(palettes)) {
  // The background and frame have identical geometry and can be sliced separately.
  emit(`row-bg-${state}`, 'rows', 512,112,
    `<rect x="2" y="2" width="508" height="108" rx="8" fill="${p.fill}"/>
${paper(512,112,state)}
<path d="M10 107H502" stroke="${p.bottom}" stroke-opacity=".48" stroke-width="1" stroke-linecap="round"/>`,
    { role:'row-background', family:'effect-row', state, slices:[12,12,12,12], minSize:[96,48], cornerRadius:8, contentInset:[12,12,12,12] });
  emit(`row-frame-${state}`, 'rows', 512,112,
    `<rect x="2" y="2" width="508" height="108" rx="8" fill="none" stroke="${p.edge}" stroke-width="1.15"/>
<path d="M12 3H500" fill="none" stroke="${p.glint}" stroke-opacity="${state==='disabled'?'.08':'.16'}" stroke-width=".6"/>`,
    { role:'row-frame', family:'effect-row', state, slices:[12,12,12,12], minSize:[96,48], cornerRadius:8, centerTransparent:true });
}
emit('row-divider', 'rows', 2,80,
  '<path d="M1 0V80" stroke="#BAA88B" stroke-opacity=".28" stroke-width="1"/>',
  { role:'optional-slot-divider', family:'effect-row', slices:[0,0,0,0], stretch:'vertical', recommendedDisplayWidth:1 });

for (const [state,p] of Object.entries(palettes)) {
  emit(`status-bg-${state}`, 'status', 64,64,
    `<rect x="2" y="2" width="60" height="60" rx="8" fill="${p.bottom}"/>
${paper(64,64,state)}
<rect x="2" y="2" width="60" height="60" rx="8" fill="none" stroke="${p.accent}" stroke-width="1.15"/>`,
    { role:'status-background', family:'status', state, slices:[12,12,12,12], minSize:[32,32], cornerRadius:8, contentInset:[8,8,8,8] });
}

for (const state of ['default','hover','pressed','expanded']) {
  const p=palettes[state==='expanded'?'hover':state];
  const expanded=state==='expanded';
  emit(`toggle-bg-${state}`, 'toggle', 176,52,
    `<rect x="2" y="2" width="172" height="48" rx="6" fill="${p.fill}"/>
${paper(176,52,state)}
<rect x="2" y="2" width="172" height="48" rx="6" fill="none" stroke="${expanded?'#D2B76C':p.edge}" stroke-width="1.15"/>
${expanded?'<path d="M12 46H164" stroke="#D2B76C" stroke-opacity=".8" stroke-width="1.2"/>':''}`,
    { role:'detail-toggle-background', family:'detail-toggle', state, slices:[12,12,12,12], minSize:[44,26], recommendedDisplaySize:[88,26], cornerRadius:6, contentInset:[14,8,14,8] });
}

const glyphs = {
  use: '<path d="M12 32H49M37 20L49 32L37 44"/>',
  unused: '<circle cx="32" cy="32" r="19"/><path d="M32 8V13M32 51V56" stroke-width="3.5"/>',
  used: '<circle cx="32" cy="32" r="22"/><path d="M20 32L28 40L44 23"/>',
  'chevron-down': '<path d="M16 25L32 41L48 25"/>',
  'chevron-up': '<path d="M16 39L32 23L48 39"/>'
};
const inks={light:'#E8DDC3', dark:'#332E27', muted:'#A89B83'};
for(const [glyph,body] of Object.entries(glyphs)) for(const [ink,color] of Object.entries(inks)) {
  const role=glyph.startsWith('chevron')?'detail-toggle-icon':'status-icon';
  const glyphColor=ink==='light'&&glyph==='use'?'#DFBE68':ink==='light'&&glyph==='used'?'#CABB9D':color;
  emit(`icon-${glyph}-${ink}`, 'icons',64,64,
    `<g fill="none" stroke="${glyphColor}" stroke-width="4.5" stroke-linecap="round" stroke-linejoin="round">${body}</g>`,
    { role, name:glyph, ink, color:glyphColor, slices:[0,0,0,0], safeArea:[6,6,52,52], recommendedDisplaySize:[28,32], iconBakedIn:true });
}

manifest.roles = {
  row: { backgroundFamily:'row-bg', frameFamily:'row-frame', text:'runtime', optionalLeftMarkerCount:[0,1,2], optionalRightStatus:['none','use','unused','used'], rowStates:['default','hover','pressed','disabled'] },
  executionChoice: { leftMarkerCount:0, rightStatus:'use', statusIsInteractive:true },
  playerEffectDetail: { leftMarkerCount:[0,1,2], rightStatus:['none','unused','used'], statusIsInteractive:false },
  quickAction: { leftMarkerCount:[0,1,2], rightStatus:['none','use','unused','used'], statusIsInteractive:'game-owned' },
  status: { baseFamily:'status-bg', iconNames:['use','unused','used'], states:['default','hover','pressed','disabled'], disabledInk:'muted', semantics:'Availability, usage and activation are independent runtime fields' },
  detailToggle: { baseFamily:'toggle-bg', collapsedGlyph:'chevron-down', expandedGlyph:'chevron-up', expandedState:'expanded', text:'runtime' },
  playerMarkers: { source:'Reuse existing originals/markers composite-player squares', colorMap:{ Green:'#5FD200', Yellow:'#FFBF00', Blue:'#006CFF', Red:'#EC0000' }, numberOverlay:'runtime-optional', noPlaceholderWhenAbsent:true }
};
fs.writeFileSync(path.join(assetRoot,'effect-assets.json'),JSON.stringify(manifest,null,2)+'\n');
if(!process.argv.includes('--svg-only')) {
  const jobs=Object.values(manifest.assets).flatMap(a=>[
    { svg:a.svg,png:a.path,w:a.viewBox[0],h:a.viewBox[1] },
    { svg:a.svg,png:a.path2x,w:a.viewBox[0]*2,h:a.viewBox[1]*2 }
  ]);
  for(const j of jobs) {
    const result=spawnSync('inkscape',[path.join(root,j.svg),'--export-type=png',`--export-filename=${path.join(root,j.png)}`,`--export-width=${j.w}`,`--export-height=${j.h}`],{encoding:'utf8',timeout:60000});
    if(result.status!==0) throw new Error(`Could not export ${j.png}: ${result.stderr}`);
  }
  console.log(`Exported ${jobs.length} PNG assets and ${Object.keys(manifest.assets).length} SVG sources.`);
}
