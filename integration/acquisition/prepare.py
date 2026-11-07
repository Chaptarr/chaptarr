from pathlib import Path
import sqlite3,json,hashlib,zipfile,os,subprocess
import sys
p=Path(sys.argv[1]).resolve();repo=Path(__file__).resolve().parents[2]
p.mkdir(parents=True,mode=0o700,exist_ok=False)
media_type=sys.argv[2] if len(sys.argv)>2 else 'ebook'
assert media_type in ['ebook','audiobook']
audio=media_type=='audiobook'
profile_id_space=sys.argv[3] if len(sys.argv)>3 else 'source'
assert profile_id_space in ['native','source']
for d in ['config','library','downloads','seed','qbit-seed/qBittorrent/config','qbit-download/qBittorrent/config','provider']:(p/d).mkdir(parents=True,mode=0o700,exist_ok=True)
db=p/'config/readarr.db'
assert not db.exists(),'Refuse to overwrite prior rehearsal'
with sqlite3.connect(db) as c:
 for file in ['BookshelfNG.Schema.158.sql','BookshelfNG.PopulatedMigration158.sql']:c.executescript((repo/'src/Chaptarr.Core.Test/Datastore/Fixtures'/file).read_text())
 c.executescript('''DELETE FROM MetadataFiles; DELETE FROM History; DELETE FROM BookFiles; DELETE FROM Notifications; DELETE FROM ImportLists; DELETE FROM Indexers; DELETE FROM DownloadClients;
 UPDATE AuthorMetadata SET ForeignAuthorId='10'; UPDATE Books SET ForeignBookId='30',Monitored=0; UPDATE Editions SET ForeignEditionId=CAST(Id AS TEXT),Images='[]',Links='[]'; UPDATE Editions SET Isbn13='9780306406157' WHERE Id=41;
 UPDATE QualityProfiles SET Cutoff=3,Items='[{"quality":3,"allowed":true}]' WHERE Id=2;''')
 if audio:
  c.execute("UPDATE QualityProfiles SET Cutoff=10,Items='[{\"quality\":10,\"allowed\":true}]' WHERE Id=1")
  c.execute("UPDATE Editions SET Asin='B000TEST40' WHERE Id=40")
 settings={'host':'127.0.0.1','port':8080,'musicCategory':'e2e','ebookCategory':'e2e','audiobookCategory':'e2e-audio','username':'','password':'','recentTvPriority':0,'olderTvPriority':0}
 c.execute('INSERT INTO DownloadClients(Id,Enable,Name,Implementation,Settings,ConfigContract,Priority,Tags,RemoveCompletedDownloads,RemoveFailedDownloads) VALUES (1,1,?,?,?,?,1,?,0,0)',('Isolated qBittorrent','QBittorrent',json.dumps(settings),'QBittorrentSettings','[]'))
 settings={'baseUrl':'http://127.0.0.1:8980','apiPath':'/api','apiKey':'synthetic-indexer-key','categories':[3000,3030] if audio else [7000,7020],'minimumSeeders':0,'seedCriteria':{'seedRatio':0,'seedTime':0}}
 c.execute('INSERT INTO Indexers(Id,Name,Implementation,Settings,ConfigContract,EnableRss,EnableAutomaticSearch,EnableInteractiveSearch,Priority,Tags,DownloadClientId) VALUES (1,?,?,?,?,0,1,1,1,?,1)',('Isolated Torznab','Torznab',json.dumps(settings),'TorznabSettings','[]'))
 c.execute('INSERT INTO DelayProfiles(Id,EnableUsenet,EnableTorrent,PreferredProtocol,UsenetDelay,TorrentDelay,\"Order\",Tags) VALUES (1,1,1,1,0,0,0,?)',('[]',))
 for k,v in [('EnableCompletedDownloadHandling','true'),('RssSyncInterval','0'),('EnableMediaInfo','true'),('WriteBookTags','0'),('CopyUsingHardlinks','false')]:c.execute('INSERT INTO Config(Key,Value) VALUES (?,?)',(k,v))
