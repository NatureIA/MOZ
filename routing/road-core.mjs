// Local, conservative road matching. Coordinates are never sent to a routing service.
// Road geometry comes from the same OpenFreeMap tiles used by the portal.
const R=6371000, rad=Math.PI/180;
export const distance=(a,b)=>{const x=(b[0]-a[0])*rad*Math.cos((a[1]+b[1])*rad/2),y=(b[1]-a[1])*rad;return R*Math.hypot(x,y)};
const point=p=>[Number(p.lon),Number(p.lat)];
const valid=p=>p&&p.lon!=null&&p.lat!=null&&Number.isFinite(+p.lon)&&Number.isFinite(+p.lat)&&Math.abs(p.lon)<=180&&Math.abs(p.lat)<=85;
export function usablePair(a,b,gap){if(!valid(a)||!valid(b)||a.trackingSince!==b.trackingSince)return false;const dt=(Date.parse(b.at)-Date.parse(a.at))/1000,d=distance(point(a),point(b));return dt>0&&dt<=Math.min(gap||120,120)&&d>=5&&d<=Math.min(3500,55*dt);}
const key=(p,level)=>Math.round(p[0]*1e5)+','+Math.round(p[1]*1e5)+':'+level;
const allowed=new Set(['motorway','trunk','primary','secondary','tertiary','minor','service','track','residential','unclassified','road','living_street','motorway_link','trunk_link','primary_link','secondary_link','tertiary_link']);
export function graph(roads){
 const nodes=new Map(),edges=[],seen=new Set();
 const node=(p,level)=>{const k=key(p,level);if(!nodes.has(k))nodes.set(k,{p,adj:[]});return k};
 for(const road of roads){if(!allowed.has(road.class))continue;const level=String(road.layer??0)+':'+String(road.brunnel??'');
 for(const line of road.lines||[])for(let i=1;i<line.length;i++){const a=line[i-1],b=line[i];if(!Array.isArray(a)||!Array.isArray(b)||!a.every(Number.isFinite)||!b.every(Number.isFinite))continue;const len=distance(a,b);if(len<.3)continue;
 const u=node(a,level),v=node(b,level);if(u===v)continue;const id=[u,v].sort().join('|');if(seen.has(id))continue;seen.add(id);const edge={a,b,u,v,len};edges.push(edge);nodes.get(u).adj.push({to:v,len});nodes.get(v).adj.push({to:u,len});
 }}
 return {nodes,edges};
}
function project(p,e){const cos=Math.cos(p[1]*rad),x=(e.a[0]-p[0])*cos,y=e.a[1]-p[1],dx=(e.b[0]-e.a[0])*cos,dy=e.b[1]-e.a[1],den=dx*dx+dy*dy;const t=Math.max(0,Math.min(1,den?-(x*dx+y*dy)/den:0)),q=[e.a[0]+(e.b[0]-e.a[0])*t,e.a[1]+(e.b[1]-e.a[1])*t];return {e,t,q,d:distance(p,q)}}
function candidates(g,p,accuracy){const limit=Math.min(65,Math.max(20,Number(accuracy)||20)*1.5+8),best=[];for(const e of g.edges){const c=project(p,e);if(c.d>limit)continue;best.push(c);best.sort((a,b)=>a.d-b.d);if(best.length>3)best.pop()}return best}
function search(g,start,end,max){
 const dist=new Map([[start,0]]),prev=new Map(),heap=[[0,start]];
 const push=x=>{let i=heap.length;heap.push(x);while(i){let p=(i-1)>>1;if(heap[p][0]<=x[0])break;heap[i]=heap[p];i=p}heap[i]=x};
 const pop=()=>{const top=heap[0],last=heap.pop();if(heap.length){let i=0;while(i*2+1<heap.length){let j=i*2+1;if(j+1<heap.length&&heap[j+1][0]<heap[j][0])j++;if(heap[j][0]>=last[0])break;heap[i]=heap[j];i=j}heap[i]=last}return top};
 let iterations=0;while(heap.length&&iterations++<14000){const [d,u]=pop();if(d!==dist.get(u))continue;if(d>max)return null;if(u===end){const path=[];let k=u;while(k!=null){path.push(g.nodes.get(k).p);k=prev.get(k)}return {len:d,path:path.reverse()}}for(const e of g.nodes.get(u).adj){const n=d+e.len;if(n<=max&&n<(dist.get(e.to)??Infinity)){dist.set(e.to,n);prev.set(e.to,u);push([n,e.to])}}}return null
}
export function matchPair(g,a,b){
 const p=point(a),q=point(b),direct=distance(p,q),dt=(Date.parse(b.at)-Date.parse(a.at))/1000,max=Math.min(direct*2.2+45,dt*55+20);if(!Number.isFinite(max)||max<=0)return null;
 const A=candidates(g,p,a.accuracy),B=candidates(g,q,b.accuracy);let best=null,score=Infinity;
 const accept=(path,len,x,y)=>{if(len>max||len<direct*.45)return;const s=(x.d+y.d)*3+Math.abs(len-direct)*.7;if(s<score){score=s;best=path}};
 for(const x of A)for(const y of B){
 if(x.e===y.e){accept([x.q,y.q],Math.abs(x.t-y.t)*x.e.len,x,y);continue}
 const starts=[[x.e.u,x.t*x.e.len],[x.e.v,(1-x.t)*x.e.len]],ends=[[y.e.u,y.t*y.e.len],[y.e.v,(1-y.t)*y.e.len]];
 for(const [u,du] of starts)for(const [v,dv] of ends){if(du+dv>max)continue;const r=search(g,u,v,max-du-dv);if(r)accept([x.q,...r.path,y.q],du+r.len+dv,x,y)}
 }
 return best&&best.filter((p,i)=>i===0||distance(p,best[i-1])>.1).length>=2?best:null;
}
