/** Geometry only. Source rectangles use image pixels; destination rectangles use UI units. */
export function nineSliceParts({sourceSize,sourceBorders,targetRect,targetBorders,drawCenter=false}){
 const [iw,ih]=sourceSize,[sl,st,sr,sb]=sourceBorders,[x,y,w,h]=targetRect;
 const [l,t,r,b]=targetBorders;
 const nums=[iw,ih,sl,st,sr,sb,x,y,w,h,l,t,r,b];
 if(nums.some(v=>!Number.isFinite(v)))throw new Error('Nine-slice requires finite numeric dimensions.');
 if(iw<=0||ih<=0||[sl,st,sr,sb,l,t,r,b].some(v=>v<0)||sl+sr>=iw||st+sb>=ih||w<l+r||h<t+b)throw new Error('Invalid source borders or target smaller than fixed corner regions.');
 const sx=[0,sl,iw-sr],sy=[0,st,ih-sb],sw=[sl,iw-sl-sr,sr],sh=[st,ih-st-sb,sb];
 const dx=[x,x+l,x+w-r],dy=[y,y+t,y+h-b],dw=[l,w-l-r,r],dh=[t,h-t-b,b];
 const names=[['top-left','top','top-right'],['left','center','right'],['bottom-left','bottom','bottom-right']];
 const parts=[];
 for(let row=0;row<3;row++)for(let col=0;col<3;col++){
  if((!drawCenter&&row===1&&col===1)||sw[col]===0||sh[row]===0||dw[col]===0||dh[row]===0)continue;
  parts.push({name:names[row][col],source:[sx[col],sy[row],sw[col],sh[row]],destination:[dx[col],dy[row],dw[col],dh[row]]});
 }
 return parts;
}
