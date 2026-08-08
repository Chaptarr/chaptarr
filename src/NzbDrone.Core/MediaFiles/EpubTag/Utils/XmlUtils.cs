using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace VersOne.Epub.Internal
{
    public static class XmlUtils
    {
        public static async Task<XDocument> LoadDocumentAsync(Stream stream)
        {
            using (var memoryStream = new MemoryStream())
            {
                await stream.CopyToAsync(memoryStream).ConfigureAwait(false);
                var settings = CreateSafeXmlReaderSettings(async: false);

                return await Task.Run(() => LoadXDocument(memoryStream, settings)).ConfigureAwait(false);
            }
        }

        private static XDocument LoadXDocument(MemoryStream memoryStream, XmlReaderSettings settings)
        {
            try
            {
                memoryStream.Position = 0;
                using (var xmlReader = XmlReader.Create(memoryStream, settings))
                {
                    return XDocument.Load(xmlReader);
                }
            }
            catch (XmlException)
            {
                // .NET can't handle XML 1.1, so try sanitising and reading as 1.0
                memoryStream.Position = 0;
                using (var sr = new StreamReader(memoryStream, leaveOpen: true))
                {
                    var text = sr.ReadToEnd();

                    if (text.StartsWith(@"<?xml version=""1.1"""))
                    {
                        text = @"<?xml version=""1.0""" + text.Substring(19);

                        var chars = text.Where(x => XmlConvert.IsXmlChar(x)).ToArray();
                        var sanitised = new string(chars);

                        using (var sanitisedReader = XmlReader.Create(new StringReader(sanitised), CreateSafeXmlReaderSettings(async: false)))
                        {
                            return XDocument.Load(sanitisedReader);
                        }
                    }
                }

                throw;
            }
        }

        private static XmlReaderSettings CreateSafeXmlReaderSettings(bool async)
        {
            return new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                Async = async
            };
        }
    }
}
