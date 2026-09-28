import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';

// Native SVG frame sources. Raster outputs are rendered from the same geometry.
// No bitmap textures, text, filters, or external references are used.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const assets = path.join(root, 'assets');
fs.mkdirSync(assets, { recursive: true });
const definitions = [
  { id: 'outer-frame', w: 1920, h: 1080, slice: 24, main: '#BEB39D', inner: '#70675A', mainOpacity: 0.88, innerOpacity: 0.43, radius: 5, corner: 16, inset: 6, role: 'screen perimeter; transparent center' },
  { id: 'bottom-frame', w: 1920, h: 96, slice: 12, main: '#B9AE98', inner: '#716656', mainOpacity: 0.85, innerOpacity: 0.38, radius: 4, corner: 10, inset: 6, role: 'bottom bar perimeter; transparent center' },
  { id: 'chain-slot-frame', w: 1024, h: 64, slice: 12, main: '#A79A80', inner: '#6A6050', mainOpacity: 0.68, innerOpacity: 0.31, radius: 3, corner: 8, inset: 6, role: 'resolution slot perimeter; transparent center' },
];

function perimeter(w, h, inset, chamfer) {
  const a = inset, b = w - inset, c = h - inset, r = chamfer;
  return `M${a+r} ${a}H${b-r}L${b} ${a+r}V${c-r}L${b-r} ${c}H${a+r}L${a} ${c-r}V${a+r}Z`;
}

function geometry(d) {
  const {w,h,radius,corner} = d;
  const a = 2, b = w-2, c = h-2, t = corner;
  const cornerPaths = [
    `M${a} ${t}V${a+radius}L${a+radius} ${a}H${t}`,
    `M${w-t} ${a}H${b-radius}L${b} ${a+radius}V${t}`,
    `M${a} ${h-t}V${c-radius}L${a+radius} ${c}H${t}`,
    `M${w-t} ${c}H${b-radius}L${b} ${c-radius}V${h-t}`,
  ].join(' ');
  return `<g fill="none" stroke-linejoin="round" stroke-linecap="butt">`+
    `<path d="${perimeter(w,h,2,radius)}" stroke="${d.main}" stroke-opacity="${d.mainOpacity}" stroke-width="1.25"/>`+
    `<path d="${perimeter(w,h,4.5,Math.max(1,radius-1.5))}" stroke="${d.inner}" stroke-opacity="${d.innerOpacity}" stroke-width="0.65"/>`+
    `<path d="${cornerPaths}" stroke="#D3C7AD" stroke-opacity="${d.id === 'chain-slot-frame' ? 0.32 : 0.55}" stroke-width="1.25"/>`+
    `</g>`;
}

