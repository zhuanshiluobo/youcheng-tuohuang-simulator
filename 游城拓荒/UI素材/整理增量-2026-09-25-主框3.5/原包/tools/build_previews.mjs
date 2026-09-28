import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawnSync} from 'node:child_process';
import {nineSliceParts} from '../integration/nine_slice.mjs';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const manifest=JSON.parse(fs.readFileSync(path.join(root,'assets/frame-assets.json'),'utf8'));
const imageUri=p=>'data:image/png;base64,'+fs.readFileSync(p).toString('base64');
const assets=Object.fromEntries(Object.entries(manifest.assets).map(([id,a])=>[id,{...a,uri:imageUri(path.join(root,a.png2x))}]));
const esc=s=>String(s).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;');
function renderer(){let n=0;const defs=[];
 const rect=(x,y,w,h,fill,rx=0)=>`<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="${rx}" fill="${fill}"/>`;
 const text=(x,y,s,size=20,fill='#e6ddca')=>`<text x="${x}" y="${y}" font-size="${size}" fill="${fill}">${esc(s)}</text>`;
 function crop(uri,iw,ih,source,target){const[sx,sy,sw,sh]=source,[x,y,w,h]=target,kx=w/sw,ky=h/sh,id='p'+(++n);defs.push(`<clipPath id="${id}">${rect(x,y,w,h,'white')}</clipPath>`);return `<g clip-path="url(#${id})"><image x="${x-sx*kx}" y="${y-sy*ky}" width="${iw*kx}" height="${ih*ky}" preserveAspectRatio="none" xlink:href="${uri}"/></g>`;}
 function frame(id,target){const a=assets[id],size=a.pixelDimensions['2x'];return nineSliceParts({sourceSize:size,sourceBorders:a.borders2x,targetRect:target,targetBorders:a.targetSlices,drawCenter:false}).map(p=>crop(a.uri,...size,p.source,p.destination)).join('');}
 const image=(id,target)=>{const a=assets[id];return crop(a.uri,...a.pixelDimensions['2x'],[0,0,...a.pixelDimensions['2x']],target);};
 const svg=(w,h,body)=>`<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}"><defs>${defs.join('')}</defs><g font-family="Noto Sans CJK SC,Microsoft YaHei,sans-serif">${body}</g></svg>`;
 return {rect,text,crop,frame,image,svg};
}
const out=path.join(root,'preview');fs.mkdirSync(out,{recursive:true});
function save(name,svg){const file=path.join(out,name+'.svg');fs.writeFileSync(file,svg);const p=spawnSync('inkscape',[file,'--export-type=png','--export-filename='+path.join(out,name+'.png')],{encoding:'utf8',env:process.env});if(p.status!==0)throw new Error(p.stderr);}
{
 const r=renderer();let b=r.rect(0,0,1600,1040,'#302c26')+r.text(48,66,'Main Frame · 3.5',34)+r.text(48,108,'原生矢量细边框 / 2× 高清 PNG / 边框与底图分离',19,'#bfb29a');
 b+=r.rect(48,158,990,540,'#39352e')+r.frame('outer-frame',[48,158,990,540]);
 b+=r.text(92,214,'主界面外框',26)+r.text(92,252,'中心完全透明，底色仅用于展示',18,'#bfb29a')+r.text(92,651,'角区保持尺寸，直边延展；主线约 1.25 逻辑像素',18,'#bfb29a');
 b+=r.text(1080,194,'左下角 · 6× 细节',23);
 const a=assets['outer-frame'],size=a.pixelDimensions['2x'];
 b+=r.rect(1080,220,288,288,'#39352e')+r.crop(a.uri,...size,[0,size[1]-96,96,96],[1080,220,288,288]);
 b+=r.text(1080,552,'1×：1920 × 1080',20)+r.text(1080,590,'2×：3840 × 2160',20)+r.text(1080,636,'无实心角块，无黄黑中段',18,'#bfb29a');
 b+=r.text(48,755,'底栏与结算槽 · 独立细框',24)+r.image('bottom-bar',[48,789,1504,108])+r.frame('bottom-frame',[48,789,1504,108]);
 b+=r.rect(256,810,1078,66,'#2d2923')+r.frame('chain-slot-frame',[256,810,1078,66])+r.text(288,850,'结算内容位置',22)+r.text(78,850,'按钮位置',18,'#bfb29a');
 b+=r.text(48,950,'另含四角 + 四边拆分版本；原有按钮和文字继续独立放置',19,'#bfb29a')+r.text(48,994,'2× 源图的切片像素翻倍，目标逻辑边距保持不变',18,'#bfb29a');
 save('asset-sheet',r.svg(1600,1040,b));
}
// Optional comparison inputs are images rendered from the same supplied demo scene.
if(process.argv[2]&&process.argv[3]){
 const oldUri=imageUri(path.resolve(process.argv[2])),newUri=imageUri(path.resolve(process.argv[3]));
 const r=renderer();let b=r.rect(0,0,1600,880,'#302c26')+r.text(48,63,'主界面边缘 · 同一布局对照',32)+r.text(48,103,'旧素材与新素材均使用提供的演示场景；非用户程序运行结果',18,'#bfb29a');
 for(const [label,uri,x] of [['旧边框',oldUri,48],['新细边框',newUri,832]]){
  b+=r.text(x,163,label,26,label==='新细边框'?'#d8bc6a':'#bfb29a');
  b+=r.crop(uri,1920,1080,[0,942,480,138],[x,192,720,207])+r.text(x,431,'左下角',17,'#bfb29a');
  b+=r.crop(uri,1920,1080,[1440,942,480,138],[x,471,720,207])+r.text(x,710,'右下角',17,'#bfb29a');
 }
 b+=r.text(48,782,'新框去除粗厚角块；底栏底图同步清除旧白角',22)+r.text(48,828,'参考布局中，主外框与底栏边框分别位于各自容器边界',17,'#bfb29a');
 save('edge-comparison',r.svg(1600,880,b));
}
console.log('Rendered frame asset sheet and optional edge comparison');
