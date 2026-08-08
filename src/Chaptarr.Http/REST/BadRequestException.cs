using System.Net;
using Chaptarr.Http.Exceptions;

namespace Chaptarr.Http.REST
{
    public class BadRequestException : ApiException
    {
        public BadRequestException(object content = null)
            : base(HttpStatusCode.BadRequest, content)
        {
        }
    }
}
