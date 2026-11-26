import {VectorTile} from '@mapbox/vector-tile';
import Pbf from 'pbf';
import {graph,matchPair,usablePair,distance} from './road-core.mjs';
const host='https://tiles.openfreemap.org',z=14,n=2**z,tiles=new Map(),graphs=new Map();
let templatePromise;
async function template(){if(!templatePromise)templatePromise=(async()=>{const r=await fetch(host+'/planet/latest',{credentials:'omit',referrerPolicy:'no-referrer',signal:AbortSignal.timeout(10000)});if(!r.ok)throw Error('TileJSON');const j=await r.json(),t=j.tiles?.[0];if(typeof t!=='string'||!t.includes('{z}')||!t.includes('{x}')||!t.includes('{y}')||!t.startsWith(host+'/'))throw Error('Fonte cartográfica inválida');return t})().catch(e=>{templatePromise=null;throw e});return templatePromise}
const xy=p=>[Math.floor((p[0]+180)/360*n),Math.floor((1-Math.asinh(Math.tan(p[1]*Math.PI/180))/Math.PI)/2*n)];
async function tile(x,y){
 const k=x+'/'+y;if(tiles.has(k))return tiles.get(k);
 const promise=(async()=>{const t=await template(),url=t.replace('{z}',z).replace('{x}',x).replace('{y}',y);const r=await fetch(url,{credentials:'omit',referrerPolicy:'no-referrer',signal:AbortSignal.timeout(10000)});if(!r.ok)throw Error('Mapa indisponível');const layer=new VectorTile(new Pbf(new Uint8Array(await r.arrayBuffer()))).layers.transportation;const roads=[];if(layer)for(let i=0;i<layer.length;i++){const f=layer.feature(i);if(f.type!==2)continue;const g=f.toGeoJSON(x,y,z).geometry;if(g?.type==='LineString'||g?.type==='MultiLineString')roads.push({class:f.properties.class,layer:f.properties.layer,brunnel:f.properties.brunnel,lines:g.type==='LineString'?[g.coordinates]:g.coordinates})}return roads})().catch(e=>{tiles.delete(k);throw e});
 if(tiles.size>=160)tiles.delete(tiles.keys().next().value);tiles.set(k,promise);return promise
}
function keys(a,b){const p=[a.lon,a.lat],q=[b.lon,b.lat],lo=xy([Math.min(p[0],q[0])-.0015,Math.max(p[1],q[1])+.0015]),hi=xy([Math.max(p[0],q[0])+.0015,Math.min(p[1],q[1])-.0015]),list=[];for(let x=lo[0];x<=hi[0];x++)for(let y=lo[1];y<=hi[1];y++)if(x>=0&&x<n&&y>=0&&y<n)list.push([x,y]);return list.length<=12?list:[]}
async function localGraph(ks){const k=ks.map(x=>x.join('/')).join(';');if(graphs.has(k))return graphs.get(k);const g=graph((await Promise.all(ks.map(([x,y])=>tile(x,y)))).flat());if(graphs.size>=8)graphs.delete(graphs.keys().next().value);graphs.set(k,g);return g}
export async function match(fixes,gap,alive,onProgress){
 const lines=[];let matched=0,eligible=0,failed=0,limited=false;let current=null;
 for(let i=1;i<fixes.length;i++){
 if(!alive())return {lines,matched,eligible,failed,limited:true,cancelled:true};
 const a=fixes[i-1],b=fixes[i];if(!usablePair(a,b,gap)){current=null;continue;}
 eligible++;if(eligible>12000){limited=true;break}const ks=keys(a,b);if(!ks.length){current=null;continue}
 try{const g=await localGraph(ks);if(!alive())break;const path=matchPair(g,a,b);if(path){
 matched++;if(current&&distance(current[current.length-1],path[0])<3){current.push(...path.slice(1))}else{current=path.slice();lines.push(current)}
 }else current=null;}catch{failed++;current=null;if(failed>=8)break;}
 if(eligible%60===0){onProgress(lines,matched,eligible);await new Promise(r=>setTimeout(r,0))}
 }
 return {lines,matched,eligible,failed,limited}
}
window.MozRoadMatch={match};
