namespace NzbDrone.Core.ImportLists
{
    public interface IMediaTypeRootFolderSettings
    {
        string AudiobookRootFolderPath { get; set; }
        string EbookRootFolderPath { get; set; }
    }
}

