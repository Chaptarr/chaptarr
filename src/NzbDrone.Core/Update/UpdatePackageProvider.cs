using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Cloud;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Update
{
    public interface IUpdatePackageProvider
    {
        UpdatePackage GetLatestUpdate(string branch, Version currentVersion);
        List<UpdatePackage> GetRecentUpdates(string branch, Version currentVersion, Version previousVersion = null);
    }

    public class UpdatePackageProvider : IUpdatePackageProvider
    {
        private static readonly Regex Sha256Regex = new Regex(@"^[a-fA-F0-9]{64}$", RegexOptions.Compiled);

        private readonly IHttpClient _httpClient;
        private readonly IHttpRequestBuilderFactory _requestBuilder;
        private readonly IPlatformInfo _platformInfo;
        private readonly IMainDatabase _mainDatabase;
        private readonly Logger _logger;

        public UpdatePackageProvider(IHttpClient httpClient, IChaptarrCloudRequestBuilder requestBuilder, IPlatformInfo platformInfo, IMainDatabase mainDatabase, Logger logger)
        {
            _platformInfo = platformInfo;
            _requestBuilder = requestBuilder.Services;
            _httpClient = httpClient;
            _mainDatabase = mainDatabase;
            _logger = logger;
        }

        public UpdatePackage GetLatestUpdate(string branch, Version currentVersion)
        {
            var request = _requestBuilder.Create()
                                         .Resource("/update/{branch}")
                                         .AddQueryParam("version", currentVersion)
                                         .AddQueryParam("os", OsInfo.Os.ToString().ToLowerInvariant())
                                         .AddQueryParam("arch", RuntimeInformation.OSArchitecture)
                                         .AddQueryParam("runtime", "netcore")
                                         .AddQueryParam("runtimeVer", _platformInfo.Version)
                                         .AddQueryParam("dbType", _mainDatabase.DatabaseType)
                                         .AddQueryParam("includeMajorVersion", true)
                                         .SetSegment("branch", branch);

            var update = _httpClient.Get<UpdatePackageAvailable>(request.Build()).Resource;

            if (!update.Available)
            {
                return null;
            }

            var available = update.UpdatePackage;

            // Safety: the update server should only return newer versions, but guard anyway so health checks
            // and the installer never treat the current version (or older) as an update.
            if (available?.Version == null || available.Version <= currentVersion)
            {
                return null;
            }

            if (!IsVerifiedUpdatePackage(available))
            {
                _logger.Warn("Services update package {0} is missing required verification metadata. Skipping update.", available.Version);
                return null;
            }

            return available;
        }

        private static bool IsVerifiedUpdatePackage(UpdatePackage package)
        {
            if (package == null)
            {
                return false;
            }

            if (package.FileName == null || package.FileName.Trim().Length == 0)
            {
                return false;
            }

            if (package.Url == null || package.Url.Trim().Length == 0)
            {
                return false;
            }

            if (package.Hash == null || package.Hash.Trim().Length == 0)
            {
                return false;
            }

            return Sha256Regex.IsMatch(package.Hash.Trim());
        }

        public List<UpdatePackage> GetRecentUpdates(string branch, Version currentVersion, Version previousVersion)
        {
            var request = _requestBuilder.Create()
                                         .Resource("/update/{branch}/changes")
                                         .AddQueryParam("version", currentVersion)
                                         .AddQueryParam("os", OsInfo.Os.ToString().ToLowerInvariant())
                                         .AddQueryParam("arch", RuntimeInformation.OSArchitecture)
                                         .AddQueryParam("runtime", "netcore")
                                         .AddQueryParam("runtimeVer", _platformInfo.Version)
                                         .SetSegment("branch", branch);

            if (previousVersion != null && previousVersion != currentVersion)
            {
                request.AddQueryParam("prevVersion", previousVersion);
            }

            var updates = _httpClient.Get<List<UpdatePackage>>(request.Build());

            // Supplement with Chaptarr enhancements
            return SupplementWithChaptarrEnhancements(updates.Resource, GetChaptarrEnhancements(currentVersion), currentVersion);
        }

        // internal static for direct testing (InternalsVisibleTo Chaptarr.Core.Test).
        internal static List<UpdatePackage> SupplementWithChaptarrEnhancements(List<UpdatePackage> originalUpdates, List<UpdatePackage> chaptarrEnhancements, Version currentVersion)
        {
            if (originalUpdates == null || originalUpdates.Count == 0)
            {
                return chaptarrEnhancements;
            }

            // Server rows are the canonical changelog history. The in-binary enhancements only
            // fill the gap for the running version: when the server doesn't know it yet (manifest
            // lagging a fresh release) or knows it without notes. Appending them onto whatever
            // row happens to be first would duplicate bullets or file them under the wrong version.
            var installedUpdate = originalUpdates.FirstOrDefault(u => IsSameRelease(u.Version, currentVersion));

            if (installedUpdate == null)
            {
                originalUpdates.Add(chaptarrEnhancements[0]);
            }
            else if (!HasChanges(installedUpdate.Changes))
            {
                installedUpdate.Changes = chaptarrEnhancements[0].Changes;
            }

            return originalUpdates;
        }

        private static bool HasChanges(UpdateChanges changes)
        {
            if (changes == null)
            {
                return false;
            }

            return (changes.New?.Count ?? 0) > 0 || (changes.Fixed?.Count ?? 0) > 0;
        }

        // BuildInfo.Version is always 4-part (AssemblyVersion 0.9.X.0) while manifest version
        // strings may be 3-part; System.Version treats the missing component as -1, so plain
        // equality calls 0.9.578 and 0.9.578.0 different releases. Compare with -1 read as 0.
        private static bool IsSameRelease(Version left, Version right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            return left.Major == right.Major &&
                   left.Minor == right.Minor &&
                   Math.Max(left.Build, 0) == Math.Max(right.Build, 0) &&
                   Math.Max(left.Revision, 0) == Math.Max(right.Revision, 0);
        }

        private List<UpdatePackage> GetChaptarrEnhancements(Version currentVersion)
        {
            return new List<UpdatePackage>
            {
                new UpdatePackage
                {
                    Version = currentVersion,
                    ReleaseDate = DateTime.UtcNow,
                    Branch = "chaptarr",
                    Changes = new UpdateChanges
                    {
                        New = new List<string>
                        {
                            "Quality profiles can replace upgrades automatically or wait for you to confirm.",
                            "Author page Search groups results by book, best pick first. It has a Stop button, keeps running if you leave the page, holds results for 30 minutes and shows clickable progress in the sidebar. Search Monitored works through books one at a time.",
                            "Keep seeding permanently is now Preserve torrents (indexer setting). Chaptarr also turns off seeding limits in Transmission and in qBittorrent 4.1 to 4.5 and 4.6.3 or later, and ratio stop and remove in Deluge and rTorrent. Your Post-Import Category still applies. In Deluge, Chaptarr re-applies unlimited seeding after the label change, which can pause the torrent for a moment; System > Status warns if one doesn't resume, and torrents you paused yourself stay paused. Thanks @jberlyn and @EasyAsABC123.",
                            "Improved library search results, and adding an author follows your root folder settings more consistently.",
                            "Chaptarr and LazyLibrarian import lists now have separate audiobook and ebook settings, like Goodreads and Hardcover lists, and every list has new options to add missing authors or formats and to monitor paused authors with books on the list. List searches follow delay profiles, \"Keep listed books monitored\" explains that each sync turns monitoring back on, and a list warns when no format is enabled or a Goodreads fetch fails. Thanks @jordanfelle and @Josh-Archer.",
                            "New naming tokens: {Smart Narrator}, {First Narrator} and {FullCast}. {Smart Narrator} uses the first value that applies, in your chosen order; by default the production label (GraphicAudio or Dramatized), then Full Cast for three or more narrators, then the narrator names. Formats with stray braces, or multi-part formats that would give every part the same name, can't be saved, and ebook naming previews show ebook details.",
                            "\"Library covers only\" (Settings > Media Management) keeps covers just for monitored books and books with files, and removes the rest when you save and during regular housekeeping. Use Calculate savings to see how much space you'll save first.",
                            "PostgreSQL backups (work in progress) include the database; restores apply at the next start and roll back if startup fails, and failed scheduled backups show in System > Status. They need matching pg_dump and pg_restore versions; back up before the first start. Settings Backup now covers delay and release profiles, quality sizes, and indexer, client and interface options; Overwrite lists everything it will delete first, and Merge only adds and fills blanks. Restoring a backup warns that it replaces your settings, including the API key, and that it clears pending import work. Thanks @Josh-Archer.",
                            "Multiple improvements for mobile users: the format switch has its own row, tap targets are larger, the author's book table now shows Narrator, Position and Rating on phones, and toolbar buttons that don't fit go under More. Mobile support is still a work in progress. Thanks @jberlyn and @bhoffman20, and @nmbrg and @plz12345 for bringing awareness.",
                            "Changed the messy narrator icon to the simple microphone.",
                            "Indexer Test checks NFO support and greys out Fetch Narrator Metadata when the indexer lacks it. Searches for books with a pinned narrator put the narrator first.",
                            "Wanted > Author Requests shows queued author adds and marks any that couldn't be added. Books has an In Library filter.",
                            "When a download also contains the other format of the book you grabbed, Chaptarr also tries to import it, as long as that format is monitored for the author and the book, the book has no files or other download in progress, and it passes your quality profile and custom formats, all checked before anything is converted. If it can't, the book you grabbed still completes and History says why; if it imports, History shows it under the original grab.",
                            "Searching more than five books at once from Wanted, Calendar, Books or an author asks first and says how many will run. Retry Match on more than five books asks too."
                        },
                        Fixed = new List<string>
                        {
                            "Better protection for your recordings (#286). A hand-picked grab keeps both files when upgrades are off and never replaces a file with a worse one. An automatic import that would replace your file with a different recording type keeps your files and waits for you to confirm in Manual Import. Manual Import shows and sets the recording type, and choosing an audio edition in Edit Book with Automatically Switch Edition off sets it from that edition (three or more narrators count as Full Cast); multi-part replacements keep the old recording until every part arrives, and Replace lists what it will delete. Thanks @SaxxyToo, @jordanfelle and @sebclark.",
                            "Author delete can remove audiobooks, ebooks or both, and first shows each folder it will delete with its book files, size and other files. Root folders, unavailable roots and folders outside your roots are refused, a folder holding another author's files needs an extra confirmation, and deleting one format keeps the other format's files. Bulk delete can add a list exclusion. Removing a root folder leaves your files on disk, and deleting one Calibre format removes only that format. Configure Chaptarr's Recycling Bin (Settings > Media Management) if you'd like, but always keep backups of data you can't afford to lose. Thanks @benjitobz (PR #134), @jordanfelle and @digitalgp.",
                            "Downloads still arriving are rechecked until they finish instead of going to Manual Import after a few tries (#319), and a download counts as imported only when every file in the grabbed format is in (#254); ._ files, Thumbs.db and .DS_Store no longer hold it up. A download that doesn't contain what was grabbed stops with a message saying what arrived and isn't failed, blocklisted, re-searched or deleted, and one identical to files you already have stops with \"Already in your library\". Removing a pending release blocklists it only if you choose; pending releases keep their books across restarts, and ones Chaptarr can't place show in the Queue, which also loads faster (#165). Thanks @Randrage, @BassMatt and @jordanfelle.",
                            "Manual Import skips a file that vanished instead of aborting (#225). Thanks @jordanfelle.",
                            "Manual Import now imports the files you pick as they are. To convert a blocked download to M4B, select it in Activity > Queue and use Retry Imports.",
                            "Release matching: a better-fitting book by the same author wins, narrator and series names no longer stand in for the author, and a release isn't mistaken for a pack when its series positions match the book. A track number in a quoted audio file name no longer blocks a match when the title also appears outside the quotes, while a number that points to another book in the series still does. Numbers match whether written as 2 or II, ½ is understood, and number words and a leading The, A or An are recognized for English editions. Emoji no longer break release processing (#116), accented and non-Latin names match better, and file size is the last tie-breaker, with small differences ignored. File import handles split \"Title: Subtitle\" tags, [Kindle Edition] and {Unabridged}, and prefers the monitored edition when an ebook matches two equally. Thanks @jonnotk, @jbob06 (PR #117) and tabby.",
                            "Place eBooks with Audiobooks no longer moves or renames an ebook when there's no shared destination (#126). Thanks @jbob06 (PR #127).",
                            "Sync Monitored also applies after a first scan and to single-book requests, and retries within minutes if it fails. Refresh is faster, failed author refreshes retry within minutes (an hour after a rate limit, a not-found or a local error; an author the metadata server refuses outright waits for the next scheduled refresh), and a metadata server outage no longer counts against the author. Thanks @jordanfelle (PR #174) and @JordanFromIT.",
                            "Series labels, folder tokens and Calibre tags use Goodreads series in a consistent order, {Book SeriesPosition:00} zero-pads series numbers (#72), refreshing one author no longer strips another author's shared-series books, and the Series tab's position sort follows the direction you choose; books without a number come after numbered ones in ascending order. Series changes on the metadata server are still being rolled out and propagated, so series will keep improving as your authors refresh. Thanks @tillkruss, @davidgibbons, @Yoldark34 and @the-wondersmith.",
                            "A notification set only for On Upgrade now sends (#150), a blank Discord username uses the webhook's name, and Kavita and Synology get the right folder. Thanks @jordanfelle (PR #151) and @rukuh.",
                            "M4B conversion keeps provider chapters on shares without symlinks, a multi-file conversion checks where the merged file will go before converting (#284), quality and custom formats are checked against the download before converting so a book is never refused because conversion made it smaller, and the conversion queue no longer crashes (#139). Further tweaks to the conversion process. Thanks @tillkruss (PR #316), @bhoffman20 (PR #287), @ArtyParty0848 and @the-wondersmith.",
                            "Root folder profiles save (#279), new or cloned profiles appear without reloading (#329), Add Series no longer crashes (#317), and Edit Author's audiobook and ebook folder fields show and save the stored folders and ask whether to move the files (#28, #332). Thanks @beardedslav (PR #280), @texas1880, @zenermerps, @elric667, @waden34, @paulrouleau and @sebclark.",
                            "Generic Books indexers are searched for audiobooks too, searching an author searches each monitored book in its own format's categories (#325), indexer Test refreshes categories, the log explains why a search was skipped, MyAnonaMouse search behavior is improved and its errors show as failures, and when Interactive Search finds nothing, it says whether indexers failed. Thanks @jschuttler-zylio and @zenermerps.",
                            "Narrator lookups run only for releases that match the book and author, and preferred narrators come from audio editions only. GraphicAudio is shown only for GraphicAudio productions, other dramatized recordings are labeled Full Cast or Dramatized, \"Full Cast\" credits aren't treated as people, and Audible search keeps the real narrator names; production labels are recognized in English for now. File import picks the right recording when a book has solo and full-cast editions: one matching narrator plus a matching length is enough, and the edition that accounts for the file's narrators wins. Thanks @JordanFromIT.",
                            "Interactive Search titles no longer get squeezed (#69, #297), Enter confirms a dialog once, Space toggles checkboxes, a book's links open the right format, and author photos show once downloaded, your picks stick and failures show the placeholder. Thanks @brt0wn, @jpdexter101-lang and @jordanfelle.",
                            "System > Updates says when it can't reach the update server instead of reporting you're up to date, and a manual update check installs updates.",
                            "Adding an author through the API without a format now uses the root folder's or quality profile's format instead of setting up both, and asks when neither says. Deleting an author that has both formats through the API asks which one, or both.",
                            "Book lookups from apps connected to Chaptarr's ebook address return ebook results. Thanks @kukitch (#67) and @snapetech (PR #114).",
                            "Security: a new Allowed Hosts setting (Settings > General > Host) is required when login is disabled for local addresses, and that option now also checks every client address your proxy reports. Login redirects stay on the address you used. Setting up a login for the first time creates a new API key; update it in connected apps. The API can no longer switch login to None or External. Thanks to Nurjaman Shaikh for reporting it.",
                            "Import lists: list searches and queued author requests respect a format you've paused for an author (Monitor Author's Audiobooks or Monitor Author's eBooks off) unless the list is set to monitor its authors, a specific-book list with search on no longer monitors the new author's whole catalog after the first scan (#159), and a list can add the missing format to an author you already have (#186). Adding one format clears only that format's list exclusion, authors with exclusions in both formats no longer fail to add, and queued list requests respect exclusions. A search you start yourself still runs. Thanks @jbob06, @foinse, @PACE-1 and @iain247.",
                            "Saving an indexer, download client, import list or notification without changes no longer re-tests it or starts a list sync. Releases Chaptarr can't classify show as Unknown instead of Unknown Text and are no longer assumed to be ebooks. Wanted > Cutoff Unmet counts and lists each book once instead of once per file.",
                            "When the metadata server merges two authors or splits one, your library follows it and History notes the change, and merging two copies of an author no longer fails on a shared Audible ID (#49). Thanks @digitalgp and @sebclark."
                            }
                    }
                }
            };
        }
    }
}
