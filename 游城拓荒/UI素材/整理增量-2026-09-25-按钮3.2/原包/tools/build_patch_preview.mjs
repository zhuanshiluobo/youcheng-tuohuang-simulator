import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawnSync} from 'node:child_process';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const assets=JSON.parse(fs.readFileSync(path.join(root,'assets/button-assets.json'),'utf8')).assets;
let defs='',clip=0;
const txt=(x,y,s,size=20,color='#e5e3d7',anchor='start',weight=400)=>`<text x="${x}" y="${y}" dominant-baseline="central" font-family="Noto Sans CJK SC,Microsoft YaHei,sans-serif" font-size="${size}" fill="${color}" text-anchor="${anchor}" font-weight="${weight}">${s}</text>`;
const pic=(a,x,y,w,h)=>`<image xlink:href="../${a.path}" x="${x}" y="${y}" width="${w}" height="${h}" preserveAspectRatio="none"/>`;
function nine(id,x,y,w,h){
 const a=assets[id],[iw,ih]=a.viewBox,[l,t,r,b]=a.slices;
 const xs=[0,l,iw-r],ys=[0,t,ih-b],ws=[l,iw-l-r,r],hs=[t,ih-t-b,b],dx=[x,x+l,x+w-r],dy=[y,y+t,y+h-b],dw=[l,w-l-r,r],dh=[t,h-t-b,b];let z='';
 for(let j=0;j<3;j++)for(let i=0;i<3;i++){const id='crop'+(++clip);defs+=`<clipPath id="${id}" clipPathUnits="userSpaceOnUse"><rect x="${dx[i]}" y="${dy[j]}" width="${dw[i]}" height="${dh[j]}"/></clipPath>`;const kx=dw[i]/ws[i],ky=dh[j]/hs[j];z+=`<g clip-path="url(#${id})">${pic(a,dx[i]-xs[i]*kx,dy[j]-ys[j]*ky,iw*kx,ih*ky)}</g>`;}return z;
}
function button(x,y,w,h,label,family,glyph,state='default',fixed=false){
 const ink=state==='disabled'?'muted':family==='primary'||state.startsWith('selected')?'dark':'light';
 let z=`<g class="normal">${nine(family+'-'+state,x,y,w,h)}</g>`;
 if(!fixed&&state!=='disabled')z+=`<g class="hover">${nine(family+'-'+(state==='selected'?'selected-hover':'hover'),x,y,w,h)}</g><g class="pressed">${nine(family+'-'+(state==='selected'?'selected':'pressed'),x,y,w,h)}</g>`;
 const icon=family==='action'?44:36,ix=x+(family==='action'?24:18),tx=family==='action'?x+(w+72)/2:x+(w+40)/2;
 z+=pic(assets['icon-'+glyph+'-'+ink],ix,y+(h-icon)/2,icon,icon);
 if(family==='action')z+=`<path d="M${x+80} ${y+16}V${y+h-16}" stroke="#899189" stroke-width=".7" opacity=".5"/>`;
 z+=txt(tx,y+h/2,label,23,ink==='dark'?'#263030':ink==='muted'?'#747e77':'#ecebda','middle',family==='primary'||state==='selected'?600:500);
 return `<g class="control ${fixed?'fixed':''} ${state==='disabled'?'disabled':''}" tabindex="${state==='disabled'?-1:0}" role="button" aria-label="${label}" aria-disabled="${state==='disabled'}">${z}</g>`;
}
const rule=(y)=>`<path d="M52 ${y}H1548" stroke="#565d58" stroke-width="1"/>`;
let body='<rect width="1600" height="1040" fill="#252b2a"/>';
body+=txt(52,54,'主界面按钮 · 3.2',33,'#ece8da','start',600)+txt(52,96,'完整边角 · 三类样式 · 独立底图与图标',18,'#aaaFA3');
body+=rule(124)+txt(52,153,'切换页签',22,'#d9c36d','start',500);
['主要行动','快速行动','城市样式'].forEach((label,i)=>body+=button(52+i*258,185,240,64,label,'tab',['main-actions','quick-actions','city-patterns'][i],i===0?'selected':'default'));
body+=txt(892,202,'浅色选中面与黄色下划线',22)+txt(892,234,'紧凑、扁平，保持页面导航的层次',17,'#a5ac9f');
body+=rule(283)+txt(52,314,'主要行动',22,'#d9c36d','start',500);
['部署','调度','探索','城市移动','建设','特殊行动'].forEach((label,i)=>body+=button(52+i%3*512,350+Math.floor(i/3)*92,472,74,label,'action',['deploy','dispatch','explore','city-move','build','special'][i]));
body+=rule(557)+txt(52,590,'底栏操作',22,'#d9c36d','start',500);
body+=button(52,641,190,64,'撤销','undo','undo')+button(265,641,190,64,'撤销','undo','undo','disabled');
body+=button(568,641,320,64,'结束行动','primary','end')+button(917,641,320,64,'结束行动','primary','end','disabled');
body+=txt(147,737,'可用',16,'#a7ad9e','middle')+txt(360,737,'不可用',16,'#818b82','middle')+txt(728,737,'可用',16,'#a7ad9e','middle')+txt(1077,737,'不可用',16,'#818b82','middle');
body+=rule(780)+txt(52,811,'交互状态 · 主要行动示例',22,'#d9c36d','start',500);
['正常','悬停','按下','不可用'].forEach((label,i)=>{body+=txt(52+i*382,860,label,17,'#a5ae9f');body+=button(52+i*382,888,350,68,'建设','action','build',['default','hover','pressed','disabled'][i],true);});
body+=txt(52,1003,'图标与文字均可独立替换，按钮轮廓保持完整喵',16,'#929e91');
const style=`.hover,.pressed{display:none}.control:not(.fixed):not(.disabled){cursor:pointer}.control:not(.fixed):not(.disabled):hover .normal{display:none}.control:not(.fixed):not(.disabled):hover .hover{display:inline}.control:not(.fixed):not(.disabled):active .normal,.control:not(.fixed):not(.disabled):active .hover{display:none}.control:not(.fixed):not(.disabled):active .pressed{display:inline}.disabled{cursor:not-allowed}`;
const svg=`<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="1600" height="1040" viewBox="0 0 1600 1040"><defs>${defs}<style>${style}</style></defs>${body}</svg>`;
fs.mkdirSync(path.join(root,'preview'),{recursive:true});fs.writeFileSync(path.join(root,'preview/button-sheet.svg'),svg);
fs.writeFileSync(path.join(root,'preview/button-sheet.html'),`<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>主界面按钮补丁 v3.2</title><style>body{margin:0;background:#252b2a}svg{display:block;width:100%;max-width:1600px;height:auto;margin:auto}p{max-width:1496px;margin:16px auto;color:#aab1a2;font:15px 'Microsoft YaHei',sans-serif}</style><p>移动鼠标或按住按钮查看视觉状态，页面不提交游戏动作喵</p>${svg}</html>`);
const r=spawnSync('inkscape',[path.join(root,'preview/button-sheet.svg'),'--export-filename='+path.join(root,'preview/button-sheet.png')],{env:process.env,encoding:'utf8'});if(r.status!==0)throw Error(r.stderr);
console.log('Button sheet and offline HTML ready');