function svg(d, box=[0,0,d.w,d.h]) {
  const [x,y,w,h] = box;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="${x} ${y} ${w} ${h}"><title>${d.id}</title>${geometry(d)}</svg>\n`;
}

const exports = [];
function addImage(d, relative, box) {
  const [x,y,w,h] = box;
  const stem=path.join(assets,relative);
  fs.mkdirSync(path.dirname(stem),{recursive:true});
  fs.writeFileSync(stem+'.svg',svg(d,box));
  for (const factor of [1,2]) {
    exports.push({ source: stem+'.svg', target: stem+(factor===2?'@2x':'')+'.png', w:w*factor, h:h*factor });
  }
  return {svg:`assets/${relative}.svg`,path:`assets/${relative}.png`,png2x:`assets/${relative}@2x.png`,sourceRect:box,logicalSize:[w,h],pixelDimensions:{'1x':[w,h],'2x':[w*2,h*2]}};
}

const catalog={
  version:'3.5',
  coordinateUnit:'logical-px',
  sliceOrder:['left','top','right','bottom'],
  cornerStrategy:'keep targetSlices equal to logical source slices; stretch straight edges only',
  rasterScaling:'1x and 2x files render at the same logical size; all source-pixel borders double for 2x',
  assets:{},
};
for(const d of definitions){
  const s=d.slice;
  const entry=addImage(d,d.id,[0,0,d.w,d.h]);
  Object.assign(entry,{
    viewBox:[d.w,d.h],svgViewBox:[0,0,d.w,d.h],
    slices:[s,s,s,s],targetSlices:[s,s,s,s],targetInsets:[s,s,s,s],
    borders1x:[s,s,s,s],borders2x:[s*2,s*2,s*2,s*2],
    contentInset:[d.inset,d.inset,d.inset,d.inset],
    transparentCenter:true,drawCenter:false,externalResources:false,
    lineWidthLogical:1.25,innerLineWidthLogical:0.65,
    visibleEdgeWidthLogical:4.825,role:d.role,
    parts:{},
  });
  const rects={
    'top-left':[0,0,s,s], 'top':[s,0,d.w-s*2,s], 'top-right':[d.w-s,0,s,s],
    'left':[0,s,s,d.h-s*2], 'right':[d.w-s,s,s,d.h-s*2],
    'bottom-left':[0,d.h-s,s,s], 'bottom':[s,d.h-s,d.w-s*2,s], 'bottom-right':[d.w-s,d.h-s,s,s],
  };
  for(const [name,box] of Object.entries(rects)){
    entry.parts[name]=addImage(d,`parts/${d.id}/${name}`,box);
    entry.parts[name].stretch = ['top','bottom'].includes(name)?'x':['left','right'].includes(name)?'y':'none';
  }
  catalog.assets[d.id]=entry;
}

// Border-free replacement for the old bottom fill, whose bitmap contained
// pale corner fragments. This layer is an opaque rectangular material only.
let seed=35192;
function random(){seed=(1664525*seed+1013904223)>>>0;return seed/4294967296;}
let fibers='';
for(let i=0;i<720;i++){
  const x=(random()*1920).toFixed(2),y=(random()*96).toFixed(2);
  const len=(0.7+random()*3.2).toFixed(2),alpha=(0.018+random()*0.025).toFixed(3);
  fibers+=`<path d="M${x} ${y}h${len}" stroke="${i%2?'#C9BA99':'#161511'}" stroke-opacity="${alpha}" stroke-width="0.45"/>`;
}
const fillStem=path.join(assets,'bottom-bar-clean');
fs.writeFileSync(fillStem+'.svg',`<svg xmlns="http://www.w3.org/2000/svg" width="1920" height="96" viewBox="0 0 1920 96"><title>border-free bottom bar material</title><defs><linearGradient id="paper" x1="0" y1="0" x2="0" y2="1"><stop stop-color="#3A3731"/><stop offset="0.48" stop-color="#36332E"/><stop offset="1" stop-color="#312F2B"/></linearGradient></defs><rect width="1920" height="96" fill="url(#paper)"/><g fill="none">${fibers}</g></svg>\n`);
for(const factor of [1,2]) exports.push({source:fillStem+'.svg',target:fillStem+(factor===2?'@2x':'')+'.png',w:1920*factor,h:96*factor});
catalog.assets['bottom-bar']={
  path:'assets/bottom-bar-clean.png',svg:'assets/bottom-bar-clean.svg',png2x:'assets/bottom-bar-clean@2x.png',
  viewBox:[1920,96],svgViewBox:[0,0,1920,96],logicalSize:[1920,96],pixelDimensions:{'1x':[1920,96],'2x':[3840,192]},
  slices:[0,0,0,0],targetSlices:[0,0,0,0],targetInsets:[0,0,0,0],borders1x:[0,0,0,0],borders2x:[0,0,0,0],
  contentInset:[0,0,0,0],opaque:true,transparentCenter:false,drawCenter:true,externalResources:false,
  role:'bottom bar background only; opaque rectangle with no border or corner decoration',stretch:'xy',
};

const queue=[...exports];
async function worker(){
  while(queue.length){
    const item=queue.shift();
    await new Promise((resolve,reject)=>{
      const p=spawn('inkscape',[item.source,'--export-type=png',`--export-filename=${item.target}`,`--export-width=${item.w}`,`--export-height=${item.h}`,'--export-background-opacity=0'],{stdio:['ignore','ignore','pipe']});
      let err='';p.stderr.on('data',x=>err+=x);p.on('error',reject);p.on('exit',code=>code===0?resolve():reject(new Error(`${item.target}: ${err}`)));
    });
  }
}
await Promise.all(Array.from({length:4},()=>worker()));
for(const entry of Object.values(catalog.assets)){
  const f=path.join(root,entry.path);
  entry.bytes=fs.statSync(f).size;
  entry.sha256=crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');
}
fs.writeFileSync(path.join(assets,'frame-assets.json'),JSON.stringify(catalog,null,2)+'\n');
console.log(`Built ${definitions.length} native SVG frames, ${definitions.length*8} edge/corner parts, one border-free fill, and ${exports.length} PNGs.`);
