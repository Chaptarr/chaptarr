namespace Chaptarr.Api.V1.Books
{
    public class AddWantedEditionRequest
    {
        public int EditionId { get; set; }

        // Whether to start an automatic search for the newly created wanted edition.
        // Defaults to false; the UI exposes this as an opt-in toggle.
        public bool SearchForNewBook { get; set; } = false;
    }
}
