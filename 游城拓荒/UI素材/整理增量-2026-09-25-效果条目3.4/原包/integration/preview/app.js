(() => {
 const state={...window.PATCH_INITIAL_STATE,effectRowStatus:{}};
 const host=document.getElementById('game'),notice=document.getElementById('notice');
 let dragging=null;
 function say(s){notice.textContent=s+'喵';}
 function redraw(){
  const active=document.activeElement;
  const focusAction=active?.dataset?.action,focusZone=active?.dataset?.scrollZone,focusThumb=active?.dataset?.scrollThumb;
  const result=window.renderPatchScene(state,window.PATCH_ASSETS);host.innerHTML=result.svg;
  document.querySelectorAll('[data-demo]').forEach(b=>b.classList.toggle('active',b.dataset.demo===state.mode));
  document.querySelectorAll('[data-view]').forEach(b=>b.classList.toggle('active',b.dataset.view==='details'?state.expandedPlayer!==null:b.dataset.view==='quick'?state.tab==='quick':state.expandedPlayer===null));
  const undo=host.querySelector('[data-action="undo"]');if(undo){const reason=state.undoEnabled?'撤销上一步（演示状态）':'当前没有可撤销的步骤';undo.setAttribute('aria-label',reason);const title=document.createElementNS('http://www.w3.org/2000/svg','title');title.textContent=reason;undo.prepend(title);}
  const end=host.querySelector('[data-action="end"]');if(end&&state.mode==='resolve'){const title=document.createElementNS('http://www.w3.org/2000/svg','title');title.textContent='当前结算尚未完成';end.prepend(title);}
  const selector=focusAction?`[data-action="${focusAction}"]`:focusThumb?`[data-scroll-thumb="${focusThumb}"]`:focusZone?`[data-scroll-zone="${focusZone}"]`:null;
  if(selector)host.querySelector(selector)?.focus({preventScroll:true});
 }
 function scrollMax(zone){return Number(host.querySelector(`[data-scroll-zone="${zone}"]`)?.dataset.scrollMax)||0;}
 function setScroll(zone,value){state[zone+'Scroll']=Math.max(0,Math.min(scrollMax(zone),value));redraw();}
 function scroll(zone,delta){setScroll(zone,(state[zone+'Scroll']||0)+delta);}
 function activate(action){
  if(action==='collapse'){state.collapsed=!state.collapsed;}
  else if(action==='drawer'){state.drawerOpen=!state.drawerOpen;}
  else if(action.startsWith('player-detail-')){const i=Number(action.slice(14));state.expandedPlayer=state.expandedPlayer===i?null:i;state.detailScroll=0;say(state.expandedPlayer===null?'已收起永续效果明细，恢复我的城市高度':'向下展开永续效果明细，保持城市底边并压缩城市高度；条目内容为示例');}
  else if(/^(detail|quick)-scroll-(up|down)$/.test(action)){const [,zone,dir]=action.match(/^(detail|quick)-scroll-(up|down)$/);scroll(zone,(dir==='up'?-1:1)*(zone==='detail'?68:84));return;}
  else if(action.startsWith('row-use-')){say('使用图标点击已识别；实际可用状态与可执行次数由游戏提供，此预览不提交游戏指令');return;}
  else if(action.startsWith('tab-')){state.tab=action.slice(4);}
  else if(action==='undo'){if(!state.undoEnabled)return;state.optionalEffect=null;state.undoEnabled=false;say('示例：已撤销最近一次可撤销操作；实际结果应由游戏引擎返回');}
  else if(action.startsWith('optional-')){say('此入口应打开对应效果的子选择；当前为待选择示例，尚未应用抵扣');return;}
  else if(action==='action-4'){state.mode='resolve';state.drawerOpen=true;state.undoEnabled=true;say('显示建设支付的插入结算示例，不执行实际建设');}
  else if(action==='end'){if(state.mode==='resolve')return;say('结束行动点击已识别；此预览不提交游戏指令');}
  else if(action==='confirm-step'){return;}
  else{say('该入口保留；本增量预览仅演示界面布局与状态');return;}
  redraw();
 }
 host.addEventListener('click',e=>{const g=e.target.closest('[data-action]');if(g&&g.getAttribute('aria-disabled')!=='true')activate(g.dataset.action);});
 host.addEventListener('keydown',e=>{
  const area=e.target.closest('[data-scroll-zone]');
  if(area&&['ArrowUp','ArrowDown','PageUp','PageDown','Home','End'].includes(e.key)){
   e.preventDefault();const zone=area.dataset.scrollZone,step=zone==='detail'?68:84,page=zone==='detail'?196:248;
   if(e.key==='Home')setScroll(zone,0);else if(e.key==='End')setScroll(zone,scrollMax(zone));
   else scroll(zone,({'ArrowUp':-step,'ArrowDown':step,'PageUp':-page,'PageDown':page})[e.key]);return;
  }
  if(e.key==='Enter'||e.key===' '){const g=e.target.closest('[data-action]');if(g&&g.getAttribute('aria-disabled')!=='true'){e.preventDefault();activate(g.dataset.action);}}
 });
 host.addEventListener('wheel',e=>{const area=e.target.closest('[data-scroll-zone]');if(!area)return;e.preventDefault();const zone=area.dataset.scrollZone,multiplier=e.deltaMode===1?20:e.deltaMode===2?(zone==='detail'?196:248):1;scroll(zone,e.deltaY*multiplier);},{passive:false});
 function scenePoint(e){const svg=host.querySelector('svg'),point=svg.createSVGPoint();point.x=e.clientX;point.y=e.clientY;return point.matrixTransform(svg.getScreenCTM().inverse());}
 host.addEventListener('pointerdown',e=>{
  const thumb=e.target.closest('[data-scroll-thumb]'),track=e.target.closest('[data-scroll-track]');
  if(thumb){e.preventDefault();const zone=thumb.dataset.scrollThumb,bar=thumb.closest('[data-scrollbar-zone]');dragging={zone,startY:scenePoint(e).y,startScroll:state[zone+'Scroll']||0,max:Number(bar.dataset.scrollMax),travel:Number(bar.dataset.thumbTravel)};thumb.focus({preventScroll:true});}
  else if(track){e.preventDefault();const zone=track.dataset.scrollTrack,y=scenePoint(e).y;setScroll(zone,(y-Number(track.dataset.trackTop))/Number(track.dataset.trackHeight)*scrollMax(zone));}
 });
 window.addEventListener('pointermove',e=>{if(dragging){e.preventDefault();const {zone,startY,startScroll,max,travel}=dragging;setScroll(zone,startScroll+(scenePoint(e).y-startY)*max/Math.max(1,travel));}});
 window.addEventListener('pointerup',()=>{dragging=null;});
 window.addEventListener('pointercancel',()=>{dragging=null;});
 document.querySelectorAll('[data-demo]').forEach(b=>b.addEventListener('click',()=>{state.mode=b.dataset.demo;state.drawerOpen=state.mode==='resolve';state.undoEnabled=state.mode==='resolve';state.optionalEffect=null;redraw();say(state.mode==='resolve'?'演示待处理建设结算：撤销可用，结束行动不可用':'演示空闲底栏：撤销不可用，结束行动可用');}));
 document.querySelectorAll('[data-view]').forEach(b=>b.addEventListener('click',()=>{state.collapsed=false;state.mode='idle';state.drawerOpen=false;state.undoEnabled=false;state.detailScroll=0;state.quickScroll=0;state.effectRowStatus={};if(b.dataset.view==='details'){state.expandedPlayer=0;state.tab='actions';}else if(b.dataset.view==='quick'){state.expandedPlayer=0;state.tab='quick';}else{state.expandedPlayer=null;state.tab='actions';}redraw();say('切换演示布局；永续效果和快速行动内容均为示例数据');}));
 document.getElementById('undo-toggle').addEventListener('click',()=>{state.undoEnabled=!state.undoEnabled;redraw();say('仅切换撤销按钮的可用外观');});
 window.PATCH_PREVIEW={state,redraw,activate,scroll,setScroll};redraw();
})();
