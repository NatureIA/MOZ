import assert from 'node:assert/strict';import {graph,matchPair,usablePair} from '../routing/road-core.mjs';
const at=s=>new Date(Date.UTC(2026,10,26,12,0,s)).toISOString(),a={lat:0,lon:0,accuracy:5,at:at(0)},b={lat:.001,lon:.001,accuracy:5,at:at(20)};
const g=graph([{class:'primary',lines:[[[0,0],[.001,0],[.001,.001]]]}]);
assert.ok(usablePair(a,b,120));const line=matchPair(g,a,b);assert.ok(line&&line.some(p=>p[0]===.001&&p[1]===0),'must follow corner rather than chord');
assert.equal(matchPair(g,a,{...b,lat:.005}),null);
assert.equal(usablePair(a,{...b,at:at(180)},120),false);
const disconnected=graph([{class:'primary',lines:[[[0,0],[.0003,0]],[[.001,.0007],[.001,.001]]]}]);assert.equal(matchPair(disconnected,a,b),null);
console.log('Road geometry: corner, no invented bridge, gaps, off-road rejection.');