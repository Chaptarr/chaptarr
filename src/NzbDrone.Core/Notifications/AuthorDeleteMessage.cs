using NzbDrone.Core.Books;

namespace NzbDrone.Core.Notifications
{
    public class AuthorDeleteMessage
    {
        public string Message { get; set; }
        public Author Author { get; set; }
        public bool DeletedFiles { get; set; }
        public string DeletedFilesMessage { get; set; }

        public override string ToString()
        {
            return Message;
        }

        public AuthorDeleteMessage(Author author, bool deleteFiles, NotificationText text = null)
        {
            Author = author;
            DeletedFiles = deleteFiles;
            text ??= new NotificationText(null);
            DeletedFilesMessage = DeletedFiles ?
                text.Get("notification.body.authorRemovedWithFiles", "Author removed and all files were deleted") :
                text.Get("notification.body.authorRemovedWithoutFiles", "Author removed, files were not deleted");
            Message = author.Name + " - " + DeletedFilesMessage;
        }
    }
}
