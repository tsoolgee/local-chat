using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace LocalChat
{
    /// Text out of attached documents. PDF is read in the page (pdf.js); Word files are read here.
    static class Documents
    {
        /// Plain text of a Word (.docx) file: paragraphs become lines.
        public static string DocxText(Stream data)
        {
            using (var zip = new ZipArchive(data, ZipArchiveMode.Read))
            {
                var entry = zip.GetEntry("word/document.xml") ?? throw new Exception("זה לא נראה כמו קובץ Word תקין");
                var sb = new StringBuilder();
                using (var r = XmlReader.Create(entry.Open()))
                {
                    while (r.Read())
                    {
                        if (r.NodeType == XmlNodeType.Element)
                        {
                            if (r.LocalName == "t") sb.Append(r.ReadElementContentAsString());
                            else if (r.LocalName == "tab") sb.Append('\t');
                            else if (r.LocalName == "br" || r.LocalName == "cr") sb.Append('\n');
                        }
                        else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "p") sb.Append('\n');
                    }
                }
                return sb.ToString().Trim();
            }
        }
    }
}
