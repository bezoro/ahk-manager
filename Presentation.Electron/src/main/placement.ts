import type { WindowPlacement } from '../shared/contracts.js';
export interface Rect { x:number;y:number;width:number;height:number }
export interface Monitor { workArea:Rect;scaleFactor:number }
export function restorePlacement(saved:WindowPlacement,monitors:readonly Monitor[]):Rect {
  const fallback=monitors[0]?.workArea??{x:0,y:0,width:1280,height:800};
  const scale=Number.isFinite(saved.dpi)&&saved.dpi>=48&&saved.dpi<=768?saved.dpi/96:1;
  const candidate={x:Math.round(saved.x/scale),y:Math.round(saved.y/scale),width:Math.round(saved.width/scale),height:Math.round(saved.height/scale)};
  const monitor=monitors.find(m=>candidate.x<m.workArea.x+m.workArea.width&&candidate.x+candidate.width>m.workArea.x&&candidate.y<m.workArea.y+m.workArea.height&&candidate.y+candidate.height>m.workArea.y)?.workArea??fallback;
  const width=Math.min(monitor.width,Math.max(760,candidate.width||1200));
  const height=Math.min(monitor.height,Math.max(560,candidate.height||780));
  return {x:Math.max(monitor.x,Math.min(candidate.x,monitor.x+monitor.width-width)),y:Math.max(monitor.y,Math.min(candidate.y,monitor.y+monitor.height-height)),width,height};
}
export function capturePlacement(bounds:Rect,scale:number,maximized:boolean):WindowPlacement {
  return {x:Math.round(bounds.x*scale),y:Math.round(bounds.y*scale),width:Math.round(bounds.width*scale),height:Math.round(bounds.height*scale),maximized,dpi:scale*96};
}
