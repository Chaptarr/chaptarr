namespace NzbDrone.Core.Books
{
    // Chaptarr's normalized edition codes. Provider-specific intake codes are mapped separately.
    public static class EditionReadingFormat
    {
        public const int Unknown = 0;
        public const int Print = 1;
        public const int Audiobook = 2;
        public const int Ebook = 3;

        public static bool IsAudiobook(int? readingFormatId) => readingFormatId == Audiobook;

        public static int? NativeFor(BookMediaType? mediaType) => mediaType switch
        {
            BookMediaType.Audiobook => Audiobook,
            BookMediaType.Ebook => Ebook,
            _ => null
        };

        public static int RepresentativeRank(int? readingFormatId, BookMediaType mediaType) =>
            readingFormatId == Ebook && mediaType == BookMediaType.Audiobook ? 2 :
            readingFormatId == Print ? 1 : 0;
    }
}
