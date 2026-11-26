import assert from 'node:assert/strict';
import {VectorTile} from '@mapbox/vector-tile';
import Pbf from 'pbf';
const host='https://tiles.openfreemap.org';
async function get(url){let last;for(let i=0;i<3;i++){try{const r=await fetch(url,{signal:AbortSignal.timeout(12000)});if(!r.ok)throw Error('HTTP '+r.status);return r}catch(e){last=e;await new Promise(r=>setTimeout(r,500*(i+1)))}}throw last}
const meta=await (await get(host+'/planet/latest')).json();
const t=meta.tiles?.[0];assert.ok(typeof t==='string'&&t.startsWith(host+'/')&&t.includes('{z}'));
const z=14,n=2**z,lon=-46.6333,lat=-23.5505,x=Math.floor((lon+180)/360*n),y=Math.floor((1-Math.asinh(Math.tan(lat*Math.PI/180))/Math.PI)/2*n);
const url=t.replace('{z}',z).replace('{x}',x).replace('{y}',y);
const v=new VectorTile(new Pbf(new Uint8Array(await (await get(url)).arrayBuffer())));assert.ok(v.layers.transportation?.length>0,'public sample must provide road geometry');
const f=Array.from({length:v.layers.transportation.length},(_,i)=>v.layers.transportation.feature(i)).find(f=>f.type===2);
assert.ok(f?.toGeoJSON(x,y,z).geometry.coordinates.length>0);
console.log('Public map tile decoded successfully. No customer positions used.');