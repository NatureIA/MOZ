'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs');
const {chromium}=require('playwright');
const css=fs.readFileSync('wwwroot/style.css','utf8'),app=fs.readFileSync('wwwroot/app.js','utf8');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try{
 for(const width of [360,390,768,1440]){
 const page=await browser.newPage({viewport:{width,height:850}});
 await page.setContent('<header><a class="brand"><span class="brand-word">MOZ.</span></a><div id="session"><span id="who">Operador de logística</span><button>Notificações <b>0</b></button><button>Sair</button></div></header><main><div class="workspace"><aside class="side"><div class="sidebar-heading"><div class="side-label">Navegação</div><button id="sidebarToggle">↔</button></div></aside><button id="sidebarBackdrop" class="sidebar-backdrop" hidden></button><section class="main-area"><div class="page-heading"><h1>Tracking de viagens</h1></div><div id="content"><div class="operations-layout tracking-only"><section class="panel"><div class="table-scroll"><table class="operation-table"><thead><tr><th>Viagem</th><th>Status</th><th>Motorista</th><th>Ação</th></tr></thead><tbody><tr data-trip-row="a"><th scope="row">MOZ-001</th><td data-label="Status">Programada</td><td data-label="Motorista">Motorista Exemplo</td><td class="trip-card-action" data-label="Ação"><button class="open-trip">Detalhes</button></td></tr></tbody></table></div></section></div></div></section></div></main>');
 await page.addStyleTag({content:css});
 const start=app.indexOf('const sidebarIcons='),end=app.indexOf('async function render()',start);
 assert.ok(start>=0&&end>start);
 await page.addScriptTag({content:"let me={id:'demo',role:'operator'};const esc=String;"+app.slice(start,end)+";document.querySelector('.side').insertAdjacentHTML('beforeend',nav());bindSidebar(document.querySelector('.workspace'));applySidebar(document.querySelector('.workspace'),true);"});
 const get=()=>page.evaluate(()=>{const s=document.querySelector('.side'),m=document.querySelector('.main-area'),r=document.querySelector('[data-trip-row]'),n=document.querySelector('.nav');return{side:s.getBoundingClientRect().toJSON(),main:m.getBoundingClientRect().toJSON(),row:getComputedStyle(r).display,nav:getComputedStyle(n).flexDirection,overflow:document.documentElement.scrollWidth>innerWidth,expanded:document.querySelector('#sidebarToggle').getAttribute('aria-expanded'),backdrop:document.querySelector('#sidebarBackdrop').hidden}});
 const collapsed=await get();assert.equal(collapsed.nav,'column');assert.equal(collapsed.expanded,'false');
 await page.locator('#sidebarToggle').click();const expanded=await get();
 assert.equal(expanded.nav,'column');assert.equal(expanded.expanded,'true');assert.ok(expanded.side.width>collapsed.side.width);assert.ok(Math.abs(expanded.side.y-collapsed.side.y)<2);
 if(width<=900){assert.equal(expanded.main.x,collapsed.main.x);assert.equal(expanded.main.width,collapsed.main.width);assert.equal(expanded.row,'grid');assert.equal(expanded.backdrop,false);assert.equal(expanded.overflow,false);await page.locator('#sidebarBackdrop').click({position:{x:width-70,y:300}});assert.equal((await get()).expanded,'false');}
 else{assert.ok(expanded.main.width<collapsed.main.width);assert.equal(expanded.row,'table-row');}
 await page.close();
 }
 console.log('Responsive browser layout: vertical rail/drawer at 360, 390, 768 px; desktop grid at 1440 px.');
 }finally{await browser.close()}
})().catch(e=>{console.error(e);process.exitCode=1});
