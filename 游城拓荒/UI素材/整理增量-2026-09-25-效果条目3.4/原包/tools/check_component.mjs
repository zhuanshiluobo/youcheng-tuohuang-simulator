import assert from 'node:assert/strict';
import {renderEffectRow,normalizeEffectRow,effectRowLayout} from '../components/effect-row.mjs';
let cases=0;
const env={asset:()=>'',txt:(x,y,t)=>`<text>${t}</text>`,box:()=>'',line:()=>'',marker:()=>'<g/>'};
for(const mode of ['choice_execute','persistent_detail','quick_action'])for(let n=0;n<=2;n++)for(const status of ['none','unused','used','use'])for(const enabled of [true,false]){
 const p={id:'test',x:0,y:0,w:374,h:60,title:'示例',mode,markers:[{mask:1},{mask:10}].slice(0,n),right:{status,enabled}};
 const l=effectRowLayout(p),s=renderEffectRow(p,env),m=mode==='choice_execute'?0:n,r=mode==='choice_execute'?'use':status;
 assert.equal(l.data.markers.length,m);assert.equal(l.data.right.status,r);
 assert.equal(l.textWidth,374-24-(m?m*28+(m-1)*4+12:0)-(r==='none'?0:44));
 assert.equal(s.includes('data-action="row-use-test"'),r==='use');
 if(r==='use')assert(s.includes(`aria-disabled="${!enabled}"`));
 assert(l.textWidth>=234);cases++;
}
const invalid=normalizeEffectRow({markers:[{mask:0},{mask:16},{mask:2.3},{mask:4,count:0}],right:{status:'none'}});
assert.deepEqual(invalid.markers,[{mask:4,count:0,label:''}]);
const noSlots=effectRowLayout({x:0,y:0,w:374,markers:[],right:{status:'none'}});
const bothSlots=effectRowLayout({x:0,y:0,w:374,markers:[{mask:3},{mask:12}],right:{status:'use'}});
assert.equal(noSlots.textWidth-bothSlots.textWidth,116);
console.log(JSON.stringify({modeCasesPassed:cases,invalidMasksFiltered:true,zeroCountPreserved:true,optionalWidthReclaimed:true}));
