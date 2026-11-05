import assert from 'node:assert/strict';import {createRequire} from 'node:module';
process.env.CONFIG_DIRECTORY='/tmp/seerrng-acquisition-probe';process.env.SEERR_EXTERNAL_CONFIG=JSON.stringify({clientId:'isolated-acquisition-probe',main:{},plex:{},jellyfin:{},oidc:{},tautulli:{},notifications:{agents:{}},network:{},radarr:[],sonarr:[],lidarr:[],readarr:[]});
const require=createRequire(import.meta.url),{default:ReadarrAPI}=require('/app/dist/api/servarr/readarr.js');
const api=new ReadarrAPI({url:'http://127.0.0.1:8789/api/v1',apiKey:'synthetic-e2e-restricted-key-20261105',mediaType:'ebook'});
const before=await api.getBook(31);assert.equal(before.statistics.bookFileCount,0);assert.equal(before.monitored,false);
console.log(JSON.stringify({phase:'before-request',bookId:before.id,monitored:before.monitored,fileCount:before.statistics.bookFileCount}));
const result=await api.addBook({id:31,author:before.author,title:'Fixture Book',foreignBookId:'30',qualityProfileId:4,metadataProfileId:2,rootFolderPath:'/library',monitored:true,editions:[{id:41,bookId:31,foreignEditionId:'41',title:'Fixture Ebook',isEbook:true,monitored:true}],addOptions:{searchForNewBook:true}});
console.log(JSON.stringify({phase:'request-submitted',bookId:result.id,monitored:result.monitored,createdBook:result.createdBook}));assert.equal(result.monitored,true);
