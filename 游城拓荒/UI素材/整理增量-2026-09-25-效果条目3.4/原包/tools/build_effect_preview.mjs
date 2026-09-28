import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawnSync} from 'node:child_process';
import {renderEffectRow,EFFECT_ROW_CSS,EFFECT_ROW_LAYOUT,normalizeEffectRow,effectRowLayout} from '../components/effect-row.mjs';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const raw=JSON.parse(fs.readFileSync(path.join(root,'assets/effect-assets.json'),'utf8'));
const catalog={};
for(const [id,v] of Object.entries(raw.assets))catalog['v33-'+id]={...v,path:'data:image/png;base64,'+fs.readFileSync(path.join(root,v.path)).toString('base64')};
for(let i=1;i<16;i++)catalog['v33-marker-'+i]={path:'data:image/png;base64,'+fs.readFileSync(path.join(root,'assets/markers/cooperation-'+i.toString(16).padStart(2,'0')+'-64.png')).toString('base64')};
function makeEnv(cat){
 let clips=0;const defs=[];
 const E=v=>String(v).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
 const box=(x,y,w,h,fill,stroke='none',sw=1,rx=0)=>`<rect x="${x}" y="${y}" width="${w}" height="${h}" fill="${fill}" stroke="${stroke}" stroke-width="${sw}" rx="${rx}"/>`;
 const txt=(x,y,s,size=20,fill='#ece4d4',weight=400,anchor='start')=>`<text x="${x}" y="${y}" font-size="${size}" fill="${fill}" font-weight="${weight}" text-anchor="${anchor}" dominant-baseline="central">${E(s)}</text>`;
 const line=(x,y,xx,yy,stroke='#7d705b',sw=1)=>`<path d="M${x} ${y}L${xx} ${yy}" fill="none" stroke="${stroke}" stroke-width="${sw}"/>`;
 const image=(file,x,y,w,h)=>`<image x="${x}" y="${y}" width="${w}" height="${h}" preserveAspectRatio="none" xlink:href="${E(file)}"/>`;
 function crop(file,x,y,w,h,sx,sy,sw,sh,iw,ih){const id='sheet-clip-'+(++clips);defs.push(`<clipPath id="${id}">${box(x,y,w,h,'white')}</clipPath>`);const a=w/sw,b=h/sh;return `<g clip-path="url(#${id})">${image(file,x-sx*a,y-sy*b,iw*a,ih*b)}</g>`;}
 function asset(id,x,y,w,h,fallback='',slice=false,target=12){const a=cat[id];if(!a)return fallback;if(!slice||!a.slices)return image(a.path,x,y,w,h);const[iw,ih]=a.viewBox||a.size,[l,t,r,b]=a.slices;if(!l&&!r&&!t&&!b)return image(a.path,x,y,w,h);const f=Math.min(1,target/Math.max(l,t,r,b)),ls=l*f,ts=t*f,rs=r*f,bs=b*f;const sx=[0,l,iw-r],sy=[0,t,ih-b],sw=[l,iw-l-r,r],sh=[t,ih-t-b,b],dx=[x,x+ls,x+w-rs],dy=[y,y+ts,y+h-bs],dw=[ls,w-ls-rs,rs],dh=[ts,h-ts-bs,bs];let out='';for(let j=0;j<3;j++)for(let i=0;i<3;i++)if(sw[i]&&sh[j]&&dw[i]>0&&dh[j]>0)out+=crop(a.path,dx[i],dy[j],dw[i],dh[j],sx[i],sy[j],sw[i],sh[j],iw,ih);return out;}
 return {box,txt,line,asset,defs,marker:(mask,x,y,size)=>asset('v33-marker-'+mask,x,y,size,size)};
}
function sheet(cat){
 const e=makeEnv(cat),{box,txt,line,asset}=e;
 let z=box(0,0,1600,1100,'#302b25')+txt(48,55,'通用效果条目 · 3.4',32)+txt(48,96,'暖灰棕底 / 轻纸纹 / 米灰细框',18,'#b8ad99')+line(48,129,1552,129,'#625748');
 const sets=[
  {x:48,label:'选择执行',note:'只显示右侧「使用」入口',mode:'choice_execute',rows:[{title:'执行选项 A',description:'选项内容由当前效果提供',right:{status:'use'}},{title:'执行选项 B',description:'较长的选项说明可换行；点击详情查看完整内容',right:{status:'use'}},{title:'暂不可执行的选项',description:'条件尚未满足',right:{status:'use',enabled:false}}]},
  {x:565,label:'玩家永续明细',note:'左侧 0—2 个标记；右侧状态可选',mode:'persistent_detail',rows:[{title:'角色牌上的标记',description:'两个独立区域，分别展示玩家组合',markers:[{mask:5},{mask:10}],right:{status:'unused'}},{title:'已使用的永续效果',description:'保留效果内容，状态图标只读',markers:[{mask:4,count:3}],right:{status:'used'}},{title:'持续生效的条目',description:'没有使用次数时隐藏右侧状态',markers:[{mask:15}],right:{status:'none'}}]},
  {x:1082,label:'快速行动',note:'同一条目，可独立开关左右插槽',mode:'quick_action',rows:[{title:'可执行的快速行动',description:'左侧标记与右侧使用入口可同时显示',markers:[{mask:8,count:2}],right:{status:'use'}},{title:'本次已经使用',description:'具体重置时机由游戏规则决定',right:{status:'used'}},{title:'待查看的快速行动',description:'未使用是状态，不代表当前可以执行',markers:[{mask:3},{mask:12}],right:{status:'unused'}}]}
 ];
 sets.forEach((set,col)=>{z+=txt(set.x,169,set.label,24,'#dfc364',500)+txt(set.x,207,set.note,16,'#c1b49e');set.rows.forEach((row,i)=>{z+=renderEffectRow({x:set.x,y:241+i*98,w:470,h:84,id:'sheet-'+col+'-'+i,mode:set.mode,...row},e)});});
 z+=line(48,551,1552,551,'#625748')+txt(48,592,'插槽按需收回空间',23,'#dfc364',500);
 const variants=[{x:48,title:'无附加区域',markers:[],right:{status:'none'}},{x:565,title:'两个标记区域',markers:[{mask:7},{mask:8}],right:{status:'none'}},{x:1082,title:'仅显示右侧入口',markers:[],right:{status:'use'}}];
 variants.forEach((r,i)=>z+=renderEffectRow({x:r.x,y:626,w:470,h:76,id:'variant-'+i,mode:'quick_action',description:'同一个预制体，文字自动获得剩余宽度',...r},e));
 z+=txt(48,749,'查看按钮',20,'#dfc364',500);
 z+=asset('v33-toggle-bg-default',48,779,148,42,'',true,8)+txt(66,800,'永续明细',17)+asset('v33-icon-chevron-down-light',164,789,22,22);
 z+=asset('v33-toggle-bg-expanded',218,779,148,42,'',true,8)+txt(236,800,'收起明细',17)+asset('v33-icon-chevron-up-light',334,789,22,22);
 z+=txt(414,800,'向下展开；下方城市区域随之缩短，原图保持比例',17,'#c1b49e');
 z+=txt(48,880,'玩家拼色标记 · 15 种非空组合',20,'#dfc364',500);
 for(let i=1;i<16;i++){const x=48+(i-1)*65;z+=e.marker(i,x,910,42);}
 z+=line(48,996,1552,996,'#625748')+txt(48,1031,'底图 / 边框 / 标记 / 图标 / 文字分别放置；预览条目内容仅为布局示例',16,'#b3a58d')+txt(48,1063,'箭头 = 使用入口     空心圆 = 未使用     对勾 = 已使用；禁用使用入口与已使用状态分开',15,'#b3a58d');
 return `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="1600" height="1100" viewBox="0 0 1600 1100"><defs>${e.defs.join('')}</defs><g font-family="Noto Sans CJK SC,Microsoft YaHei,sans-serif">${z}</g></svg>`;
}
const out=path.join(root,'preview');fs.mkdirSync(out,{recursive:true});
fs.writeFileSync(path.join(out,'effect-rows.svg'),sheet(catalog));
let component=fs.readFileSync(path.join(root,'components/effect-row.mjs'),'utf8').replaceAll('export ','');
const html=`<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>通用效果条目 v3.4</title><style>
*{box-sizing:border-box}body{margin:0;background:#302b25;color:#ece4d4;font:16px 'Microsoft YaHei','Noto Sans CJK SC',sans-serif}main{max-width:1280px;margin:28px auto;padding:0 24px}h1{font-size:25px;font-weight:500}p{color:#b8ac97;line-height:1.7}.controls{display:flex;flex-wrap:wrap;gap:18px;padding:20px 0}label{display:flex;gap:10px;align-items:center}select,button{background:#484036;color:#eee8d8;border:1px solid #91806a;border-radius:5px;font:inherit;padding:8px}#live{background:#28241f;border:1px solid #766956;padding:14px;border-radius:8px}#live svg{width:100%;max-width:1060px;display:block}.overview svg{display:block;width:100%;height:auto}#notice{min-height:32px;color:#e0c66c}.caption{font-size:14px}.use-control:focus{outline:none}${EFFECT_ROW_CSS}</style><main><h1>通用效果条目 · 可切换模式</h1><p>同一条目共用边框和底图，左侧玩家标记与右侧状态按模式显示喵。</p><div class="controls"><label>模式<select id="mode"><option value="choice_execute">选择执行</option><option value="persistent_detail">永续明细</option><option value="quick_action" selected>快速行动</option></select></label><label>左侧方块<select id="slots"><option value="0">无</option><option value="1">1 个</option><option value="2" selected>2 个</option></select></label><label>右侧内容<select id="right"><option value="none">无</option><option value="unused">未使用</option><option value="used">已使用</option><option value="use" selected>使用入口</option></select></label><label><input id="enabled" type="checkbox" checked>允许使用</label><button id="reset">恢复默认</button></div><div id="live"></div><p id="geometry" class="caption"></p><p id="notice" role="status">可切换插槽并点击右侧使用图标；示例不提交游戏指令喵。</p><div class="overview">${sheet(catalog)}</div></main><script>${component}
const catalog=${JSON.stringify(catalog)};
${makeEnv.toString()}
const byId=id=>document.getElementById(id);
function redraw(){const choice=byId('mode').value==='choice_execute';byId('slots').disabled=choice;byId('right').disabled=choice;const p={x:18,y:14,w:1000,h:84,id:'live',title:'可复用的效果条目',description:'点击查看完整说明；左右区域隐藏后，文字区域自动扩展',bodyAction:'details-live',mode:byId('mode').value,markers:[{mask:5},{mask:10}].slice(0,Number(byId('slots').value)),right:{status:byId('right').value,enabled:byId('enabled').checked}};const e=makeEnv(catalog),body=renderEffectRow(p,e),l=effectRowLayout(p);byId('live').innerHTML='<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 1040 112"><defs>'+e.defs.join('')+'</defs><g font-family="Microsoft YaHei,Noto Sans CJK SC,sans-serif">'+body+'</g></svg>';byId('geometry').textContent='当前：左侧 '+l.data.markers.length+' 个方块 · 右侧 '+({none:'无',unused:'未使用',used:'已使用',use:'使用入口'}[l.data.right.status])+' · 文字区域 '+l.textWidth+' 像素喵。';}
document.querySelectorAll('select,input').forEach(el=>el.addEventListener('change',redraw));byId('reset').onclick=()=>{byId('mode').value='quick_action';byId('slots').value='2';byId('right').value='use';byId('enabled').checked=true;redraw();};
function activate(e){const a=e.target.closest('[data-action]');if(!a||a.getAttribute('aria-disabled')==='true')return;if(e.type==='keydown'&&!['Enter',' '].includes(e.key))return;e.preventDefault();byId('notice').textContent=a.dataset.action==='details-live'?'完整说明：此处接入原牌与效果详情，示例保留全文，不执行游戏规则喵。':'已识别“使用”点击；是否执行成功、是否改成已使用应由游戏规则层返回喵。';}
byId('live').addEventListener('click',activate);byId('live').addEventListener('keydown',activate);redraw();</script></html>`;
fs.writeFileSync(path.join(out,'effect-rows.html'),html);
const result=spawnSync('inkscape',[path.join(out,'effect-rows.svg'),'--export-type=png','--export-filename='+path.join(out,'effect-rows.png')],{encoding:'utf8',env:process.env});if(result.status!==0)throw new Error(result.stderr);
console.log('Rendered effect-rows.svg/png and standalone interactive HTML');
