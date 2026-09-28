/** Renderer-neutral data model; SVG adapter takes the host's drawing functions. */
export const EFFECT_ROW_LAYOUT={padding:12,markerSize:28,markerGap:4,slotGap:12,statusSize:32,standardHeight:76,compactHeight:60};
export function normalizeEffectRow(input){
 const mode=['choice_execute','persistent_detail','quick_action'].includes(input.mode)?input.mode:'persistent_detail';
 const markers=mode==='choice_execute'?[]:(input.markers||[]).filter(m=>m&&Number.isInteger(m.mask)&&m.mask>=1&&m.mask<=15).slice(0,2).map(m=>({mask:m.mask,...(Number.isInteger(m.count)&&m.count>=0?{count:m.count}:{}),label:m.label||''}));
 const right=mode==='choice_execute'?{status:'use',enabled:input.right?.enabled!==false}:{status:['used','unused','use'].includes(input.right?.status)?input.right.status:'none',enabled:input.right?.enabled!==false};
 return {...input,mode,markers,right,h:input.h||76,title:String(input.title||''),description:String(input.description||'')};
}
export function effectRowLayout(input){
 const p=normalizeEffectRow(input),L=EFFECT_ROW_LAYOUT;
 const leftWidth=p.markers.length?p.markers.length*L.markerSize+(p.markers.length-1)*L.markerGap+L.slotGap:0;
 const rightWidth=p.right.status==='none'?0:L.statusSize+L.slotGap;
 return {data:p,leftWidth,rightWidth,textX:p.x+L.padding+leftWidth,textWidth:p.w-2*L.padding-leftWidth-rightWidth,statusX:p.x+p.w-L.padding-L.statusSize};
}
const escape=v=>String(v).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
function truncate(s,width,size){let w=0,result='';for(const c of [...s]){w+=/[^\u0000-\u00ff]/.test(c)?size:size*.56;if(w>width-size)return result+'…';result+=c;}return result;}
function wrap(s,width,size,max=2){const lines=[];let line='',w=0;for(const c of [...s]){const cw=/[^\u0000-\u00ff]/.test(c)?size:size*.56;if(w+cw>width&&line){lines.push(line);line='';w=0;if(lines.length===max){lines[max-1]=truncate(lines[max-1]+'…',width,size);return lines;}}line+=c;w+=cw;}if(line)lines.push(line);return lines;}
export function renderEffectRow(input,env){
 const {asset,txt,box,line,marker}=env;
 const {data:p,leftWidth,rightWidth,textX,textWidth,statusX}=effectRowLayout(input);
 const {x,y,w,h,title,description,mode,markers,right}=p,L=EFFECT_ROW_LAYOUT;
 const compact=h<72,fs=compact?16:18,ds=compact?11.5:13;
 const disabled=right.status==='use'&&!right.enabled;
 const state=disabled?'disabled':'default';
 const fullDescription=title+(description?'，'+description:'');
 let content=`<title>${escape(fullDescription)}</title>`;
 const skin=s=>asset('v33-row-bg-'+s,x,y,w,h,'',true,12)+asset('v33-row-frame-'+s,x,y,w,h,'',true,12);
 content+=`<g class="row-normal">${skin(state)}</g>`;
 if(!disabled&&(p.bodyAction||right.status==='use'))content+=`<g class="row-hover" style="display:none">${skin('hover')}</g>`;
 markers.forEach((m,i)=>{const mx=x+L.padding+i*(L.markerSize+L.markerGap),my=y+(h-L.markerSize)/2;let v=marker(m.mask,mx,my,L.markerSize);
  const single=(m.mask&(m.mask-1))===0;
  if(single&&m.count!==undefined){v+=box(mx+5,my+6,L.markerSize-10,16,'#272219','none',0,3)+txt(mx+L.markerSize/2,my+14,m.count,12,'#fff',700,'middle');}
  content+=`<g role="img" aria-label="${escape(m.label||'玩家标记组合 '+m.mask+(m.count!==undefined?'，数量 '+m.count:''))}"><title>${escape(m.label||'玩家标记组合 '+m.mask)}</title>${v}</g>`;
 });
 if(leftWidth)content+=line(textX-6,y+14,textX-6,y+h-14,'#817360',.65);
 content+=txt(textX,description?y+(compact?19:22):y+h/2,truncate(title,textWidth,fs),fs,disabled?'#A89B83':'#E8DDC3',500);
 if(description){const lines=compact?[truncate(description,textWidth,ds)]:wrap(description,textWidth,ds,2);lines.forEach((s,i)=>content+=txt(textX,y+(compact?42:45)+i*16,s,ds,disabled?'#918471':'#BDAF99'));}
 if(p.bodyAction){content+=`<g data-action="${escape(p.bodyAction)}" role="button" tabindex="0" class="row-detail-hit" aria-label="${escape('查看'+title+'详情')}">${box(x+4,y+4,w-rightWidth-8,h-8,'#000').replace('fill="#000"','fill="#000" fill-opacity="0" pointer-events="all"')}<rect class="effect-focus" x="${x+4}" y="${y+4}" width="${w-rightWidth-8}" height="${h-8}" rx="4" fill="none" stroke="#e2c464" opacity="0" pointer-events="none"/></g>`;}
 if(right.status!=='none'){
  const sy=y+(h-L.statusSize)/2;
  const label=right.status==='use'?(right.enabled?'使用':'当前不可使用'):right.status==='used'?'已使用':'未使用';
  const icon=asset('v33-icon-'+right.status+'-'+(disabled||right.status==='used'?'muted':'light'),statusX+3,sy+3,26,26);
  if(right.status==='use'){
   const base=s=>asset('v33-status-bg-'+s,statusX,sy,32,32,'',true,6);
   content+=`<g data-action="row-use-${escape(p.id)}" class="use-control ${right.enabled?'enabled':'disabled'}" role="button" tabindex="${right.enabled?0:-1}" aria-disabled="${!right.enabled}" aria-label="${escape(label+'：'+title)}"><title>${escape(label+'：'+title)}</title><g class="use-normal">${base(disabled?'disabled':'default')}</g>${right.enabled?`<g class="use-hover" style="display:none">${base('hover')}</g><g class="use-pressed" style="display:none">${base('pressed')}</g>`:''}${icon}<rect class="effect-focus" x="${statusX-3}" y="${sy-3}" width="38" height="38" rx="7" fill="none" stroke="#e2c464" opacity="0" pointer-events="none"/></g>`;
  }else content+=`<g role="img" aria-label="${label}" data-status="${right.status}"><title>${label}</title>${icon}</g>`;
 }
 return `<g class="effect-row${!disabled&&(p.bodyAction||right.status==='use')?' interactive':''}" data-row-id="${escape(p.id)}" data-row-mode="${mode}" data-marker-slots="${markers.length}" data-right="${right.status}" data-text-width="${textWidth}">${content}</g>`;
}
export const EFFECT_ROW_CSS=`
.effect-row .row-hover,.use-control .use-hover,.use-control .use-pressed{display:none!important}
.effect-row.interactive:hover .row-normal{display:none}.effect-row.interactive:hover .row-hover{display:inline!important}
.use-control.enabled,.row-detail-hit{cursor:pointer}.use-control.disabled{cursor:not-allowed}
.use-control.enabled:hover .use-normal{display:none}.use-control.enabled:hover .use-hover{display:inline!important}
.use-control.enabled:active .use-normal,.use-control.enabled:active .use-hover{display:none!important}.use-control.enabled:active .use-pressed{display:inline!important}
.use-control:focus-visible,.row-detail-hit:focus-visible{outline:none}.use-control:focus-visible .effect-focus,.row-detail-hit:focus-visible .effect-focus{opacity:1}
`;
