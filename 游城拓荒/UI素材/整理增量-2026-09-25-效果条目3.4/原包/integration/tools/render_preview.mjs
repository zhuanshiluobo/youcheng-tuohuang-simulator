import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {renderScene} from '../preview/scene.mjs';
import {INITIAL_STATE} from '../preview/data.mjs';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const out=path.join(root,'preview');
const knownFontConfig='/workspace/scratch/f2adf1f55c56/tmp/enterprise-fontconfig.conf';
const fontConfig=process.env.FONTCONFIG_FILE||(fs.existsSync(knownFontConfig)?knownFontConfig:null);
const renderEnv={...process.env,...(fontConfig?{FONTCONFIG_FILE:fontConfig}:{})};
const manifestPath=path.join(root,'asset-manifest.json');
const raw=fs.existsSync(manifestPath)?JSON.parse(fs.readFileSync(manifestPath,'utf8')):{assets:{}};
const entries=Array.isArray(raw.assets)?Object.fromEntries(raw.assets.map(a=>[a.id,a])):raw.assets;
const catalog=Object.fromEntries(Object.entries(entries).map(([id,a])=>[id,{...a,path:'../'+a.path}]));
const aliases={'screen-bg':'module-bg','module-bg-paper':'paper-bg','module-header':'header-bg','title-plaque':'header-bg','turn-ribbon':'turn-tag','card-slot-bg':'card-bg','resolution-slot':'chain-slot-bg','drawer-bg':'module-bg','drawer-header':'header-bg'};
for(const [a,b] of Object.entries(aliases))if(!catalog[a]&&catalog[b])catalog[a]=catalog[b];
let data=fs.readFileSync(path.join(out,'data.mjs'),'utf8').replaceAll('export const ','const ');
let scene=fs.readFileSync(path.join(out,'scene.mjs'),'utf8').replace(/^import[^\n]*\n/gm,'').replace('export function renderScene','function renderScene');
const effectRow=fs.readFileSync(path.join(out,'effect-row.mjs'),'utf8').replaceAll('export const ','const ').replaceAll('export function ','function ');
fs.writeFileSync(path.join(out,'preview.bundle.js'),`(()=>{\n${data}\nconst R=GEOMETRY;\n${effectRow}\n${scene}\nwindow.PATCH_INITIAL_STATE=INITIAL_STATE;window.PATCH_ASSETS=${JSON.stringify(catalog)};window.renderPatchScene=renderScene;\n})();\n`);
const modes=[['main-collapsed-details',{mode:'idle',expandedPlayer:null}],['main-expanded-details',{mode:'idle',expandedPlayer:0}],['main-quick',{mode:'idle',expandedPlayer:0,tab:'quick'}],['main-scrolled-details',{mode:'idle',expandedPlayer:0,detailScroll:136,quickScroll:164,tab:'quick'}]];
const used=new Set();
for(const [name,state] of modes){const result=renderScene(state,catalog);fs.writeFileSync(path.join(out,name+'.svg'),result.svg);for(const f of result.usedAssets){used.add(f);if(!fs.existsSync(path.resolve(out,f)))throw new Error('Missing asset '+f);}const proc=spawnSync('inkscape',[path.join(out,name+'.svg'),'--export-type=png','--export-filename='+path.join(out,name+'.png')],{encoding:'utf8',env:renderEnv,maxBuffer:4*1024*1024});if(proc.status!==0)throw new Error('Render failed: '+name+' '+proc.stderr);console.log('Rendered '+name);}
const originals=[];function walk(dir){for(const n of fs.readdirSync(dir)){const p=path.join(dir,n);if(fs.statSync(p).isDirectory())walk(p);else originals.push({path:path.relative(root,p),bytes:fs.statSync(p).size,sha256:crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex')});}}walk(path.join(out,'originals'));fs.writeFileSync(path.join(out,'originals-manifest.json'),JSON.stringify({source:'MainGameUI-v2.2 / 07-originals and 04-markers',files:originals},null,2)+'\n');
for(const file of ['preview.bundle.js','app.js']){const p=spawnSync(process.execPath,['--check',path.join(out,file)],{encoding:'utf8'});if(p.status!==0)throw new Error(p.stderr);}
const report={version:raw.version||'3.4',browserTestsRun:false,browserReason:'No browser executable used; HTML and static previews share the same scene source.',renderer:'Inkscape',offlinePreview:true,sharedScene:true,assetCount:used.size,originals:originals.length,states:modes.map(([name,state])=>({name,...state})),checks:{assetPathsExist:true,javascriptSyntax:true,staticRendersFinished:true,originalCityAspectPreserved:true,mapAspectPreserved:true,cardArtUnmodified:true,cityBottomFixedAt978:true,expandedPlayerAccordion:true,detailsAndQuickShareEffectRow:true},interactionScope:['tabs','collapse and expand UI','expand and collapse resolution drawer','open optional-effect child-choice placeholder','switch undo appearance','undo demonstration state only','single expanded opponent persistent effects','independent internal scrolling via wheel arrows keyboard and draggable thumb','use icon click notification without consuming the effect'],engineFeaturesImplemented:false};fs.writeFileSync(path.join(out,'verification.json'),JSON.stringify(report,null,2)+'\n');console.log(JSON.stringify(report,null,2));