(p/'config/config.xml').write_text('<Config><Port>8789</Port><BindAddress>*</BindAddress><ApiKey>synthetic-e2e-admin-key-20261105</ApiKey><AuthenticationMethod>External</AuthenticationMethod><AuthenticationRequired>Enabled</AuthenticationRequired><LogLevel>debug</LogLevel><LaunchBrowser>False</LaunchBrowser><UpdateAutomatically>False</UpdateAutomatically></Config>')
# Original content only: a real EPUB container, never a placeholder renamed as media.
if not audio:
 name='Fixture Author - Fixture Ebook.epub';payload=p/'seed'/name
 with zipfile.ZipFile(payload,'w',compression=zipfile.ZIP_STORED) as z:
  z.writestr('mimetype','application/epub+zip');z.writestr('META-INF/container.xml','<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="EPUB/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>')
  z.writestr('EPUB/package.opf','<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" unique-identifier="bookid" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="bookid">urn:isbn:9780306406157</dc:identifier><dc:title>Fixture Ebook</dc:title><dc:creator>Fixture Author</dc:creator><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-05T00:00:00Z</meta></metadata><manifest><item id="text" href="text.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="text"/></spine></package>')
  z.writestr('EPUB/text.xhtml','<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>Fixture Ebook</title></head><body>'+''.join('<p>Original synthetic integration-test paragraph '+str(i)+'. No production media is used.</p>' for i in range(4000))+'</body></html>')
else:
 name='Fixture Author - Fixture Audiobook.mp3';payload=p/'seed'/name
 subprocess.run(['ffmpeg','-v','error','-f','lavfi','-i','sine=frequency=440:duration=20','-c:a','libmp3lame','-b:a','128k','-metadata','title=Fixture Audiobook','-metadata','album=Fixture Book','-metadata','artist=Fixture Author','-metadata','composer=A Narrator','-metadata','ASIN=B000TEST40',str(payload)],check=True)
def bencode(x):
 if isinstance(x,int):return b'i'+str(x).encode()+b'e'
 if isinstance(x,str):x=x.encode()
 if isinstance(x,bytes):return str(len(x)).encode()+b':'+x
 if isinstance(x,list):return b'l'+b''.join(map(bencode,x))+b'e'
 return b'd'+b''.join(bencode(k)+bencode(v) for k,v in sorted(x.items()))+b'e'
content=payload.read_bytes();piece=16384
# BitTorrent v1 wire-format piece/info hashes require SHA-1; never used as signatures.
# nosemgrep: python.lang.security.insecure-hash-algorithms.insecure-hash-algorithm-sha1
info={'name':name,'length':len(content),'piece length':piece,'pieces':b''.join(hashlib.sha1(content[n:n+piece], usedforsecurity=False).digest() for n in range(0,len(content),piece)),'private':1}
torrent=bencode({'info':info,'created by':'ChaptarrNG isolated acceptance'});(p/'provider/fixture.torrent').write_bytes(torrent)
# nosemgrep: python.lang.security.insecure-hash-algorithms.insecure-hash-algorithm-sha1
(p/'manifest.json').write_text(json.dumps({'mediaType':media_type,'bookId':30 if audio else 31,'editionId':40 if audio else 41,'editionTitle':'Fixture Audiobook' if audio else 'Fixture Ebook','profileIdSpace':profile_id_space,'qualityProfileId':(1 if audio else 2) if profile_id_space=='source' else (1 if audio else 4),'nativeQualityProfileId':1 if audio else 4,'metadataProfileId':1 if audio else 2,'category':3030 if audio else 7020,'releaseTitle':'Fixture Author - Fixture Audiobook [MP3]' if audio else 'Fixture Author - Fixture Ebook [EPUB]','filename':name,'size':len(content),'sha256':hashlib.sha256(content).hexdigest(),'infoHash':hashlib.sha1(bencode(info), usedforsecurity=False).hexdigest()},indent=2))
for kind,web,peer,path in [('seed',8081,6881,'/seed'),('download',8080,6882,'/downloads')]:
 (p/('qbit-'+kind)/'qBittorrent/config/qBittorrent.conf').write_text('[LegalNotice]\nAccepted=true\n[BitTorrent]\nSession\\DefaultSavePath='+path+'\nSession\\Port='+str(peer)+'\nSession\\DHTEnabled=false\nSession\\LSDEnabled=false\nSession\\PeXEnabled=false\n[Preferences]\nWebUI\\LocalHostAuth=false\nWebUI\\Address=127.0.0.1\nWebUI\\Port='+str(web)+'\n')
print('Isolated schema-158 source, provider/client settings, original EPUB and valid torrent prepared.')
