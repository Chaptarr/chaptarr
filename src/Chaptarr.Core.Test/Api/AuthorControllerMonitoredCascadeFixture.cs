using Chaptarr.Api.V1.Author;
using Chaptarr.Http.Middleware;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorControllerMonitoredCascadeFixture
    {
        // AuthorController.CascadeExplicitMonitoredIntoMediaTypeSettings and StoredAuthorMonitoringState
        // are internal (not public) but have no dependency on controller state, so they're called
        // directly here - via [assembly: InternalsVisibleTo("Chaptarr.Core.Test")] on Chaptarr.Api.V1 -
        // rather than standing up the full controller (20+ constructor dependencies unrelated to this
        // logic).
        private static void Cascade(
            AuthorResource resource,
            Author model,
            string storedAudiobookRootFolderPath,
            string storedEbookRootFolderPath,
            bool wasMonitoredFromMediaSettings,
            int? storedAudiobookMonitorExisting = null,
            bool? storedAudiobookMonitorFuture = null,
            int? storedEbookMonitorExisting = null,
            bool? storedEbookMonitorFuture = null,
            ReadarrFacadeContext facadeContext = null)
        {
            var stored = new AuthorController.StoredAuthorMonitoringState(
                storedAudiobookRootFolderPath,
                storedEbookRootFolderPath,
                storedAudiobookMonitorExisting,
                storedAudiobookMonitorFuture,
                storedEbookMonitorExisting,
                storedEbookMonitorFuture,
                wasMonitoredFromMediaSettings);

            AuthorController.CascadeExplicitMonitoredIntoMediaTypeSettings(resource, model, facadeContext, stored);
        }

        private static Author FullyConfiguredMonitoredAuthor()
        {
            return new Author
            {
                AudiobookRootFolderPath = @"C:\audiobooks",
                AudiobookMonitorExisting = 1,
                AudiobookMonitorFuture = true,
                EbookRootFolderPath = @"C:\ebooks",
                EbookMonitorExisting = 2,
                EbookMonitorFuture = true
            };
        }

        [Test]
        public void should_cascade_explicit_unmonitor_into_both_media_types_when_tri_state_untouched()
        {
            var model = FullyConfiguredMonitoredAuthor();
            var resource = new AuthorResource { Monitored = false };

            Cascade(resource, model, @"C:\audiobooks", @"C:\ebooks", wasMonitoredFromMediaSettings: true,
                storedAudiobookMonitorExisting: 1, storedAudiobookMonitorFuture: true,
                storedEbookMonitorExisting: 2, storedEbookMonitorFuture: true);

            Assert.Multiple(() =>
            {
                Assert.That(model.AudiobookMonitorFuture, Is.False);
                Assert.That(model.AudiobookMonitorExisting, Is.EqualTo(0));
                Assert.That(model.EbookMonitorFuture, Is.False);
                Assert.That(model.EbookMonitorExisting, Is.EqualTo(0));
            });
        }

        [Test]
        public void should_unmonitor_when_client_echoes_unchanged_tri_state_fields_from_a_prior_get()
        {
            // The gap an earlier version of this fix had: a client that does GET -> flip `monitored`
            // -> PUT the whole object back (arguably the most common *arr API usage pattern, and
            // exactly how issue #17's own repro style reads) sends the four tri-state fields back
            // unchanged. Treating their mere presence as "the client edited this" - which an earlier
            // version of this cascade did - made the cascade skip every media type, silently
            // reproducing the original bug for this exact client shape. It has to fire here too.
            var model = FullyConfiguredMonitoredAuthor();
            var resource = new AuthorResource
            {
                Monitored = false,
                AudiobookMonitorExisting = 1, // echoed back unchanged from the prior GET
                AudiobookMonitorFuture = true,
                EbookMonitorExisting = 2,
                EbookMonitorFuture = true
            };

            Cascade(resource, model, @"C:\audiobooks", @"C:\ebooks", wasMonitoredFromMediaSettings: true,
                storedAudiobookMonitorExisting: 1, storedAudiobookMonitorFuture: true,
                storedEbookMonitorExisting: 2, storedEbookMonitorFuture: true);

            Assert.Multiple(() =>
            {
                Assert.That(model.AudiobookMonitorFuture, Is.False);
                Assert.That(model.AudiobookMonitorExisting, Is.EqualTo(0));
                Assert.That(model.EbookMonitorFuture, Is.False);
                Assert.That(model.EbookMonitorExisting, Is.EqualTo(0));
            });
        }

        [Test]
        public void should_cascade_explicit_monitor_true_without_touching_existing()
        {
            var model = new Author { AudiobookMonitorExisting = 0, AudiobookMonitorFuture = false };
            var resource = new AuthorResource { Monitored = true };

            Cascade(resource, model, @"C:\audiobooks", null, wasMonitoredFromMediaSettings: false,
                storedAudiobookMonitorExisting: 0, storedAudiobookMonitorFuture: false);

            Assert.Multiple(() =>
            {
                Assert.That(model.AudiobookMonitorFuture, Is.True);
                Assert.That(model.AudiobookMonitorExisting, Is.EqualTo(0), "MonitorFuture=true alone is enough to make the author monitored; MonitorExisting is a separate, unrelated choice this request never asked to change");
            });
        }

        [Test]
        public void should_be_a_no_op_when_the_requested_value_already_matches_current_truth()
        {
            // The critical case: a client (Chaptarr's own UI toggle included) that spreads the last
            // GET response onto a PUT sends back a `monitored` that already matches reality whenever
            // its edit was to something else entirely. Nothing should move here.
            var model = new Author { AudiobookMonitorExisting = 2, AudiobookMonitorFuture = false };
            var resource = new AuthorResource { Monitored = true };

            Cascade(resource, model, @"C:\audiobooks", null, wasMonitoredFromMediaSettings: true,
                storedAudiobookMonitorExisting: 2, storedAudiobookMonitorFuture: false);

            Assert.Multiple(() =>
            {
                Assert.That(model.AudiobookMonitorFuture, Is.False, "unchanged - nothing was actually requested here");
                Assert.That(model.AudiobookMonitorExisting, Is.EqualTo(2), "an already-configured Selected(2) must not be silently promoted to All(1)");
            });
        }

        [Test]
        public void should_only_touch_media_types_the_author_is_configured_for()
        {
            var model = new Author { AudiobookMonitorExisting = 1, AudiobookMonitorFuture = true };
            var resource = new AuthorResource { Monitored = false };

            Cascade(resource, model, @"C:\audiobooks", storedEbookRootFolderPath: null, wasMonitoredFromMediaSettings: true,
                storedAudiobookMonitorExisting: 1, storedAudiobookMonitorFuture: true);

            Assert.Multiple(() =>
            {
                Assert.That(model.AudiobookMonitorFuture, Is.False);
                Assert.That(model.EbookMonitorFuture, Is.Null);
                Assert.That(model.EbookMonitorExisting, Is.Null);
            });
        }

        [Test]
        public void should_use_the_stored_root_folder_paths_not_whatever_is_currently_on_model()
        {
            // Regression case: ToModel/ApplyChanges run BEFORE this cascade and, for a PUT that omits
            // AudiobookRootFolderPath (whatever else it does or doesn't include), will already have
            // overwritten model's path with the (null) value off the request. The caller is
            // responsible for passing the paths captured from the STORED author before ToModel ran -
            // this test locks in that this method uses whatever it's handed, not
            // model.AudiobookRootFolderPath, so that contract can't silently regress back to reading
            // the wrong (post-mutation) value.
            var model = new Author
            {
                AudiobookRootFolderPath = null, // as it would be after ToModel on a request that omits this field
                AudiobookMonitorExisting = 1,
                AudiobookMonitorFuture = true
            };
            var resource = new AuthorResource { Monitored = false };

            Cascade(resource, model, storedAudiobookRootFolderPath: @"C:\audiobooks", storedEbookRootFolderPath: null, wasMonitoredFromMediaSettings: true,
                storedAudiobookMonitorExisting: 1, storedAudiobookMonitorFuture: true);

            Assert.That(model.AudiobookMonitorFuture, Is.False, "must cascade using the stored path, not model's (already-nulled) path");
        }

        [Test]
        public void should_not_cascade_a_media_type_the_client_genuinely_changed()
        {
            // model reflects the state AFTER ApplyChanges has already applied the client's edit
            // (AudiobookMonitorFuture: false -> true); `stored` reflects what was there BEFORE this
            // request. The two differing is what makes this a genuine edit, not an echo.
            var model = new Author
            {
                AudiobookMonitorExisting = 1,
                AudiobookMonitorFuture = true, // just changed by the client's explicit edit
                EbookMonitorExisting = 2,
                EbookMonitorFuture = true
            };
            var resource = new AuthorResource { Monitored = false, AudiobookMonitorFuture = true };

            Cascade(resource, model, @"C:\audiobooks", @"C:\ebooks", wasMonitoredFromMediaSettings: true,
                storedAudiobookMonitorExisting: 1, storedAudiobookMonitorFuture: false, // was false before this request
                storedEbookMonitorExisting: 2, storedEbookMonitorFuture: true);

            Assert.Multiple(() =>
            {
                Assert.That(model.AudiobookMonitorFuture, Is.True, "client's genuine edit should win, not be overridden by the legacy flag");
                Assert.That(model.AudiobookMonitorExisting, Is.EqualTo(1));
                Assert.That(model.EbookMonitorFuture, Is.False, "ebook wasn't touched by the client, so the legacy flag should still cascade into it");
                Assert.That(model.EbookMonitorExisting, Is.EqualTo(0));
            });
        }

        [Test]
        public void should_not_leak_a_stale_echoed_monitored_into_an_untouched_media_type_edited_elsewhere()
        {
            // The exact shape of Chaptarr's own UI toggle: the user turns audiobook monitoring off
            // (touching audiobook's own tri-state fields directly, a genuine change from stored), and
            // the full-object PUT body carries `monitored: true` unchanged from the last GET, since
            // the author was (still is, per the pre-request derived value) monitored via audiobook at
            // the time of that GET. Ebook has never been configured (null tri-state) but does have a
            // root folder. Ebook must stay exactly as it was - it must NOT get silently turned on
            // because a field elsewhere in the same request happened to still read "true".
            var model = new Author
            {
                AudiobookRootFolderPath = @"C:\audiobooks",
                AudiobookMonitorExisting = 0, // just written by ApplyChanges from the client's genuine edit
                AudiobookMonitorFuture = true, // untouched by the client, preserved from storage
                EbookRootFolderPath = @"C:\ebooks",
                EbookMonitorExisting = null,
                EbookMonitorFuture = null
            };
            var resource = new AuthorResource { Monitored = true, AudiobookMonitorExisting = 0 };

            Cascade(resource, model, @"C:\audiobooks", @"C:\ebooks", wasMonitoredFromMediaSettings: true,
                storedAudiobookMonitorExisting: 1, storedAudiobookMonitorFuture: true); // was 1/All before this request

            Assert.Multiple(() =>
            {
                Assert.That(model.AudiobookMonitorExisting, Is.EqualTo(0), "the client's own genuine edit, left untouched by this cascade");
                Assert.That(model.EbookMonitorFuture, Is.Null, "must not be materialized just because the stale echoed flag happens to still read true");
                Assert.That(model.EbookMonitorExisting, Is.Null);
            });
        }

        [Test]
        public void should_cascade_a_newly_added_root_folder_media_type_even_when_top_level_flag_is_unchanged()
        {
            // Same PUT both assigns ebook a root folder for the first time AND asks for monitored:false.
            // wasMonitoredFromMediaSettings is already false (nothing was monitored before this request),
            // so the top-level flag isn't "changing" by the simple equality check - but AuthorService's
            // root-folder-defaults fill is about to see a media type with a root folder and null tri-state
            // fields for the first time and resolve them from the root folder's own defaults, which could
            // silently re-enable monitoring right after this explicit false. Forcing explicit values here
            // pre-empts that.
            var model = new Author
            {
                EbookRootFolderPath = @"C:\ebooks", // just assigned by this same PUT
                EbookMonitorExisting = null,
                EbookMonitorFuture = null
            };
            var resource = new AuthorResource { Monitored = false };

            Cascade(resource, model, storedAudiobookRootFolderPath: null, storedEbookRootFolderPath: null, wasMonitoredFromMediaSettings: false);

            Assert.Multiple(() =>
            {
                Assert.That(model.EbookMonitorFuture, Is.False);
                Assert.That(model.EbookMonitorExisting, Is.EqualTo(0));
            });
        }

        [Test]
        public void should_leave_a_long_configured_but_never_set_media_type_alone_when_nothing_actually_changed()
        {
            // Deliberately the mirror image of the previous test: ebook has HAD a root folder all
            // along (not newly assigned by this request) and its tri-state fields are still null, but
            // the top-level flag isn't actually changing either. This PUT isn't asking for anything
            // regarding ebook, so it's left for AuthorService's normal (unrelated) first-time
            // root-folder-defaults resolution to decide, same as it would if this cascade didn't exist.
            var model = new Author
            {
                EbookRootFolderPath = @"C:\ebooks",
                EbookMonitorExisting = null,
                EbookMonitorFuture = null
            };
            var resource = new AuthorResource { Monitored = false };

            Cascade(resource, model, storedAudiobookRootFolderPath: null, storedEbookRootFolderPath: @"C:\ebooks", wasMonitoredFromMediaSettings: false);

            Assert.Multiple(() =>
            {
                Assert.That(model.EbookMonitorFuture, Is.Null);
                Assert.That(model.EbookMonitorExisting, Is.Null);
            });
        }

        [Test]
        public void should_do_nothing_when_monitored_was_omitted()
        {
            var model = FullyConfiguredMonitoredAuthor();
            var resource = new AuthorResource { Monitored = null };

            Cascade(resource, model, @"C:\audiobooks", @"C:\ebooks", wasMonitoredFromMediaSettings: true,
                storedAudiobookMonitorExisting: 1, storedAudiobookMonitorFuture: true,
                storedEbookMonitorExisting: 2, storedEbookMonitorFuture: true);

            Assert.Multiple(() =>
            {
                Assert.That(model.AudiobookMonitorFuture, Is.True);
                Assert.That(model.AudiobookMonitorExisting, Is.EqualTo(1));
                Assert.That(model.EbookMonitorFuture, Is.True);
                Assert.That(model.EbookMonitorExisting, Is.EqualTo(2));
            });
        }

        [Test]
        public void should_not_cascade_under_readarr_facade_context()
        {
            var model = FullyConfiguredMonitoredAuthor();
            var resource = new AuthorResource { Monitored = false };
            var facadeContext = new ReadarrFacadeContext("gr", "audiobook", "readarr");

            Cascade(resource, model, @"C:\audiobooks", @"C:\ebooks", wasMonitoredFromMediaSettings: true,
                storedAudiobookMonitorExisting: 1, storedAudiobookMonitorFuture: true,
                storedEbookMonitorExisting: 2, storedEbookMonitorFuture: true,
                facadeContext: facadeContext);

            Assert.That(model.AudiobookMonitorFuture, Is.True, "facade requests already get single-media-type cascade from ToModel; this helper should stay out of the way");
        }

        // End-to-end seam test wiring the real AuthorResource.ToModel(author, facadeContext) call
        // together with the cascade, using the exact "snapshot stored state before ToModel mutates the
        // author" sequence AuthorController.UpdateAuthor follows. This is the case that would have
        // caught reading paths off the (already-mutated) model instead of the pre-mutation snapshot.
        // The request body below deliberately still omits AudiobookRootFolderPath/EbookRootFolderPath/
        // RootFolderPath and all four tri-state fields - the minimal shape a client would send to
        // "just" flip the legacy on/off switch - while including Path and AudiobookQualityProfileId,
        // since AuthorController.UpdateAuthor's own PutValidator/SharedValidator rules reject Path
        // being absent and reject neither quality profile being set, on every PUT, unconditionally; a
        // body missing those two fields would never reach this code in the real endpoint, whatever
        // else it carries. ToModel's native (non-facade) branch nulls AudiobookRootFolderPath out on
        // exactly this shape (it falls back through the also-empty legacy RootFolderPath field), so
        // this only passes if the cascade is driven by the pre-ToModel snapshot rather than model's
        // post-ToModel state.
        [Test]
        public void end_to_end_a_minimal_put_body_still_unmonitors_a_configured_author()
        {
            var storedAuthor = new Author
            {
                Id = 42,
                Path = @"C:\audiobooks\Some Author",
                AudiobookQualityProfileId = 1,
                AudiobookRootFolderPath = @"C:\audiobooks",
                AudiobookMonitorExisting = 1,
                AudiobookMonitorFuture = true,
                EbookRootFolderPath = @"C:\ebooks",
                EbookMonitorExisting = 1,
                EbookMonitorFuture = true
            };

            // What a client sends when it only means to flip the legacy on/off switch, but still has
            // to satisfy the endpoint's other required fields to get this far at all.
            var requestResource = new AuthorResource
            {
                Id = 42,
                Path = @"C:\audiobooks\Some Author",
                AudiobookQualityProfileId = 1,
                Monitored = false
            };

            // Mirrors AuthorController.UpdateAuthor exactly: capture, then mutate.
            var stored = AuthorController.StoredAuthorMonitoringState.Capture(storedAuthor);
            var model = requestResource.ToModel(storedAuthor, null);

            // Confirms the premise this test exists to guard: ToModel really does null the path out on
            // this shape of body, so a cascade reading paths off `model` (rather than the pre-call
            // snapshot) would see nothing configured and silently do nothing - the exact v1 regression.
            Assert.That(model.AudiobookRootFolderPath, Is.Null, "sanity check on ToModel's own behavior for this request shape - if this ever stops being null, this test stops proving anything");

            AuthorController.CascadeExplicitMonitoredIntoMediaTypeSettings(requestResource, model, null, stored);

            Assert.Multiple(() =>
            {
                Assert.That(model.AudiobookMonitorFuture, Is.False);
                Assert.That(model.AudiobookMonitorExisting, Is.EqualTo(0));
                Assert.That(model.EbookMonitorFuture, Is.False);
                Assert.That(model.EbookMonitorExisting, Is.EqualTo(0));
                Assert.That(model.IsMonitoredFromMediaSettings(), Is.False, "this is what AuthorService.UpdateAuthor's recompute reads - it must land on false");
            });
        }
    }
}
